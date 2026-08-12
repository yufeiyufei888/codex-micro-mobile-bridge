package com.codexmicro.mobile.domain

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ProtocolRulesTest {
    @Test
    fun eventCursorToleratesGapAndDuplicateButRequiresSnapshotForNewEpoch() {
        val fresh = EventCursor()
        assertEquals(CursorDecision.WaitForSnapshot, fresh.reduce("epoch-1234567890", 1))

        val accepted = fresh.acceptSnapshot("epoch-1234567890", 7)
        assertEquals(EventCursor("epoch-1234567890", 8, false), (accepted.reduce("epoch-1234567890", 8) as CursorDecision.Accept).cursor)
        assertEquals(EventCursor("epoch-1234567890", 9, false), (accepted.reduce("epoch-1234567890", 9) as CursorDecision.Gap).cursor)
        assertEquals(1L, (accepted.reduce("epoch-1234567890", 9) as CursorDecision.Gap).missingCount)
        assertEquals(CursorDecision.Ignore, accepted.reduce("epoch-1234567890", 7))
        assertEquals(CursorDecision.EpochChanged, accepted.reduce("another-epoch-123", 8))
    }

    @Test
    fun readAcknowledgementDoesNotClearUnreadAfterAConcurrentEventOrMessage() {
        val cursor = EventCursor("epoch-1234567890", 7, false)
        assertTrue(canApplyReadAcknowledgement("epoch-1234567890", 7, "message-1", cursor, "message-1"))
        assertTrue(!canApplyReadAcknowledgement("epoch-1234567890", 7, "message-1", cursor.copy(seq = 8), "message-2"))
        assertTrue(!canApplyReadAcknowledgement("epoch-1234567890", 7, "message-1", cursor, "message-2"))
    }

    @Test
    fun canonicalStatusUsesAttentionForUnreadCompletion() {
        assertEquals(TaskStatus.WAITING_REPLY, mapCanonicalTaskStatus("waiting_input", false))
        assertEquals(TaskStatus.COMPLETED_UNREAD, mapCanonicalTaskStatus("completed", true))
        assertEquals(TaskStatus.SUCCEEDED, mapCanonicalTaskStatus("completed", false))
        assertEquals(TaskStatus.RECOVERY_UNKNOWN, mapCanonicalTaskStatus("recovery_unknown", true))
    }

    @Test
    fun approvalResolutionUsesOriginalRequestBindingNotResolutionEventSequence() {
        val resolutionEventSeq = 9L
        val pending = ApprovalRequest(
            id = "approval-1",
            taskId = "thread-1",
            threadId = "thread-1",
            turnId = "turn-1",
            taskTitle = "Task",
            title = "Approval",
            reason = "Reason",
            commandPreview = "",
            status = ApprovalStatus.PENDING,
            requestedAtEpochMs = 1,
            expiresAtEpochMs = 2,
            requestEpoch = "epoch-123456789012",
            requestSeq = 4,
            approvalType = "command",
            detailsJson = "{}",
        )

        assertTrue(resolutionEventSeq != pending.requestSeq)
        assertTrue(
            approvalResolutionMatches(
                eventEpoch = "epoch-123456789012",
                updateEpoch = "epoch-123456789012",
                updateSeq = 4,
                updateThreadId = "thread-1",
                updateTurnId = "turn-1",
                pending = pending,
            ),
        )
        assertTrue(!approvalResolutionMatches(
            "epoch-123456789012", "epoch-123456789012", 9, "thread-1", "turn-1", pending,
        ))
    }

    @Test
    fun slotPlannerAlwaysReturnsSixAndAppliesPriorityToFreeSlots() {
        val recent = task("recent", TaskStatus.IDLE, updatedAt = 50)
        val pinned = task("pinned", TaskStatus.IDLE, pinned = true, updatedAt = 10)
        val running = task("running", TaskStatus.WORKING, updatedAt = 20)
        val completedUnread = task("unread", TaskStatus.COMPLETED_UNREAD, updatedAt = 30)
        val attention = task("attention", TaskStatus.WAITING_APPROVAL, attention = true, updatedAt = 1)

        val slots = TaskSlotPlanner.sixSlots(listOf(recent, pinned, running, completedUnread, attention))

        assertEquals(6, slots.size)
        assertEquals(listOf("attention", "unread", "running", "pinned", "recent"), slots.take(5).map { it?.id })
        assertNull(slots[5])
    }

    @Test
    fun taskWithoutPlanNeverInventsAPercentage() {
        val task = task("unknown", TaskStatus.WORKING)
        assertTrue(task.progressKind is ProgressKind.Unknown)
        assertNull(task.progress)
    }

    private fun task(
        id: String,
        status: TaskStatus,
        attention: Boolean = false,
        pinned: Boolean = false,
        updatedAt: Long = 0,
    ) = TaskItem(
        id = id,
        title = id,
        workspace = "",
        summary = "",
        status = status,
        plan = emptyList(),
        transport = TransportKind.LAN_WSS,
        updatedAtEpochMs = updatedAt,
        attention = attention,
        pinned = pinned,
    )
}
