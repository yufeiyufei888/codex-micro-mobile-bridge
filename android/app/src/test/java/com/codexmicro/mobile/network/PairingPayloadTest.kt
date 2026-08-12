package com.codexmicro.mobile.network

import java.util.Base64
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingPayloadTest {
    private val json = Json { ignoreUnknownKeys = false }
    private val now = 1_700_000_000_000L
    private val pin = Base64.getEncoder().withoutPadding().encodeToString(ByteArray(32) { it.toByte() })
    private val nonce = Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(32) { (it + 1).toByte() })

    @Test
    fun parsesCanonicalQrAndRetainsSecurityFields() {
        val result = PairingPayload.parse(payload(expiresAtSeconds = now / 1_000 + 60), json, now).getOrThrow()
        assertEquals("host-123", result.hostId)
        assertEquals("192.168.1.8", result.host)
        assertEquals(7443, result.port)
        assertEquals("/v1/mobile", result.path)
        assertEquals(pin, result.spkiSha256)
        assertEquals(nonce, result.serverNonce)
        assertEquals("123456", result.pairingCode)
    }

    @Test
    fun rejectsExpiredOverlongAndWrongPathPayloads() {
        assertTrue(PairingPayload.parse(payload(now / 1_000), json, now).isFailure)
        assertTrue(PairingPayload.parse(payload(now / 1_000 + 90), json, now).isFailure)
        assertTrue(PairingPayload.parse(payload(now / 1_000 + 60).replace("/v1/mobile", "/wrong"), json, now).isFailure)
    }

    @Test
    fun rejectsInvalidSpkiAndUnknownQrField() {
        assertTrue(PairingPayload.parse(payload(now / 1_000 + 60).replace(pin, "short"), json, now).isFailure)
        val unknown = payload(now / 1_000 + 60).dropLast(1) + ",\"extra\":true}"
        assertTrue(PairingPayload.parse(unknown, json, now).isFailure)
    }

    private fun payload(expiresAtSeconds: Long) =
        """{"v":1,"hostId":"host-123","wssUrl":"wss://192.168.1.8:7443/v1/mobile","certSpkiSha256":"$pin","nonce":"$nonce","expiresAt":$expiresAtSeconds,"pairingCode":"123456"}"""
}
