package com.codexmicro.mobile.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.ArrowForward
import androidx.compose.material.icons.automirrored.rounded.Send
import androidx.compose.material.icons.rounded.Approval
import androidx.compose.material.icons.rounded.CallSplit
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Circle
import androidx.compose.material.icons.rounded.RadioButtonChecked
import androidx.compose.material.icons.rounded.StopCircle
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.codexmicro.mobile.domain.ModelOption
import com.codexmicro.mobile.domain.PlanStep
import com.codexmicro.mobile.domain.PlanStepState
import com.codexmicro.mobile.domain.TaskItem
import com.codexmicro.mobile.domain.TaskStatus
import com.codexmicro.mobile.ui.components.visual
import com.codexmicro.mobile.ui.theme.Emerald300

@Composable
@OptIn(ExperimentalMaterial3Api::class)
fun TaskDetailScreen(
    task: TaskItem?,
    models: List<ModelOption>,
    online: Boolean,
    busy: Boolean,
    onSend: (String, String?, String?) -> Unit,
    onInterrupt: () -> Unit,
    onFork: () -> Unit,
    onOpenApprovals: () -> Unit,
    historyCount: Int,
    onOpenHistory: () -> Unit,
    onAssignSlot: (Int) -> Unit,
    onClearSlot: (Int) -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text(task?.title ?: "任务详情", maxLines = 1) },
            navigationIcon = {
                IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回") }
            },
        )
        if (task == null) {
            Column(Modifier.padding(24.dp)) {
                Text("任务已不存在", style = MaterialTheme.typography.titleLarge)
                Text("它可能已被远端归档。", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            return@Column
        }
        val visual = task.status.visual()
        var message by rememberSaveable(task.id) { mutableStateOf("") }
        @Suppress("UNUSED_VARIABLE") val legacyControls = listOf(models, onFork, onAssignSlot, onClearSlot)
        val activeTurn = task.activeTurnId != null
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                Card(colors = CardDefaults.cardColors(containerColor = visual.containerColor)) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Icon(visual.icon, contentDescription = null, tint = visual.color)
                            Text(visual.label, color = visual.color, fontWeight = FontWeight.SemiBold)
                            Text("· ${task.transport.name}", color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
            if (task.status == TaskStatus.WAITING_APPROVAL) {
                item {
                    Button(onClick = onOpenApprovals, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.Rounded.Approval, contentDescription = null)
                        Text("前往审批中心")
                    }
                }
            }
            if (task.lastResponse.isNotBlank()) {
                item {
                    Card(
                        onClick = onOpenHistory,
                        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
                    ) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text("最近回复", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text(
                                        "查看对话${if (historyCount > 0) "（$historyCount 条）" else ""}",
                                        style = MaterialTheme.typography.labelLarge,
                                        color = MaterialTheme.colorScheme.primary,
                                    )
                                    Icon(
                                        Icons.AutoMirrored.Rounded.ArrowForward,
                                        contentDescription = null,
                                        tint = MaterialTheme.colorScheme.primary,
                                    )
                                }
                            }
                            Text(task.lastResponse, style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                }
            }
            item {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Text("发送到当前桌面对话", style = MaterialTheme.typography.titleLarge)
                        Text(
                            "电脑端当前打开哪个 Codex 对话，这条消息就发送到哪个对话。",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        OutlinedTextField(
                            value = message,
                            onValueChange = { message = it },
                            label = { Text("给 Codex 的消息") },
                            minLines = 3,
                            modifier = Modifier.fillMaxWidth(),
                            enabled = online && !busy,
                        )
                        if (activeTurn) {
                            Text(
                                "当前桌面对话仍在执行：发送内容会像你在电脑输入框继续输入并按发送。",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Button(
                            onClick = {
                                onSend(message, null, null)
                            },
                            enabled = online && !busy && message.isNotBlank(),
                            modifier = Modifier.fillMaxWidth(),
                        ) { Icon(Icons.AutoMirrored.Rounded.Send, contentDescription = null); Text("发送到桌面当前对话") }
                        if (activeTurn) {
                            OutlinedButton(onClick = onInterrupt, enabled = online && !busy, modifier = Modifier.fillMaxWidth()) {
                                Icon(Icons.Rounded.StopCircle, contentDescription = null)
                                Text("停止当前执行")
                            }
                        }
                    }
                }
            }
            item { Text("执行计划", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold) }
            if (task.plan.isEmpty()) {
                item { Text("电脑端尚未提供可验证的计划步骤。", color = MaterialTheme.colorScheme.onSurfaceVariant) }
            } else {
                items(task.plan, key = { it.id }) { step -> PlanStepRow(step) }
            }
            item {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text("同步目标", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Text(
                            "电脑屏幕上当前打开的 Codex 对话",
                            fontFamily = FontFamily.Monospace,
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun DetailSelector(
    label: String,
    value: String,
    options: List<Pair<String, String>>,
    enabled: Boolean,
    onSelect: (String) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    Box {
        OutlinedButton(
            onClick = { expanded = true },
            enabled = enabled && options.isNotEmpty(),
            modifier = Modifier.fillMaxWidth(),
        ) { Text("$label：$value", modifier = Modifier.weight(1f)) }
        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            options.forEach { (id, name) ->
                DropdownMenuItem(text = { Text(name) }, onClick = { onSelect(id); expanded = false })
            }
        }
    }
}

@Composable
private fun PlanStepRow(step: PlanStep) {
    val (icon, color, label) = when (step.state) {
        PlanStepState.COMPLETED -> Triple(Icons.Rounded.CheckCircle, Emerald300, "已完成")
        PlanStepState.IN_PROGRESS -> Triple(Icons.Rounded.RadioButtonChecked, MaterialTheme.colorScheme.secondary, "当前步骤")
        PlanStepState.PENDING -> Triple(Icons.Rounded.Circle, MaterialTheme.colorScheme.onSurfaceVariant, "未开始")
    }
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Icon(icon, contentDescription = null, tint = color)
        Column(Modifier.weight(1f)) {
            Text(step.title, style = MaterialTheme.typography.bodyLarge)
            Text(label, style = MaterialTheme.typography.labelMedium, color = color)
        }
    }
}
