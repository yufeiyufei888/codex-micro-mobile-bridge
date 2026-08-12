package com.codexmicro.mobile.domain

import kotlinx.serialization.Serializable

enum class TaskStatus {
    UNASSIGNED,
    QUEUED,
    IDLE,
    WORKING,
    WAITING_APPROVAL,
    WAITING_REPLY,
    COMPLETED_UNREAD,
    SUCCEEDED,
    INTERRUPTED,
    FAILED,
    RECOVERY_UNKNOWN,
    PAUSED,
}

enum class ApprovalStatus {
    PENDING,
    APPROVED,
    REJECTED,
    RESOLVED,
    EXPIRED,
}

enum class ApprovalDecision(val wireValue: String) {
    APPROVE_ONCE("approve_once"),
    APPROVE_SESSION("approve_session"),
    DECLINE("decline"),
    CANCEL("cancel"),
}

enum class PlanStepState {
    PENDING,
    IN_PROGRESS,
    COMPLETED,
}

enum class TransportKind {
    LAN_WSS,
    BLE,
    DEMO,
}

data class ModelOption(
    val id: String,
    val displayName: String,
    val supportedReasoningEfforts: List<String>,
    val defaultReasoningEffort: String?,
    val isDefault: Boolean = false,
)

data class ProjectOption(val id: String, val displayName: String, val path: String)

@Serializable
data class PlanStep(
    val id: String,
    val title: String,
    val state: PlanStepState,
)

data class TaskItem(
    val id: String,
    val title: String,
    val workspace: String,
    val projectId: String? = null,
    val summary: String,
    val status: TaskStatus,
    val plan: List<PlanStep>,
    val transport: TransportKind,
    val updatedAtEpochMs: Long,
    val activeTurnId: String? = null,
    val lastTurnId: String? = null,
    val slot: Int? = null,
    val unread: Boolean = false,
    val attention: Boolean = false,
    val pinned: Boolean = false,
    val reportedProgress: ProgressKind? = null,
    val lastResponse: String = "",
) {
    val completedSteps: Int get() = plan.count { it.state == PlanStepState.COMPLETED }
    val progressKind: ProgressKind
        get() = reportedProgress ?: if (plan.isEmpty()) ProgressKind.Unknown
        else ProgressKind.PlanSteps(completedSteps, plan.size)
    val progress: Float?
        get() = (progressKind as? ProgressKind.PlanSteps)?.fraction
    val currentStep: String
        get() = plan.firstOrNull { it.state == PlanStepState.IN_PROGRESS }?.title
            ?: plan.lastOrNull { it.state == PlanStepState.COMPLETED }?.title
            ?: (progressKind as? ProgressKind.Indeterminate)?.label
            ?: "等待开始"
}

sealed interface ProgressKind {
    data object Unknown : ProgressKind
    data class Indeterminate(val label: String) : ProgressKind
    data class PlanSteps(val completed: Int, val total: Int) : ProgressKind {
        val fraction: Float get() = if (total <= 0) 0f
        else (completed.toFloat() / total.toFloat()).coerceIn(0f, 1f)
    }
}

data class TaskMessage(
    val messageId: String,
    val threadId: String,
    val turnId: String,
    val itemId: String,
    val role: String,
    val text: String,
    val completedAtEpochMs: Long,
)

data class ApprovalRequest(
    val id: String,
    val taskId: String,
    val threadId: String,
    val turnId: String,
    val taskTitle: String,
    val title: String,
    val reason: String,
    val commandPreview: String,
    val status: ApprovalStatus,
    val requestedAtEpochMs: Long,
    val expiresAtEpochMs: Long,
    val requestEpoch: String,
    val requestSeq: Long,
    val approvalType: String = "command",
    val detailsJson: String = "{}",
)

data class PairingInfo(
    val hostId: String,
    val deviceName: String,
    val host: String,
    val port: Int,
    val path: String = "/v1/mobile",
    val spkiSha256: String,
    val pairingCode: String? = null,
    val serverNonce: String? = null,
    val pairingExpiresAtEpochMs: Long? = null,
)

sealed interface ConnectionStatus {
    data object Demo : ConnectionStatus
    data object Disconnected : ConnectionStatus
    data object Discovering : ConnectionStatus
    data object Connecting : ConnectionStatus
    data class Online(val transport: TransportKind, val deviceName: String) : ConnectionStatus
    data class Degraded(val reason: String?) : ConnectionStatus
    data class RecoveryUnknown(val reason: String?) : ConnectionStatus
    data class RemoteOffline(val reason: String?) : ConnectionStatus
    data class Reconnecting(val attempt: Int) : ConnectionStatus
    data class Blocked(val reason: String) : ConnectionStatus
    data class Error(val message: String) : ConnectionStatus
}
