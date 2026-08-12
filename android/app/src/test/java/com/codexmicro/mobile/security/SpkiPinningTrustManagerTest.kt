package com.codexmicro.mobile.security

import java.security.MessageDigest
import java.util.Base64
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SpkiPinningTrustManagerTest {
    private val spki = ByteArray(91) { (it * 3).toByte() }
    private val pin = Base64.getEncoder().withoutPadding()
        .encodeToString(MessageDigest.getInstance("SHA-256").digest(spki))

    @Test
    fun pinMatchAndMismatchAreExplicit() {
        assertTrue(SpkiPinningTrustManager.matchesPin(pin, spki))
        assertFalse(SpkiPinningTrustManager.matchesPin(pin, spki.copyOf().also { it[0]++ }))
    }

    @Test
    fun dnsSanRequiresExactNonWildcardMatch() {
        assertTrue(SpkiPinningTrustManager.matchesSubjectAlternativeName("desktop.local", listOf(listOf(2, "desktop.local"))))
        assertFalse(SpkiPinningTrustManager.matchesSubjectAlternativeName("desktop.local", listOf(listOf(2, "other.local"))))
        assertFalse(SpkiPinningTrustManager.matchesSubjectAlternativeName("desktop.local", listOf(listOf(2, "*.local"))))
    }

    @Test
    fun ipSanRequiresExactAddressMatch() {
        assertTrue(SpkiPinningTrustManager.matchesSubjectAlternativeName("192.168.1.8", listOf(listOf(7, "192.168.1.8"))))
        assertFalse(SpkiPinningTrustManager.matchesSubjectAlternativeName("192.168.1.8", listOf(listOf(7, "192.168.1.9"))))
    }
}
