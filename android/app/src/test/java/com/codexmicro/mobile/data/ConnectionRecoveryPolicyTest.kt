package com.codexmicro.mobile.data

import com.codexmicro.mobile.domain.ConnectionStatus
import com.codexmicro.mobile.network.WireBridgeStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ConnectionRecoveryPolicyTest {
    @Test
    fun onlyIdentityAndProtocolAuthenticationErrorsPermanentlyBlockPairing() {
        assertTrue(isBlockingAuthenticationError("AUTH_FAILED"))
        assertTrue(isBlockingAuthenticationError("CERT_PIN_MISMATCH"))
        assertTrue(isBlockingAuthenticationError("UNSUPPORTED_PROTOCOL"))
        assertFalse(isBlockingAuthenticationError("INTERNAL"))
        assertFalse(isBlockingAuthenticationError("TIMEOUT"))
        assertFalse(isBlockingAuthenticationError("OVERLOADED"))
    }

    @Test
    fun matchingSnapshotPreviewDoesNotReplaceCompleteReply() {
        val full = "第一句。第二句。最后一句。"
        assertEquals(full, preserveCompleteResponse(full, "第一句。"))
        assertEquals(full, preserveCompleteResponse(full, "最后一句。"))
        assertEquals(full, preserveCompleteResponse(full, null))
    }

    @Test
    fun aDifferentAuthoritativePreviewReplacesStaleReply() {
        assertEquals(
            "这是更新后的回复",
            preserveCompleteResponse("这是旧回复，内容很长", "这是更新后的回复"),
        )
    }

    @Test
    fun verifiedDesktopRunningStatusIsAcceptedAsCanonicalProgress() {
        assertTrue(isCanonicalProgressSource("desktop_ui_status"))
        assertTrue(isCanonicalProgressSource("app_server_status"))
        assertFalse(isCanonicalProgressSource("desktop-ui-status"))
    }

    @Test
    fun authoritativeDesktopDegradedStateSurvivesTransientRefresh() {
        val restored = restoreAuthoritativeBridgeStatus(
            WireBridgeStatus("degraded", "桌面输入框暂不可用"),
            "测试电脑",
        )

        assertTrue(restored is ConnectionStatus.Degraded)
        assertEquals("桌面输入框暂不可用", (restored as ConnectionStatus.Degraded).reason)
    }
}
