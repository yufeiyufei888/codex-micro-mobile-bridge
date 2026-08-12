package com.codexmicro.mobile.data.local

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import kotlinx.coroutines.flow.Flow

@Dao
interface TaskDao {
    @Query("SELECT * FROM tasks ORDER BY updatedAtEpochMs DESC")
    fun observeAll(): Flow<List<TaskEntity>>

    @Query("SELECT COUNT(*) FROM tasks")
    suspend fun count(): Int

    @Query("SELECT * FROM tasks WHERE id = :id LIMIT 1")
    suspend fun getById(id: String): TaskEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(tasks: List<TaskEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(task: TaskEntity)

    @Query("UPDATE tasks SET status = :status, updatedAtEpochMs = :updatedAt WHERE id = :taskId")
    suspend fun updateStatus(taskId: String, status: String, updatedAt: Long)

    @Query("DELETE FROM tasks")
    suspend fun clear()
}

@Dao
interface ApprovalDao {
    @Query("SELECT * FROM approvals ORDER BY requestedAtEpochMs DESC")
    fun observeAll(): Flow<List<ApprovalEntity>>

    @Query("SELECT * FROM approvals WHERE id = :id LIMIT 1")
    suspend fun getById(id: String): ApprovalEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(approvals: List<ApprovalEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(approval: ApprovalEntity)

    @Query("UPDATE approvals SET status = :status WHERE id = :approvalId AND status = 'PENDING'")
    suspend fun resolve(approvalId: String, status: String): Int

    @Query("DELETE FROM approvals")
    suspend fun clear()

    @Query("DELETE FROM approvals WHERE threadId = :threadId")
    suspend fun clearForThread(threadId: String)
}

@Dao
interface TaskMessageDao {
    @Query("SELECT * FROM task_messages WHERE threadId = :threadId ORDER BY completedAtEpochMs, messageId")
    fun observeForThread(threadId: String): Flow<List<TaskMessageEntity>>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(messages: List<TaskMessageEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(message: TaskMessageEntity)

    @Query("SELECT messageId FROM task_messages WHERE threadId = :threadId ORDER BY completedAtEpochMs DESC, messageId DESC LIMIT 1")
    suspend fun lastMessageId(threadId: String): String?

    @Query("DELETE FROM task_messages WHERE threadId = :threadId")
    suspend fun clearForThread(threadId: String)

    @Query("DELETE FROM task_messages WHERE threadId NOT IN (SELECT id FROM tasks)")
    suspend fun pruneOrphans()

    @Query("DELETE FROM task_messages")
    suspend fun clear()
}

@Dao
interface PendingCommandDao {
    @Query("SELECT * FROM pending_commands WHERE actionKey = :actionKey LIMIT 1")
    suspend fun get(actionKey: String): PendingCommandEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(command: PendingCommandEntity)

    @Query("UPDATE pending_commands SET state = :state WHERE actionKey = :actionKey")
    suspend fun updateState(actionKey: String, state: String)

    @Query("DELETE FROM pending_commands WHERE actionKey = :actionKey")
    suspend fun delete(actionKey: String)

    @Query("DELETE FROM pending_commands WHERE epoch != :epoch")
    suspend fun deleteOutsideEpoch(epoch: String)

    @Query("DELETE FROM pending_commands")
    suspend fun clear()
}
