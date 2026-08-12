package com.codexmicro.mobile.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.util.Base64

class PairingKeyStore {
    fun publicKeySpkiBase64(): String = Base64.getEncoder()
        .encodeToString(loadOrCreate().public.encoded)

    fun signChallenge(challenge: ByteArray): String {
        require(challenge.size in 16..4096) { "Challenge length is invalid" }
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(loadOrCreate().private)
        signature.update(challenge)
        return Base64.getEncoder().withoutPadding().encodeToString(signature.sign())
    }

    fun signDerBase64Url(payload: ByteArray): String {
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(loadOrCreate().private)
        signature.update(payload)
        return Base64.getUrlEncoder().withoutPadding().encodeToString(signature.sign())
    }

    fun createPairingPayload(
        deviceId: String,
        serverNonce: ByteArray,
        clientNonce: ByteArray,
        fingerprint: String,
    ): ByteArray = lengthPrefixed(
        "codex-micro-pair-v1".toByteArray(StandardCharsets.US_ASCII),
        deviceId.toByteArray(StandardCharsets.UTF_8),
        serverNonce,
        clientNonce,
        fingerprint.toByteArray(StandardCharsets.US_ASCII),
    )

    fun createAuthenticationPayload(
        challengeId: String,
        deviceId: String,
        serverNonce: ByteArray,
        fingerprint: String,
    ): ByteArray = lengthPrefixed(
        "codex-micro-auth-v1".toByteArray(StandardCharsets.US_ASCII),
        challengeId.toByteArray(StandardCharsets.UTF_8),
        deviceId.toByteArray(StandardCharsets.UTF_8),
        serverNonce,
        fingerprint.toByteArray(StandardCharsets.US_ASCII),
    )

    private fun lengthPrefixed(vararg fields: ByteArray): ByteArray {
        val size = fields.sumOf { 4 + it.size }
        return ByteBuffer.allocate(size).apply {
            fields.forEach { field -> putInt(field.size); put(field) }
        }.array()
    }

    private fun loadOrCreate(): KeyPair {
        val keyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }
        val privateKey = keyStore.getKey(KEY_ALIAS, null)
        val publicKey = keyStore.getCertificate(KEY_ALIAS)?.publicKey
        if (privateKey != null && publicKey != null) return KeyPair(publicKey, privateKey as java.security.PrivateKey)

        return KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEY_STORE).run {
            initialize(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY,
                )
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .setUserAuthenticationRequired(false)
                    .build(),
            )
            generateKeyPair()
        }
    }

    private companion object {
        const val ANDROID_KEY_STORE = "AndroidKeyStore"
        const val KEY_ALIAS = "codex_micro_pairing_p256_v1"
    }
}
