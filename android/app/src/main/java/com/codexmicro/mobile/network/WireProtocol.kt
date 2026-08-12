package com.codexmicro.mobile.network

import com.codexmicro.mobile.domain.PlanStep
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

const val PROTOCOL_VERSION = 1

object ProtocolOps {
    const val TASKS_LIST = "tasks.list"
    const val TASK_CREATE = "task.create"
    const val TASK_READ = "task.read"
    const val TASK_SEND = "task.send"
    const val TASK_INTERRUPT = "task.interrupt"
    const val TASK_FORK = "task.fork"
    const val TASK_READ_ACK = "task.read_ack"
    const val APPROVAL_RESPOND = "approval.respond"
    const val SLOT_ASSIGN = "slot.assign"
}

object ProtocolEvents {
    const val SNAPSHOT = "snapshot"
    const val BRIDGE_STATUS = "bridge.status"
    const val TASK_STATE = "task.state"
    const val TASK_MESSAGE_DELTA = "task.message.delta"
    const val TASK_MESSAGE_COMPLETED = "task.message.completed"
    const val TASK_PLAN_UPDATED = "task.plan.updated"
    const val APPROVAL_REQUESTED = "approval.requested"
    const val APPROVAL_RESOLVED = "approval.resolved"
    const val TASK_ERROR = "task.error"
}

@Serializable
data class ProtocolRequest(
    val v: Int = PROTOCOL_VERSION,
    val id: String,
    val op: String,
    val params: JsonObject,
)

@Serializable
data class ProtocolResponse(
    val v: Int,
    val id: String,
    val result: JsonElement? = null,
    val error: ProtocolError? = null,
)

@Serializable
data class ProtocolError(
    val code: String,
    val message: String,
    val retryable: Boolean,
    val details: JsonObject? = null,
)

@Serializable
data class ProtocolEvent(
    val v: Int,
    val epoch: String,
    val seq: Long,
    val event: String,
    val data: JsonObject,
)

sealed interface IncomingMessage {
    data class Response(val value: ProtocolResponse) : IncomingMessage
    data class Event(val value: ProtocolEvent) : IncomingMessage
}

@Serializable
data class SnapshotData(
    val bridge: WireBridgeStatus,
    val tasks: List<WireTask>,
    val approvals: List<WireApproval>,
    val modelCatalog: List<WireModel>,
    val projectCatalog: List<WireProject>,
    val slots: List<WireSlot>,
)

@Serializable data class WireBridgeStatus(val status: String, val reason: String? = null)

@Serializable data class WireProject(val projectId: String, val path: String? = null, val displayName: String)
@Serializable data class WireSlot(val slot: Int, val threadId: String?)

@Serializable
data class WireModel(
    val id: String,
    val displayName: String,
    val supportedReasoningEfforts: List<String>,
    val default: Boolean,
)

@Serializable
data class WireTask(
    val threadId: String,
    val title: String,
    val projectId: String?,
    val status: String,
    val activeTurnId: String?,
    val attention: Boolean,
    val plan: List<WirePlanStep>,
    val progress: WireProgress,
    val lastMessagePreview: String?,
    val updatedAt: String,
)

@Serializable
data class WirePlanStep(val stepId: String, val text: String, val status: String)

@Serializable
data class WireProgress(
    val kind: String,
    val label: String? = null,
    val source: String? = null,
    val completedSteps: Int? = null,
    val totalSteps: Int? = null,
)

@Serializable
data class WireApproval(
    val approvalId: String,
    val threadId: String,
    val turnId: String,
    val epoch: String,
    val seq: Long,
    val approvalType: String,
    val title: String,
    val summary: String,
    val details: JsonObject,
    val requestedAt: String,
)

@Serializable
data class WireMessage(
    val messageId: String,
    val threadId: String,
    val turnId: String,
    val itemId: String,
    val role: String,
    val text: String,
    val completedAt: String,
)

@Serializable data class TaskStateData(val task: WireTask)
@Serializable data class BridgeStatusData(val status: String, val reason: String?)
@Serializable data class TaskMessageDeltaData(
    val threadId: String,
    val turnId: String,
    val itemId: String,
    val messageId: String,
    val channel: String,
    val delta: String,
)
@Serializable data class TaskMessageCompletedData(val message: WireMessage)
@Serializable data class TaskPlanUpdatedData(
    val threadId: String,
    val turnId: String,
    val steps: List<WirePlanStep>,
)
@Serializable data class ApprovalRequestedData(val approval: WireApproval)
@Serializable data class ApprovalResolvedData(
    val approvalId: String,
    val threadId: String,
    val turnId: String,
    val epoch: String,
    val seq: Long,
    val resolution: String,
)
@Serializable data class TaskErrorData(
    val threadId: String,
    val turnId: String?,
    val code: String,
    val message: String,
    val recoverable: Boolean,
)

@Serializable data class CommandApprovalDetails(
    val type: String,
    val command: String,
    val cwd: String,
    val reason: String,
    val allowedDecisions: List<String>,
)
@Serializable data class FileChangeApprovalDetails(
    val type: String,
    val itemId: String,
    val paths: List<String>?,
    val grantRoot: String,
    val allowedDecisions: List<String>,
)
@Serializable data class FilesystemPermission(
    val permissionId: String,
    val path: String,
    val access: String,
)
@Serializable data class NetworkTarget(
    val host: String,
    val protocol: String,
    val port: Int? = null,
)
@Serializable data class NetworkPermission(
    val permissionId: String,
    val enabled: Boolean,
    val targets: List<NetworkTarget>,
)
@Serializable data class RequestedPermissions(
    val filesystem: List<FilesystemPermission>,
    val network: NetworkPermission,
)
@Serializable data class PermissionApprovalDetails(
    val type: String,
    val cwd: String,
    val requested: RequestedPermissions,
    val allowedScopes: List<String>,
)
@Serializable data class ApprovalQuestion(
    val questionId: String,
    val prompt: String,
    val required: Boolean,
    val options: List<String>,
)
@Serializable data class UserInputApprovalDetails(
    val type: String,
    val questions: List<ApprovalQuestion>,
)

@Serializable data class TaskResult(val task: WireTask)
@Serializable data class TurnAcceptedResult(val accepted: Boolean, val threadId: String, val turnId: String)
@Serializable data class ReadAckResult(val accepted: Boolean, val threadId: String, val throughMessageId: String)
@Serializable data class ApprovalRespondResult(val accepted: Boolean, val approvalId: String)
@Serializable data class SlotAssignResult(val accepted: Boolean, val slot: Int, val threadId: String?)
@Serializable data class TaskReadResult(
    val epoch: String,
    val seq: Long,
    val task: WireTask,
    val messages: List<WireMessage>,
    val approvals: List<WireApproval>,
)

class RemoteProtocolException(val remote: ProtocolError) : Exception("${remote.code}: ${remote.message}")

fun JsonObject.taskResultThreadId(): String? =
    this["task"]?.jsonObject?.get("threadId")?.jsonPrimitive?.contentOrNull
