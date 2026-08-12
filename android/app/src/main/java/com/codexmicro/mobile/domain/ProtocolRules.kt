package com.codexmicro.mobile.domain

data class EventCursor(val epoch: String? = null, val seq: Long = 0, val waitingForSnapshot: Boolean = true) {
    fun acceptSnapshot(epoch: String, seq: Long): EventCursor = EventCursor(epoch, seq, false)

    fun reduce(epoch: String, seq: Long): CursorDecision = when {
        waitingForSnapshot -> CursorDecision.WaitForSnapshot
        epoch != this.epoch -> CursorDecision.EpochChanged
        seq <= this.seq -> CursorDecision.Ignore
        seq > this.seq + 1L -> CursorDecision.Gap(copy(seq = seq), seq - this.seq - 1L)
        else -> CursorDecision.Accept(copy(seq = seq))
    }
}

sealed interface CursorDecision {
    data object WaitForSnapshot : CursorDecision
    data object EpochChanged : CursorDecision
    data object Ignore : CursorDecision
    data class Gap(val cursor: EventCursor, val missingCount: Long) : CursorDecision
    data class Accept(val cursor: EventCursor) : CursorDecision
}

fun mapCanonicalTaskStatus(status: String, attention: Boolean): TaskStatus = when (status.lowercase()) {
    "running" -> TaskStatus.WORKING
    "waiting_approval" -> TaskStatus.WAITING_APPROVAL
    "waiting_input" -> TaskStatus.WAITING_REPLY
    "completed" -> if (attention) TaskStatus.COMPLETED_UNREAD else TaskStatus.SUCCEEDED
    "interrupted" -> TaskStatus.INTERRUPTED
    "error" -> TaskStatus.FAILED
    "recovery_unknown" -> TaskStatus.RECOVERY_UNKNOWN
    "idle" -> TaskStatus.IDLE
    else -> TaskStatus.QUEUED
}

fun approvalResolutionMatches(
    eventEpoch: String,
    updateEpoch: String,
    updateSeq: Long,
    updateThreadId: String,
    updateTurnId: String,
    pending: ApprovalRequest?,
): Boolean {
    if (updateEpoch != eventEpoch || updateSeq < 1) return false
    return pending == null || (
        pending.requestEpoch == updateEpoch &&
            pending.requestSeq == updateSeq &&
            pending.threadId == updateThreadId &&
            pending.turnId == updateTurnId
        )
}

fun canApplyReadAcknowledgement(
    readEpoch: String,
    readSeq: Long,
    throughMessageId: String,
    cursor: EventCursor,
    latestMessageId: String?,
): Boolean = !cursor.waitingForSnapshot &&
    cursor.epoch == readEpoch &&
    cursor.seq == readSeq &&
    latestMessageId == throughMessageId

object TaskSlotPlanner {
    fun sixSlots(tasks: List<TaskItem>): List<TaskItem?> {
        val ordered = tasks.sortedWith(
            compareByDescending<TaskItem> { it.attention }
                .thenByDescending { it.status == TaskStatus.COMPLETED_UNREAD }
                .thenByDescending { it.status == TaskStatus.WORKING }
                .thenByDescending { it.pinned }
                .thenByDescending { it.updatedAtEpochMs },
        )
        val result = arrayOfNulls<TaskItem>(6)
        ordered.filter { it.slot in 1..6 }.forEach { result[it.slot!! - 1] = it }
        ordered.filter { it.slot !in 1..6 }.forEach { task ->
            val index = result.indexOfFirst { it == null }
            if (index >= 0) result[index] = task
        }
        return result.toList()
    }
}
