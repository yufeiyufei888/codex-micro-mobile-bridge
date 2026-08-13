package com.codexmicro.mobile.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.OpenInNew
import androidx.compose.material.icons.rounded.AddLink
import androidx.compose.material.icons.rounded.DesktopWindows
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.codexmicro.mobile.domain.ConnectionStatus
import com.codexmicro.mobile.domain.ModelOption
import com.codexmicro.mobile.domain.ProjectOption
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.ui.components.TaskCard
import com.codexmicro.mobile.ui.components.visual

@Composable
fun TaskGridScreen(
    tasks: List<TaskItem>,
    models: List<ModelOption>,
    projects: List<ProjectOption>,
    connection: ConnectionStatus,
    busy: Boolean,
    onOpenTask: (String) -> Unit,
    onCreateTask: (String, String, String, String?, String?, Int?) -> Unit,
    onAssignSlot: (String, Int) -> Unit,
    onClearSlot: (Int) -> Unit,
    onTogglePinned: (String, Boolean) -> Unit,
    onPair: () -> Unit,
    modifier: Modifier = Modifier,
) {
    @Suppress("UNUSED_VARIABLE") val legacyCallbacks =
        listOf(models, projects, onCreateTask, onAssignSlot, onClearSlot, onTogglePinned)
    val (current, recent) = splitDesktopConversations(tasks)
    val offline = connection !is ConnectionStatus.Online

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("桌面同步", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
            Text(
                "分别查看当前对话与最近对话的回复、状态和审批",
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        ConnectionCard(connection = connection, onPair = onPair)

        if (current != null) {
            TaskCard(
                task = current,
                slot = current.slot ?: 1,
                offline = offline,
                onClick = { onOpenTask(current.id) },
                onLongClick = { onOpenTask(current.id) },
                modifier = Modifier.fillMaxWidth(),
            )
            Button(
                onClick = { onOpenTask(current.id) },
                enabled = connection is ConnectionStatus.Online && !busy,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Icon(Icons.AutoMirrored.Rounded.OpenInNew, contentDescription = null)
                Text("打开当前对话")
            }
        } else {
            Card(
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
            ) {
                Column(Modifier.fillMaxWidth().padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Icon(Icons.Rounded.DesktopWindows, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                    Text("等待电脑端当前对话", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                    Text(
                        "请在电脑上打开 Codex 和目标对话。桥接程序识别到输入框后会自动同步。",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }

        if (recent.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text("最近对话", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                Text(
                    "历史对话按各自会话独立保存；切回电脑当前对话后才可从手机发送。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            recent.forEach { task ->
                ConversationRow(task = task, onClick = { onOpenTask(task.id) })
            }
        }

        Surface(
            color = MaterialTheme.colorScheme.surfaceVariant,
            shape = MaterialTheme.shapes.large,
        ) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("使用说明", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text("1. 电脑上先切换到你要控制的 Codex 对话。")
                Text("2. 当前对话始终显示在第一张卡片，可输入内容并发送。")
                Text("3. 最近对话可以查看独立历史；在电脑切换后会自动置顶。")
                Text("4. 权限确认会标明所属对话，请在“审批”页长按批准。")
                Text(
                    "安全限制：如果 Codex 窗口、输入框或审批控件在操作前发生变化，本次操作会直接取消。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

internal fun splitDesktopConversations(tasks: List<TaskItem>): Pair<TaskItem?, List<TaskItem>> {
    val current = tasks.firstOrNull { it.slot == 1 } ?: tasks.maxByOrNull { it.updatedAtEpochMs }
    val recent = tasks
        .filterNot { it.id == current?.id }
        .sortedWith(compareBy<TaskItem> { it.slot ?: Int.MAX_VALUE }.thenByDescending { it.updatedAtEpochMs })
    return current to recent
}

@Composable
private fun ConversationRow(task: TaskItem, onClick: () -> Unit) {
    val visual = task.status.visual()
    Card(
        onClick = onClick,
        colors = CardDefaults.cardColors(containerColor = visual.containerColor),
        border = BorderStroke(1.dp, visual.color.copy(alpha = 0.30f)),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Surface(
                color = MaterialTheme.colorScheme.surface.copy(alpha = 0.78f),
                contentColor = visual.color,
                shape = MaterialTheme.shapes.small,
            ) {
                Icon(visual.icon, contentDescription = null, modifier = Modifier.padding(8.dp).size(20.dp))
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(task.title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, maxLines = 2)
                Text(
                    task.summary.ifBlank { "尚无回复摘要" },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                )
            }
            Column(horizontalAlignment = Alignment.End) {
                Text(visual.label, color = visual.color, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold)
                Text("对话 ${task.slot ?: "-"}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun ConnectionCard(connection: ConnectionStatus, onPair: () -> Unit) {
    val visual = connection.visual()
    Card(
        colors = CardDefaults.cardColors(containerColor = visual.containerColor),
        border = BorderStroke(1.dp, visual.color.copy(alpha = 0.42f)),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Surface(
                color = MaterialTheme.colorScheme.surface.copy(alpha = 0.72f),
                contentColor = visual.color,
                shape = MaterialTheme.shapes.small,
            ) {
                Icon(visual.icon, contentDescription = null, modifier = Modifier.padding(7.dp))
            }
            Column(Modifier.weight(1f)) {
                Text(visual.label, fontWeight = FontWeight.SemiBold)
                val detail = when (connection) {
                    is ConnectionStatus.Blocked -> connection.reason
                    is ConnectionStatus.Degraded -> connection.reason ?: "电脑端未找到可用的 Codex 当前对话"
                    is ConnectionStatus.RecoveryUnknown -> connection.reason ?: "正在恢复电脑端状态"
                    is ConnectionStatus.RemoteOffline -> connection.reason ?: "电脑端暂时离线"
                    is ConnectionStatus.Error -> connection.message
                    else -> "WSS 局域网安全链路 · 桌面同步模式"
                }
                Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (connection is ConnectionStatus.Disconnected ||
                connection is ConnectionStatus.Blocked ||
                connection is ConnectionStatus.Error
            ) Button(onClick = onPair) {
                Icon(Icons.Rounded.AddLink, contentDescription = null)
                Text("配对")
            }
        }
    }
}
