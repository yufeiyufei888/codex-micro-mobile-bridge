package com.codexmicro.mobile.network

import com.codexmicro.mobile.domain.PairingInfo
import com.codexmicro.mobile.security.SpkiPinningTrustManager
import java.net.URI
import java.util.Base64
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json

object PairingPayload {
    fun parse(raw: String, json: Json, nowEpochMs: Long = System.currentTimeMillis()): Result<PairingInfo> = runCatching {
        val normalized = raw.trim()
        require(normalized.length <= MAX_QR_CHARS) { "Pairing payload is too large" }
        val dto = json.decodeFromString<PairingQr>(normalized)
        require(dto.v == 1) { "Unsupported pairing protocol version" }
        require(dto.hostId.matches(Regex("^[A-Za-z0-9._-]{3,128}$"))) { "Invalid host ID" }
        require(dto.pairingCode.matches(Regex("^[0-9]{6}$"))) { "Invalid pairing code" }
        val uri = URI(dto.wssUrl)
        require(uri.scheme.equals("wss", ignoreCase = true)) { "Pairing URL must use WSS" }
        require(uri.userInfo == null && uri.query == null && uri.fragment == null) { "Pairing URL contains unsupported fields" }
        require(!uri.host.isNullOrBlank() && uri.port in 1..65535) { "Pairing URL host or port is invalid" }
        require(uri.path == "/v1/mobile") { "Pairing URL path must be /v1/mobile" }
        val expiryMs = Math.multiplyExact(dto.expiresAt, 1_000L)
        require(expiryMs > nowEpochMs) { "Pairing code has expired" }
        require(expiryMs <= nowEpochMs + MAX_PAIRING_WINDOW_MS) { "Pairing expiry is outside the allowed window" }
        val nonce = Base64.getUrlDecoder().decode(padBase64Url(dto.nonce))
        require(nonce.size == 32) { "Pairing nonce is invalid" }
        SpkiPinningTrustManager.canonicalPin(dto.certSpkiSha256)
        PairingInfo(
            hostId = dto.hostId,
            deviceName = "Codex Micro ${dto.hostId.take(8)}",
            host = uri.host,
            port = uri.port,
            path = uri.path,
            spkiSha256 = dto.certSpkiSha256.trim(),
            pairingCode = dto.pairingCode,
            serverNonce = dto.nonce,
            pairingExpiresAtEpochMs = expiryMs,
        )
    }

    private fun padBase64Url(value: String): String = value + "=".repeat((4 - value.length % 4) % 4)

    @Serializable
    private data class PairingQr(
        val v: Int,
        val hostId: String,
        val wssUrl: String,
        val certSpkiSha256: String,
        val nonce: String,
        val expiresAt: Long,
        val pairingCode: String,
    )

    internal const val MAX_PAIRING_WINDOW_MS = 75_000L
    private const val MAX_QR_CHARS = 8_192
}
