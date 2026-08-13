package com.codexmicro.mobile.data

import androidx.room.testing.MigrationTestHelper
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.codexmicro.mobile.data.local.CodexMicroDatabase
import com.codexmicro.mobile.data.local.MIGRATION_3_4
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class LegacyDemoMigrationTest {
    @get:Rule
    val helper = MigrationTestHelper(
        InstrumentationRegistry.getInstrumentation(),
        CodexMicroDatabase::class.java,
        emptyList(),
        FrameworkSQLiteOpenHelperFactory(),
    )

    @Test
    fun migration3To4RemovesOnlyLegacyDemoRows() {
        helper.createDatabase(TEST_DATABASE, 3).apply {
            insertTask("desktop-current", "旧演示任务")
            insertTask("real-thread", "真实任务")
            insertApproval("approval-desktop-demo", "desktop-current")
            insertApproval("real-approval", "real-thread")
            insertMessage("demo-message", "desktop-current")
            insertMessage("real-message", "real-thread")
            insertCommand("demo-command", """{ "approvalId": "approval-desktop-demo" }""")
            insertCommand("real-command", """{"threadId":"real-thread"}""")
            close()
        }

        helper.runMigrationsAndValidate(TEST_DATABASE, 4, true, MIGRATION_3_4).use { db ->
            assertEquals(0, db.rowCount("tasks", "id = 'desktop-current'"))
            assertEquals(0, db.rowCount("approvals", "id = 'approval-desktop-demo'"))
            assertEquals(0, db.rowCount("task_messages", "threadId = 'desktop-current'"))
            assertEquals(0, db.rowCount("pending_commands", "actionKey = 'demo-command'"))

            assertEquals(1, db.rowCount("tasks", "id = 'real-thread'"))
            assertEquals(1, db.rowCount("approvals", "id = 'real-approval'"))
            assertEquals(1, db.rowCount("task_messages", "messageId = 'real-message'"))
            assertEquals(1, db.rowCount("pending_commands", "actionKey = 'real-command'"))
        }
    }

    private fun SupportSQLiteDatabase.insertTask(id: String, title: String) {
        execSQL(
            "INSERT INTO tasks(id,title,workspace,summary,status,planJson,transport,updatedAtEpochMs) " +
                "VALUES(?,?,?,?,?,?,?,?)",
            arrayOf<Any?>(id, title, "workspace", "summary", "IDLE", "[]", "LAN_WSS", 1L),
        )
    }

    private fun SupportSQLiteDatabase.insertApproval(id: String, threadId: String) {
        execSQL(
            "INSERT INTO approvals(id,taskId,threadId,taskTitle,title,reason,commandPreview,status," +
                "requestedAtEpochMs,expiresAtEpochMs) VALUES(?,?,?,?,?,?,?,?,?,?)",
            arrayOf<Any?>(id, threadId, threadId, "task", "title", "reason", "command", "PENDING", 1L, 2L),
        )
    }

    private fun SupportSQLiteDatabase.insertMessage(id: String, threadId: String) {
        execSQL(
            "INSERT INTO task_messages(messageId,threadId,turnId,itemId,role,text,completedAtEpochMs) " +
                "VALUES(?,?,?,?,?,?,?)",
            arrayOf<Any?>(id, threadId, "turn", "item", "assistant", "text", 1L),
        )
    }

    private fun SupportSQLiteDatabase.insertCommand(actionKey: String, paramsJson: String) {
        execSQL(
            "INSERT INTO pending_commands(actionKey,commandId,epoch,operation,paramsJson,state,createdAtEpochMs) " +
                "VALUES(?,?,?,?,?,?,?)",
            arrayOf<Any?>(actionKey, "command", "epoch", "approval.respond", paramsJson, "pending", 1L),
        )
    }

    private fun SupportSQLiteDatabase.rowCount(table: String, where: String): Int =
        query("SELECT COUNT(*) FROM $table WHERE $where").use { cursor ->
            cursor.moveToFirst()
            cursor.getInt(0)
        }

    private companion object {
        const val TEST_DATABASE = "legacy-demo-migration"
    }
}
