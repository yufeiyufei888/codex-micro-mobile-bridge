package com.codexmicro.mobile.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
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
    // The v0.2 desktop-sync surface intentionally exposes one current desktop conversation.
    @Suppress("UNUSED_VARIABLE") val legacyCallbacks =
        listOf(models, projects, onCreateTask, onAssignSlot, onClearSlot, onTogglePinned)
    val current = tasks.firstOrNull()
    val offline = connection !is ConnectionStatus.Online && connection != ConnectionStatus.Demo

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
                "控制电脑屏幕上当前打开的 Codex 对话",
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        ConnectionCard(connection = connection, onPair = onPair)

        if (current != null) {
            TaskCard(
                task = current,
                slot = 1,
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
                Text("打开当前桌面对话控制器")
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

        Surface(
            color = MaterialTheme.colorScheme.surfaceVariant,
            shape = MaterialTheme.shapes.large,
        ) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("使用说明", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text("1. 电脑上先切换到你要控制的 Codex 对话。")
                Text("2. 手机进入上方卡片，输入内容并发送。")
                Text("3. 手机消息会写入当前桌面输入框并执行发送。")
                Text("4. 出现权限确认时，在“审批”页长按批准。")
                Text(
                    "安全限制：如果 Codex 窗口、输入框或审批控件在操作前发生变化，本次操作会直接取消。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
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
                    ConnectionStatus.Demo -> "演示数据不会操作电脑"
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
