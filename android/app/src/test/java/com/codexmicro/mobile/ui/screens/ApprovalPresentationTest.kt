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

    @Test
    fun presentsComputerUseApprovalAsOneRequestAndOneTarget() {
        val reason = "Computer Use\n允许 ChatGPT 使用 notepad?"

        assertEquals(
            ApprovalPresentation(
                request = "允许 Codex 使用电脑功能操作目标应用？",
                target = "Windows 记事本（notepad）",
            ),
            buildApprovalPresentation(
                approvalType = "command",
                commandPreview = "在当前 Codex 桌面审批界面执行确认",
                title = "确认当前桌面权限",
                reason = reason,
            ),
        )
    }

    @Test
    fun doesNotRepeatGenericApprovalChromeWhenTheTargetIsUnknown() {
        assertEquals(
            ApprovalPresentation("允许执行当前电脑操作？", ""),
            buildApprovalPresentation(
                approvalType = "command",
                commandPreview = "在当前 Codex 桌面审批界面执行确认",
                reason = "确认\n权限\n待批准",
                title = "确认当前桌面权限",
            ),
        )
    }
}
