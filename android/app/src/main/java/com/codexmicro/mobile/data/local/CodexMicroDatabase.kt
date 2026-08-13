package com.codexmicro.mobile.data.local

import androidx.room.Database
import androidx.room.RoomDatabase

@Database(
    entities = [TaskEntity::class, ApprovalEntity::class, TaskMessageEntity::class, PendingCommandEntity::class],
    version = 4,
    exportSchema = true,
)
abstract class CodexMicroDatabase : RoomDatabase() {
    abstract fun taskDao(): TaskDao
    abstract fun approvalDao(): ApprovalDao
    abstract fun taskMessageDao(): TaskMessageDao
    abstract fun pendingCommandDao(): PendingCommandDao
}

/**
 * Removes only the two identities used by the retired desktop demo.
 *
 * The predicates deliberately do not use a broad transport or status match: early builds could
 * store real LAN conversations beside the sample row, and those records must survive an upgrade.
 */
val MIGRATION_3_4 = object : androidx.room.migration.Migration(3, 4) {
    override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
        db.execSQL("DELETE FROM task_messages WHERE threadId = 'desktop-current'")
        db.execSQL("DELETE FROM approvals WHERE id = 'approval-desktop-demo' OR threadId = 'desktop-current'")
        db.execSQL(
            "DELETE FROM pending_commands " +
                "WHERE REPLACE(paramsJson, ' ', '') LIKE '%\"approvalId\":\"approval-desktop-demo\"%' " +
                "OR REPLACE(paramsJson, ' ', '') LIKE '%\"threadId\":\"desktop-current\"%'",
        )
        db.execSQL("DELETE FROM tasks WHERE id = 'desktop-current'")
    }
}

val MIGRATION_2_3 = object : androidx.room.migration.Migration(2, 3) {
    override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
        db.execSQL("ALTER TABLE tasks ADD COLUMN lastResponse TEXT NOT NULL DEFAULT ''")
        db.execSQL("ALTER TABLE tasks ADD COLUMN projectId TEXT")
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS task_messages (messageId TEXT NOT NULL, threadId TEXT NOT NULL, turnId TEXT NOT NULL, itemId TEXT NOT NULL, role TEXT NOT NULL, text TEXT NOT NULL, completedAtEpochMs INTEGER NOT NULL, PRIMARY KEY(messageId))",
        )
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS pending_commands (actionKey TEXT NOT NULL, commandId TEXT NOT NULL, epoch TEXT NOT NULL, operation TEXT NOT NULL, paramsJson TEXT NOT NULL, state TEXT NOT NULL, createdAtEpochMs INTEGER NOT NULL, PRIMARY KEY(actionKey))",
        )
    }
}

val MIGRATION_1_2 = object : androidx.room.migration.Migration(1, 2) {
    override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
        db.execSQL("ALTER TABLE tasks ADD COLUMN activeTurnId TEXT")
        db.execSQL("ALTER TABLE tasks ADD COLUMN lastTurnId TEXT")
        db.execSQL("ALTER TABLE tasks ADD COLUMN slot INTEGER")
        db.execSQL("ALTER TABLE tasks ADD COLUMN unread INTEGER NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE tasks ADD COLUMN attention INTEGER NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE tasks ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE tasks ADD COLUMN progressKind TEXT NOT NULL DEFAULT 'unknown'")
        db.execSQL("ALTER TABLE tasks ADD COLUMN progressCompleted INTEGER")
        db.execSQL("ALTER TABLE tasks ADD COLUMN progressTotal INTEGER")
        db.execSQL("ALTER TABLE tasks ADD COLUMN progressLabel TEXT")
        db.execSQL("ALTER TABLE approvals ADD COLUMN threadId TEXT NOT NULL DEFAULT ''")
        db.execSQL("ALTER TABLE approvals ADD COLUMN turnId TEXT NOT NULL DEFAULT ''")
        db.execSQL("ALTER TABLE approvals ADD COLUMN requestEpoch TEXT NOT NULL DEFAULT ''")
        db.execSQL("ALTER TABLE approvals ADD COLUMN requestSeq INTEGER NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE approvals ADD COLUMN approvalType TEXT NOT NULL DEFAULT 'command'")
        db.execSQL("ALTER TABLE approvals ADD COLUMN detailsJson TEXT NOT NULL DEFAULT '{}'")
    }
}
