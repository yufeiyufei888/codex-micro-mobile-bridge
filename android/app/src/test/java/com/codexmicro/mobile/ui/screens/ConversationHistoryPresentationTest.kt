package com.codexmicro.mobile.ui.screens

import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskMessage
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.domain.TransportKind
import org.junit.Assert.assertEquals
import org.junit.Test

class ConversationHistoryPresentationTest {
    @Test
    fun repeatedMessageIdIsShownOnceAndHistoryIsChronological() {
        val later = message("message-2", "第二条", 20)
        val earlier = message("message-1", "第一条", 10)
        val duplicate = message("message-1", "不应重复显示", 10)

        val result = conversationDisplayMessages(task("旧的最近回复"), listOf(later, duplicate, earlier))

        assertEquals(listOf("message-1", "message-2"), result.map(TaskMessage::messageId))
        assertEquals(listOf("不应重复显示", "第二条"), result.map(TaskMessage::text))
    }

    @Test
    fun persistedHistoryTakesPriorityOverLatestResponseFallback() {
        val result = conversationDisplayMessages(
            task("与历史正文相同"),
            listOf(message("message-1", "与历史正文相同", 10)),
        )

        assertEquals(1, result.size)
        assertEquals("message-1", result.single().messageId)
    }

    @Test
    fun latestResponseIsUsedOnlyWhenNoPersistedHistoryExists() {
        val result = conversationDisplayMessages(task("完整回复"), emptyList())

        assertEquals(1, result.size)
        assertEquals("latest-response-fallback", result.single().messageId)
        assertEquals("完整回复", result.single().text)
    }

    private fun task(lastResponse: String) = TaskItem(
        id = "thread-1",
        title = "当前桌面对话",
        workspace = "",
        summary = "",
        status = TaskStatus.SUCCEEDED,
        plan = emptyList(),
        transport = TransportKind.LAN_WSS,
        updatedAtEpochMs = 30,
        lastTurnId = "turn-1",
        lastResponse = lastResponse,
    )

    private fun message(id: String, text: String, completedAt: Long) = TaskMessage(
        messageId = id,
        threadId = "thread-1",
        turnId = "turn-1",
        itemId = "item-$id",
        role = "assistant",
        text = text,
        completedAtEpochMs = completedAt,
    )
}
