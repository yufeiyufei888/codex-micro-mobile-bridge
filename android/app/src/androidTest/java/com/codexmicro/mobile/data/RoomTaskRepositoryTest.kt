package com.codexmicro.mobile.data

import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.codexmicro.mobile.data.local.CodexMicroDatabase
import com.codexmicro.mobile.domain.ApprovalRequest
import com.codexmicro.mobile.domain.ApprovalStatus
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskMessage
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.domain.TransportKind
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class RoomTaskRepositoryTest {
    private lateinit var database: CodexMicroDatabase
    private lateinit var repository: RoomTaskRepository

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            InstrumentationRegistry.getInstrumentation().targetContext,
            CodexMicroDatabase::class.java,
        ).allowMainThreadQueries().build()
        repository = RoomTaskRepository(database, Json { ignoreUnknownKeys = true })
    }

    @After
    fun close() = database.close()

    @Test
    fun snapshotReplacementDeletesMissingTasksAndApprovals() = runBlocking {
        repository.upsertTask(task("old"))
        repository.upsertApproval(approval("old-approval", "old"))

        repository.replaceSnapshot(listOf(task("new")), listOf(approval("new-approval", "new")))

        assertEquals(listOf("new"), repository.tasks.first().map { it.id })
        assertEquals(listOf("new-approval"), repository.approvals.first().map { it.id })
        assertNull(repository.getTask("old"))
        assertNull(repository.getApproval("old-approval"))
    }

    @Test
    fun uncertainCommandRetainsOriginalClientCommandId() = runBlocking {
        val command = PendingCommand("action", "command-1", "epoch-123456789012", "task.send", "{}", "pending", 1)
        repository.savePendingCommand(command)
        repository.markPendingCommandUncertain("action")

        assertEquals("command-1", repository.getPendingCommand("action")?.commandId)
        assertEquals("uncertain", repository.getPendingCommand("action")?.state)
    }

    @Test
    fun latestMessageWatermarkChangesWhenANewerCompletionArrives() = runBlocking {
        repository.upsertMessage(message("message-1", 1))
        assertEquals("message-1", repository.lastMessageId("thread-1"))

        repository.upsertMessage(message("message-2", 2))

        assertEquals("message-2", repository.lastMessageId("thread-1"))
    }

    private fun task(id: String) = TaskItem(
        id = id,
        title = id,
        workspace = "",
        summary = "",
        status = TaskStatus.IDLE,
        plan = emptyList(),
        transport = TransportKind.LAN_WSS,
        updatedAtEpochMs = 1,
    )

    private fun approval(id: String, threadId: String) = ApprovalRequest(
        id = id,
        taskId = threadId,
        threadId = threadId,
        turnId = "turn-1",
        taskTitle = threadId,
        title = "Approval",
        reason = "Reason",
        commandPreview = "",
        status = ApprovalStatus.PENDING,
        requestedAtEpochMs = 1,
        expiresAtEpochMs = 2,
        requestEpoch = "epoch-123456789012",
        requestSeq = 1,
    )

    private fun message(id: String, completedAt: Long) = TaskMessage(
        messageId = id,
        threadId = "thread-1",
        turnId = "turn-1",
        itemId = "item-$id",
        role = "assistant",
        text = id,
        completedAtEpochMs = completedAt,
    )
}
