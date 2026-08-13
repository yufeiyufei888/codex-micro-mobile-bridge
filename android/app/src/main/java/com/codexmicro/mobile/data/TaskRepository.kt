package com.codexmicro.mobile.data

import com.codexmicro.mobile.data.local.ApprovalDao
import com.codexmicro.mobile.data.local.ApprovalEntity
import com.codexmicro.mobile.data.local.TaskDao
import com.codexmicro.mobile.data.local.TaskEntity
import com.codexmicro.mobile.data.local.CodexMicroDatabase
import com.codexmicro.mobile.data.local.PendingCommandEntity
import com.codexmicro.mobile.data.local.TaskMessageEntity
import com.codexmicro.mobile.domain.ApprovalRequest
import com.codexmicro.mobile.domain.ApprovalStatus
import com.codexmicro.mobile.domain.PlanStep
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskMessage
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.domain.ProgressKind
import com.codexmicro.mobile.domain.TransportKind
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import androidx.room.withTransaction
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json

interface TaskRepository {
    val tasks: Flow<List<TaskItem>>
    val approvals: Flow<List<ApprovalRequest>>
    fun messages(threadId: String): Flow<List<TaskMessage>>
    suspend fun upsertTask(task: TaskItem)
    suspend fun upsertApproval(approval: ApprovalRequest)
    suspend fun getTask(id: String): TaskItem?
    suspend fun getApproval(id: String): ApprovalRequest?
    suspend fun setPinned(id: String, pinned: Boolean): Boolean
    suspend fun replaceSnapshot(tasks: List<TaskItem>, approvals: List<ApprovalRequest>)
    suspend fun replaceTaskRead(task: TaskItem, messages: List<TaskMessage>, approvals: List<ApprovalRequest>)
    suspend fun upsertMessage(message: TaskMessage)
    suspend fun lastMessageId(threadId: String): String?
    suspend fun getPendingCommand(actionKey: String): PendingCommand?
    suspend fun savePendingCommand(command: PendingCommand)
    suspend fun markPendingCommandUncertain(actionKey: String)
    suspend fun deletePendingCommand(actionKey: String)
    suspend fun deletePendingCommandsOutsideEpoch(epoch: String)
    suspend fun resolveApproval(id: String, approved: Boolean): Boolean
}

data class PendingCommand(
    val actionKey: String,
    val commandId: String,
    val epoch: String,
    val operation: String,
    val paramsJson: String,
    val state: String,
    val createdAtEpochMs: Long,
)

class RoomTaskRepository(
    private val database: CodexMicroDatabase,
    private val json: Json,
) : TaskRepository {
    private val taskDao = database.taskDao()
    private val approvalDao = database.approvalDao()
    private val messageDao = database.taskMessageDao()
    private val pendingCommandDao = database.pendingCommandDao()
    override val tasks: Flow<List<TaskItem>> = taskDao.observeAll().map { rows ->
        rows.mapNotNull { runCatching { it.toDomain(json) }.getOrNull() }
    }

    override val approvals: Flow<List<ApprovalRequest>> = approvalDao.observeAll().map { rows ->
        rows.mapNotNull { runCatching { it.toDomain() }.getOrNull() }
    }

    override fun messages(threadId: String): Flow<List<TaskMessage>> =
        messageDao.observeForThread(threadId).map { rows -> rows.map(TaskMessageEntity::toDomain) }

    override suspend fun upsertTask(task: TaskItem) {
        taskDao.upsert(task.toEntity(json))
    }

    override suspend fun upsertApproval(approval: ApprovalRequest) {
        approvalDao.upsert(approval.toEntity())
    }

    override suspend fun getTask(id: String): TaskItem? = taskDao.getById(id)?.toDomain(json)

    override suspend fun getApproval(id: String): ApprovalRequest? = approvalDao.getById(id)?.toDomain()

    override suspend fun setPinned(id: String, pinned: Boolean): Boolean {
        val task = taskDao.getById(id)?.toDomain(json) ?: return false
        taskDao.upsert(task.copy(pinned = pinned).toEntity(json))
        return true
    }

    override suspend fun replaceSnapshot(tasks: List<TaskItem>, approvals: List<ApprovalRequest>) {
        database.withTransaction {
            taskDao.clear()
            approvalDao.clear()
            taskDao.upsertAll(tasks.map { it.toEntity(json) })
            approvalDao.upsertAll(approvals.map { it.toEntity() })
            messageDao.pruneOrphans()
        }
    }

    override suspend fun replaceTaskRead(
        task: TaskItem,
        messages: List<TaskMessage>,
        approvals: List<ApprovalRequest>,
    ) {
        database.withTransaction {
            taskDao.upsert(task.toEntity(json))
            messageDao.clearForThread(task.id)
            messageDao.upsertAll(messages.map(TaskMessage::toEntity))
            approvalDao.clearForThread(task.id)
            approvalDao.upsertAll(approvals.map(ApprovalRequest::toEntity))
        }
    }

    override suspend fun upsertMessage(message: TaskMessage) = messageDao.upsert(message.toEntity())

    override suspend fun lastMessageId(threadId: String): String? = messageDao.lastMessageId(threadId)

    override suspend fun getPendingCommand(actionKey: String): PendingCommand? =
        pendingCommandDao.get(actionKey)?.toDomain()

    override suspend fun savePendingCommand(command: PendingCommand) =
        pendingCommandDao.upsert(command.toEntity())

    override suspend fun markPendingCommandUncertain(actionKey: String) =
        pendingCommandDao.updateState(actionKey, "uncertain")

    override suspend fun deletePendingCommand(actionKey: String) = pendingCommandDao.delete(actionKey)

    override suspend fun deletePendingCommandsOutsideEpoch(epoch: String) =
        pendingCommandDao.deleteOutsideEpoch(epoch)

    override suspend fun resolveApproval(id: String, approved: Boolean): Boolean {
        val status = if (approved) ApprovalStatus.APPROVED else ApprovalStatus.REJECTED
        return approvalDao.resolve(id, status.name) == 1
    }
}

