package com.codexmicro.mobile.ui.screens

import org.junit.Assert.assertEquals
import org.junit.Test

class ApprovalPresentationTest {
    @Test
    fun removesDesktopChromeFragmentsAndKeepsTheActualPermissionQuestion() {
        val reason = """
            确认
            权限
            确认
            审批
            权限
            待批准
            Computer Use
            允许 ChatGPT 使用 notepad?
        """.trimIndent()

        assertEquals(
            "允许 ChatGPT 使用 notepad?",
            sanitizeApprovalReason(reason, "确认当前桌面权限"),
        )
    }

    @Test
    fun removesDuplicateLinesAndTheRepeatedTitle() {
        val reason = """
            确认当前桌面权限
            允许 ChatGPT 使用 notepad?
            允许 ChatGPT 使用 notepad?
        """.trimIndent()

        assertEquals(
            "允许 ChatGPT 使用 notepad?",
            sanitizeApprovalReason(reason, "确认当前桌面权限"),
        )
    }
}
