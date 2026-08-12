package com.codexmicro.mobile.security

import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import java.net.IDN
import java.net.InetAddress
import javax.net.ssl.X509TrustManager

class SpkiPinningTrustManager(pin: String, private val expectedHost: String) : X509TrustManager {
    private val expectedPin = decodePin(pin)

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("Client certificate validation is not supported")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("Server certificate is missing")
        leaf.checkValidity()
        val actual = MessageDigest.getInstance("SHA-256").digest(leaf.publicKey.encoded)
        if (!MessageDigest.isEqual(expectedPin, actual)) {
            throw CertificateException("Paired device identity does not match")
        }
        if (!matchesSubjectAlternativeName(expectedHost, leaf.subjectAlternativeNames)) {
            throw CertificateException("Server certificate does not contain the paired host")
        }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    companion object {
        fun canonicalPin(pin: String): String = Base64.getEncoder().withoutPadding()
            .encodeToString(decodePin(pin))

        fun matchesPin(pin: String, publicKeySpki: ByteArray): Boolean = MessageDigest.isEqual(
            decodePin(pin),
            MessageDigest.getInstance("SHA-256").digest(publicKeySpki),
        )

        fun matchesSubjectAlternativeName(host: String, names: Collection<List<*>>?): Boolean {
            val expected = host.trim().removePrefix("[").removeSuffix("]")
            val isIp = expected.contains(':') || expected.matches(Regex("^[0-9]{1,3}(\\.[0-9]{1,3}){3}$"))
            return names.orEmpty().any { entry ->
                if (entry.size < 2) return@any false
                val type = entry[0] as? Int ?: return@any false
                val value = entry[1] as? String ?: return@any false
                when {
                    isIp && type == 7 -> runCatching {
                        InetAddress.getByName(expected).address.contentEquals(InetAddress.getByName(value).address)
                    }.getOrDefault(false)
                    !isIp && type == 2 && !value.contains('*') ->
                        IDN.toASCII(value).equals(IDN.toASCII(expected), ignoreCase = true)
                    else -> false
                }
            }
        }

        private fun decodePin(raw: String): ByteArray {
            val value = raw.trim().removePrefix("sha256/").removePrefix("SHA256/").replace(":", "")
            val isHex = value.length == 64 && value.all { it.isDigit() || it.lowercaseChar() in 'a'..'f' }
            val bytes = if (isHex) {
                value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
            } else runCatching { Base64.getDecoder().decode(value) }
                .recoverCatching { Base64.getUrlDecoder().decode(value + "=".repeat((4 - value.length % 4) % 4)) }
                .getOrElse { throw IllegalArgumentException("SPKI pin must be SHA-256 base64 or hex", it) }
            require(bytes.size == 32) { "SPKI pin must contain 32 bytes" }
            return bytes
        }
    }
}