private fun TaskItem.toEntity(json: Json): TaskEntity {
    val storedProgress = reportedProgress ?: if (plan.isEmpty()) ProgressKind.Unknown
        else ProgressKind.PlanSteps(completedSteps, plan.size)
    return TaskEntity(
    id = id,
    title = title,
    workspace = workspace,
    projectId = projectId,
    summary = summary,
    status = status.name,
    planJson = json.encodeToString(ListSerializer(PlanStep.serializer()), plan),
    transport = transport.name,
    updatedAtEpochMs = updatedAtEpochMs,
    activeTurnId = activeTurnId,
    lastTurnId = lastTurnId,
    slot = slot,
    unread = unread,
    attention = attention,
    pinned = pinned,
    progressKind = when (storedProgress) {
        ProgressKind.Unknown -> "unknown"
        is ProgressKind.Indeterminate -> "indeterminate"
        is ProgressKind.PlanSteps -> "plan_steps"
    },
    progressCompleted = (storedProgress as? ProgressKind.PlanSteps)?.completed,
    progressTotal = (storedProgress as? ProgressKind.PlanSteps)?.total,
    progressLabel = (storedProgress as? ProgressKind.Indeterminate)?.label,
    lastResponse = lastResponse,
    )
}

private fun TaskEntity.toDomain(json: Json) = TaskItem(
    id = id,
    title = title,
    workspace = workspace,
    projectId = projectId,
    summary = summary,
    status = TaskStatus.valueOf(status),
    plan = json.decodeFromString(ListSerializer(PlanStep.serializer()), planJson),
    transport = TransportKind.valueOf(transport),
    updatedAtEpochMs = updatedAtEpochMs,
    activeTurnId = activeTurnId,
    lastTurnId = lastTurnId,
    slot = slot,
    unread = unread,
    attention = attention,
    pinned = pinned,
    reportedProgress = when (progressKind) {
        "indeterminate" -> ProgressKind.Indeterminate(progressLabel ?: "正在执行")
        "plan_steps" -> if (progressCompleted != null && progressTotal != null && progressTotal > 0) {
            ProgressKind.PlanSteps(progressCompleted, progressTotal)
        } else ProgressKind.Unknown
        else -> ProgressKind.Unknown
    },
    lastResponse = lastResponse,
)

private fun ApprovalRequest.toEntity() = ApprovalEntity(
    id = id,
    taskId = taskId,
    threadId = threadId,
    turnId = turnId,
    taskTitle = taskTitle,
    title = title,
    reason = reason,
    commandPreview = commandPreview,
    status = status.name,
    requestedAtEpochMs = requestedAtEpochMs,
    expiresAtEpochMs = expiresAtEpochMs,
    requestEpoch = requestEpoch,
    requestSeq = requestSeq,
    approvalType = approvalType,
    detailsJson = detailsJson,
)

private fun ApprovalEntity.toDomain() = ApprovalRequest(
    id = id,
    taskId = taskId,
    threadId = threadId,
    turnId = turnId,
    taskTitle = taskTitle,
    title = title,
    reason = reason,
    commandPreview = commandPreview,
    status = ApprovalStatus.valueOf(status),
    requestedAtEpochMs = requestedAtEpochMs,
    expiresAtEpochMs = expiresAtEpochMs,
    requestEpoch = requestEpoch,
    requestSeq = requestSeq,
    approvalType = approvalType,
    detailsJson = detailsJson,
)

private fun TaskMessage.toEntity() = TaskMessageEntity(
    messageId = messageId,
    threadId = threadId,
    turnId = turnId,
    itemId = itemId,
    role = role,
    text = text,
    completedAtEpochMs = completedAtEpochMs,
)

private fun TaskMessageEntity.toDomain() = TaskMessage(
    messageId = messageId,
    threadId = threadId,
    turnId = turnId,
    itemId = itemId,
    role = role,
    text = text,
    completedAtEpochMs = completedAtEpochMs,
)

private fun PendingCommand.toEntity() = PendingCommandEntity(
    actionKey = actionKey,
    commandId = commandId,
    epoch = epoch,
    operation = operation,
    paramsJson = paramsJson,
    state = state,
    createdAtEpochMs = createdAtEpochMs,
)

private fun PendingCommandEntity.toDomain() = PendingCommand(
    actionKey = actionKey,
    commandId = commandId,
    epoch = epoch,
    operation = operation,
    paramsJson = paramsJson,
    state = state,
    createdAtEpochMs = createdAtEpochMs,
)
