package com.codexmicro.mobile.ui

import kotlinx.coroutines.CancellationException
import org.junit.Assert.assertEquals
import org.junit.Test

class OperationFeedbackPolicyTest {
    @Test
    fun cancellationDoesNotExposeCoroutineImplementationText() {
        assertEquals(
            "连接正在同步状态，请以桌面执行状态为准；若未执行再重试",
            operationFailureMessage(CancellationException("Job was cancelled")),
        )
    }

    @Test
    fun ordinaryFailureKeepsItsActionableMessage() {
        assertEquals("输入框校验失败", operationFailureMessage(IllegalStateException("输入框校验失败")))
    }
}
