package com.codexmicro.mobile.ui.screens

import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.domain.TransportKind
import org.junit.Assert.assertEquals
import org.junit.Test

class DesktopConversationLayoutTest {
    @Test
    fun slotOneIsCurrentAndRecentConversationsKeepTheirOwnSlots() {
        val tasks = listOf(task("third", slot = 3, updatedAt = 30), task("current", slot = 1, updatedAt = 10), task("second", slot = 2, updatedAt = 20))

        val (current, recent) = splitDesktopConversations(tasks)

        assertEquals("current", current?.id)
        assertEquals(listOf("second", "third"), recent.map(TaskItem::id))
    }

    @Test
    fun newestConversationBecomesCurrentOnlyWhenNoSlotLayoutExists() {
        val (current, recent) = splitDesktopConversations(
            listOf(task("older", slot = null, updatedAt = 10), task("newer", slot = null, updatedAt = 20)),
        )

        assertEquals("newer", current?.id)
        assertEquals(listOf("older"), recent.map(TaskItem::id))
    }

    private fun task(id: String, slot: Int?, updatedAt: Long) = TaskItem(
        id = id,
        title = id,
        workspace = "",
        summary = "",
        status = TaskStatus.IDLE,
        plan = emptyList(),
        transport = TransportKind.LAN_WSS,
        updatedAtEpochMs = updatedAt,
        slot = slot,
    )
}
