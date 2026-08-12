package com.codexmicro.mobile.data

import com.codexmicro.mobile.domain.ApprovalRequest
import com.codexmicro.mobile.domain.ApprovalStatus
import com.codexmicro.mobile.domain.PlanStep
import com.codexmicro.mobile.domain.PlanStepState
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.domain.TransportKind

object DemoSeed {
    fun tasks(now: Long = System.currentTimeMillis()): List<TaskItem> = listOf(
        TaskItem(
            id = "desktop-current",
            title = "当前 Codex 桌面对话",
            workspace = "Windows Codex Desktop",
            summary = "演示：手机消息将同步到电脑端当前打开的对话",
            status = TaskStatus.WAITING_APPROVAL,
            plan = listOf(
                PlanStep("desktop-step-1", "定位并核对当前 Codex 窗口", PlanStepState.COMPLETED),
                PlanStep("desktop-step-2", "等待当前桌面权限确认", PlanStepState.IN_PROGRESS),
            ),
            transport = TransportKind.DEMO,
            updatedAtEpochMs = now,
            slot = 1,
            unread = false,
            attention = true,
        ),
    )

    fun approvals(now: Long = System.currentTimeMillis()): List<ApprovalRequest> = listOf(
        ApprovalRequest(
            id = "approval-desktop-demo",
            taskId = "desktop-current",
            threadId = "desktop-current",
            turnId = "desktop-turn-demo",
            taskTitle = "当前 Codex 桌面对话",
            title = "确认当前桌面权限",
            reason = "演示数据：真实模式会在执行前重新核对当前审批控件。",
            commandPreview = "当前 Codex 对话中的权限请求",
            status = ApprovalStatus.PENDING,
            requestedAtEpochMs = now - 30_000,
            expiresAtEpochMs = now + 10 * 60_000,
            requestEpoch = "demo-epoch",
            requestSeq = 1,
            detailsJson = """{"type":"command","command":"当前 Codex 对话中的权限请求","cwd":"Windows Codex Desktop","reason":"演示：批准前会重新核对桌面审批身份","allowedDecisions":["approve_once","decline"]}""",
        ),
    )
}
