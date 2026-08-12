package com.codexmicro.mobile.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.History
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskMessage
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
@OptIn(ExperimentalMaterial3Api::class)
fun ConversationHistoryScreen(
    task: TaskItem?,
    messages: List<TaskMessage>,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val displayMessages = remember(task?.id, task?.lastResponse, messages) {
        conversationDisplayMessages(task, messages)
    }
    val listState = rememberLazyListState()
    LaunchedEffect(displayMessages.size) {
        if (displayMessages.isNotEmpty()) listState.scrollToItem(displayMessages.size)
    }

    Column(modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("对话历史") },
            navigationIcon = {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回当前桌面对话")
                }
            },
        )
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item(key = "history-intro") {
                Surface(
                    color = MaterialTheme.colorScheme.surfaceVariant,
                    shape = MaterialTheme.shapes.large,
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(16.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(Icons.Rounded.History, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(task?.title ?: "当前桌面对话", fontWeight = FontWeight.SemiBold)
                            Text(
                                "按时间保留手机或电脑发送的内容，以及 Codex 的完整回复。",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }
            if (displayMessages.isEmpty()) {
                item(key = "history-empty") {
                    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                        Column(Modifier.fillMaxWidth().padding(24.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text("暂无已同步的对话记录", style = MaterialTheme.typography.titleMedium)
                            Text(
                                "完成下一次桌面对话后，桥接程序会同步当前会话的用户消息和 Codex 回复。",
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            } else {
                items(displayMessages, key = TaskMessage::messageId) { message ->
                    ConversationMessageCard(message)
                }
            }
        }
    }
}

internal fun conversationDisplayMessages(
    task: TaskItem?,
    messages: List<TaskMessage>,
): List<TaskMessage> {
    if (messages.isNotEmpty()) {
        return messages
            .distinctBy(TaskMessage::messageId)
            .sortedWith(compareBy(TaskMessage::completedAtEpochMs, TaskMessage::messageId))
    }
    return task?.lastResponse?.takeIf(String::isNotBlank)?.let { response ->
        listOf(
            TaskMessage(
                messageId = "latest-response-fallback",
                threadId = task.id,
                turnId = task.lastTurnId ?: task.id,
                itemId = "latest-response-fallback",
                role = "assistant",
                text = response,
                completedAtEpochMs = task.updatedAtEpochMs,
            ),
        )
    }.orEmpty()
}

@Composable
private fun ConversationMessageCard(message: TaskMessage) {
    val fromUser = message.role == "user"
    val label = when (message.role) {
        "user" -> "你"
        "assistant" -> "Codex"
        "tool" -> "工具"
        else -> "系统"
    }
    val container = if (fromUser) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surface
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = if (fromUser) Arrangement.End else Arrangement.Start,
    ) {
        Card(
            modifier = Modifier.fillMaxWidth(0.94f),
            colors = CardDefaults.cardColors(containerColor = container),
        ) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(label, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold)
                    Text(
                        historyTimeFormatter.format(Instant.ofEpochMilli(message.completedAtEpochMs)),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Text(message.text, style = MaterialTheme.typography.bodyMedium)
            }
        }
    }
}

private val historyTimeFormatter = DateTimeFormatter.ofPattern("MM-dd HH:mm")
    .withZone(ZoneId.systemDefault())
