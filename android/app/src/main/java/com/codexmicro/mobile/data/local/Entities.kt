package com.codexmicro.mobile.data.local

import androidx.room.Entity
import androidx.room.ColumnInfo
import androidx.room.PrimaryKey

@Entity(tableName = "tasks")
data class TaskEntity(
    @PrimaryKey val id: String,
    val title: String,
    val workspace: String,
    val projectId: String?,
    val summary: String,
    val status: String,
    val planJson: String,
    val transport: String,
    val updatedAtEpochMs: Long,
    val activeTurnId: String?,
    val lastTurnId: String?,
    val slot: Int?,
    @ColumnInfo(defaultValue = "0") val unread: Boolean,
    @ColumnInfo(defaultValue = "0") val attention: Boolean,
    @ColumnInfo(defaultValue = "0") val pinned: Boolean,
    @ColumnInfo(defaultValue = "'unknown'") val progressKind: String,
    val progressCompleted: Int?,
    val progressTotal: Int?,
    val progressLabel: String?,
    @ColumnInfo(defaultValue = "''") val lastResponse: String,
)

@Entity(tableName = "approvals")
data class ApprovalEntity(
    @PrimaryKey val id: String,
    val taskId: String,
    @ColumnInfo(defaultValue = "''") val threadId: String,
    @ColumnInfo(defaultValue = "''") val turnId: String,
    val taskTitle: String,
    val title: String,
    val reason: String,
    val commandPreview: String,
    val status: String,
    val requestedAtEpochMs: Long,
    val expiresAtEpochMs: Long,
    @ColumnInfo(defaultValue = "''") val requestEpoch: String,
    @ColumnInfo(defaultValue = "0") val requestSeq: Long,
    @ColumnInfo(defaultValue = "'command'") val approvalType: String,
    @ColumnInfo(defaultValue = "'{}'") val detailsJson: String,
)

@Entity(tableName = "task_messages")
data class TaskMessageEntity(
    @PrimaryKey val messageId: String,
    val threadId: String,
    val turnId: String,
    val itemId: String,
    val role: String,
    val text: String,
    val completedAtEpochMs: Long,
)

@Entity(tableName = "pending_commands")
data class PendingCommandEntity(
    @PrimaryKey val actionKey: String,
    val commandId: String,
    val epoch: String,
    val operation: String,
    val paramsJson: String,
    val state: String,
    val createdAtEpochMs: Long,
)
