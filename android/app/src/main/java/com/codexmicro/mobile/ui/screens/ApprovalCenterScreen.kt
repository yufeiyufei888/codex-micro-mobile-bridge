package com.codexmicro.mobile.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Approval
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.DoNotDisturb
import androidx.compose.material.icons.rounded.Schedule
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.codexmicro.mobile.domain.ApprovalRequest
import com.codexmicro.mobile.domain.ApprovalDecision
import com.codexmicro.mobile.domain.ApprovalStatus
import com.codexmicro.mobile.ui.components.HoldToApproveButton
import com.codexmicro.mobile.ui.theme.Amber300
import com.codexmicro.mobile.ui.theme.Emerald300
import com.codexmicro.mobile.ui.theme.Rose300
import com.codexmicro.mobile.ui.theme.Slate300
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

@Composable
fun ApprovalCenterScreen(
    approvals: List<ApprovalRequest>,
    busy: Boolean,
    onResolve: (String, ApprovalDecision) -> Unit,
    onRespondUserInput: (String, Map<String, String>) -> Unit,
    modifier: Modifier = Modifier,
) {
    val sorted = approvals.sortedWith(compareBy<ApprovalRequest> { it.status != ApprovalStatus.PENDING }
        .thenByDescending { it.requestedAtEpochMs })
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Column(Modifier.semantics { heading() }) {
                Text("桌面权限确认", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
                Text(
                    "长按批准会点击电脑端当前 Codex 审批按钮，效果等同于在该确认界面执行回车。",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        if (busy) {
            item {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    Text("正在提交操作，请稍候…", color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        if (sorted.isEmpty()) {
            item {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Icon(Icons.Rounded.Approval, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Text("当前桌面对话无需确认", style = MaterialTheme.typography.titleMedium)
                        Text("电脑端出现可识别的权限确认控件后，会自动显示在这里。", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
        items(sorted, key = { it.id }) { approval ->
            ApprovalCard(approval, busy, onResolve, onRespondUserInput)
        }
    }
}

@Composable
private fun ApprovalCard(
    approval: ApprovalRequest,
    busy: Boolean,
    onResolve: (String, ApprovalDecision) -> Unit,
    onRespondUserInput: (String, Map<String, String>) -> Unit,
) {
    val pending = approval.status == ApprovalStatus.PENDING
    val details = remember(approval.detailsJson) {
        runCatching { Json.parseToJsonElement(approval.detailsJson).jsonObject }.getOrNull()
    }
    val allowedDecisions = details?.get("allowedDecisions")?.jsonArray.orEmpty()
        .mapNotNull { it.jsonPrimitive.contentOrNull }
    val allowedScopes = details?.get("allowedScopes")?.jsonArray.orEmpty()
        .mapNotNull { it.jsonPrimitive.contentOrNull }
    val onceAllowed = when (approval.approvalType) {
        "permission" -> "once" in allowedScopes
        else -> "approve_once" in allowedDecisions
    }
    val sessionAllowed = when (approval.approvalType) {
        "permission" -> "session" in allowedScopes
        else -> "approve_session" in allowedDecisions
    }
    val rejectDecision = when {
        approval.approvalType == "permission" -> ApprovalDecision.DECLINE
        "decline" in allowedDecisions -> ApprovalDecision.DECLINE
        "cancel" in allowedDecisions -> ApprovalDecision.CANCEL
        else -> null
    }
    var menuExpanded by remember(approval.id) { mutableStateOf(false) }
    var confirmSession by remember(approval.id) { mutableStateOf(false) }
    val (icon, color, label) = when (approval.status) {
        ApprovalStatus.PENDING -> Triple(Icons.Rounded.Schedule, Amber300, "等待你的确认")
        ApprovalStatus.APPROVED -> Triple(Icons.Rounded.CheckCircle, Emerald300, "已批准")
        ApprovalStatus.REJECTED -> Triple(Icons.Rounded.DoNotDisturb, Rose300, "已拒绝")
        ApprovalStatus.RESOLVED -> Triple(Icons.Rounded.CheckCircle, Slate300, "已处理")
        ApprovalStatus.EXPIRED -> Triple(Icons.Rounded.Schedule, Slate300, "已过期")
    }
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(20.dp))
                Text(label, color = color, fontWeight = FontWeight.SemiBold)
                Text("· ${formatTime(approval.requestedAtEpochMs)}", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Text(approval.taskTitle, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.secondary)
            Text(approval.title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(approval.reason, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            ApprovalTypedDetails(approval)
            if (approval.commandPreview.isNotBlank()) {
                Surface(color = MaterialTheme.colorScheme.surfaceVariant, shape = MaterialTheme.shapes.small) {
                    Text(
                        approval.commandPreview,
                        modifier = Modifier.padding(12.dp),
                        fontFamily = FontFamily.Monospace,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            }
            if (pending) {
                if (approval.approvalType == "user_input") {
                    UserInputForm(approval, busy, onRespondUserInput)
                } else {
                    HoldToApproveButton(
                        enabled = onceAllowed && !busy,
                        onApprove = { onResolve(approval.id, ApprovalDecision.APPROVE_ONCE) },
                    )
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        if (rejectDecision != null) {
                            OutlinedButton(
                                onClick = { onResolve(approval.id, rejectDecision) },
                                enabled = !busy,
                                modifier = Modifier.weight(1f),
                            ) { Text(if (rejectDecision == ApprovalDecision.CANCEL) "取消" else "拒绝") }
                        }
                        if (sessionAllowed) {
                            IconButton(onClick = { menuExpanded = true }, enabled = !busy) {
                                Icon(Icons.Rounded.MoreVert, contentDescription = "更多审批选项")
                            }
                            DropdownMenu(expanded = menuExpanded, onDismissRequest = { menuExpanded = false }) {
                                DropdownMenuItem(
                                    text = { Text("批准本次会话…") },
                                    onClick = { menuExpanded = false; confirmSession = true },
                                )
                            }
                        }
                    }
                }
            }
        }
    }
    if (confirmSession) {
        AlertDialog(
            onDismissRequest = { confirmSession = false },
            title = { Text("批准整个会话？") },
            text = { Text("同类操作在当前电脑会话内可能不再逐次询问。仅在你确认任务来源和范围后使用。") },
            confirmButton = {
                Button(onClick = {
                    confirmSession = false
                    onResolve(approval.id, ApprovalDecision.APPROVE_SESSION)
                }) { Text("确认会话批准") }
            },
            dismissButton = { TextButton(onClick = { confirmSession = false }) { Text("返回") } },
        )
    }
}

@Composable
private fun ApprovalTypedDetails(approval: ApprovalRequest) {
    val details = remember(approval.detailsJson) {
        runCatching { Json.parseToJsonElement(approval.detailsJson).jsonObject }.getOrNull()
    } ?: return
    val lines = remember(details, approval.approvalType) {
        buildList {
            when (approval.approvalType) {
                "command" -> details["cwd"]?.jsonPrimitive?.contentOrNull?.let { add("工作目录：$it") }
                "file_change" -> {
                    details["itemId"]?.jsonPrimitive?.contentOrNull?.let { add("变更项：$it") }
                    details["grantRoot"]?.jsonPrimitive?.contentOrNull?.let { add("授权根目录：$it") }
                    val paths = runCatching { details["paths"]?.jsonArray }.getOrNull()
                        ?.mapNotNull { it.jsonPrimitive.contentOrNull }
                    add(if (paths == null) "具体路径：电脑端未提供" else "文件：${paths.joinToString("\n")}")
                }
                "permission" -> {
                    details["cwd"]?.jsonPrimitive?.contentOrNull?.let { add("工作目录：$it") }
                    val requested = details["requested"]?.jsonObject
                    requested?.get("filesystem")?.jsonArray.orEmpty().forEach { element ->
                        val row = element.jsonObject
                        add(
                            "文件权限：${row["access"]?.jsonPrimitive?.contentOrNull.orEmpty()} " +
                                row["path"]?.jsonPrimitive?.contentOrNull.orEmpty(),
                        )
                    }
                    requested?.get("network")?.jsonObject?.let { network ->
                        val enabled = network["enabled"]?.jsonPrimitive?.booleanOrNull == true
                        add("网络权限：${if (enabled) "请求启用" else "未请求"}")
                        network["targets"]?.jsonArray.orEmpty().forEach { element ->
                            val target = element.jsonObject
                            val host = target["host"]?.jsonPrimitive?.contentOrNull.orEmpty()
                            val protocol = target["protocol"]?.jsonPrimitive?.contentOrNull.orEmpty()
                            val port = target["port"]?.jsonPrimitive?.contentOrNull
                            add("网络目标：$protocol://$host${port?.let { ":$it" }.orEmpty()}")
                        }
                    }
                }
            }
        }
    }
    if (lines.isNotEmpty()) {
        Surface(color = MaterialTheme.colorScheme.surfaceVariant, shape = MaterialTheme.shapes.small) {
            Text(
                lines.joinToString("\n"),
                modifier = Modifier.padding(12.dp),
                fontFamily = FontFamily.Monospace,
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

private data class ApprovalQuestion(val id: String, val prompt: String, val required: Boolean)

@Composable
private fun UserInputForm(
    approval: ApprovalRequest,
    busy: Boolean,
    onSubmit: (String, Map<String, String>) -> Unit,
) {
    val questions = remember(approval.detailsJson) {
        runCatching {
            Json.parseToJsonElement(approval.detailsJson).jsonObject["questions"]?.jsonArray.orEmpty().map { item ->
                val row = item.jsonObject
                ApprovalQuestion(
                    id = row["questionId"]!!.jsonPrimitive.content,
                    prompt = row["prompt"]!!.jsonPrimitive.content,
                    required = row["required"]?.jsonPrimitive?.booleanOrNull == true,
                )
            }
        }.getOrDefault(emptyList())
    }
    val answers = remember(approval.id) { mutableStateMapOf<String, String>() }
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        questions.forEach { question ->
            OutlinedTextField(
                value = answers[question.id].orEmpty(),
                onValueChange = { answers[question.id] = it },
                label = { Text(question.prompt + if (question.required) " *" else "") },
                minLines = 2,
                modifier = Modifier.fillMaxWidth(),
            )
        }
        Button(
            onClick = { onSubmit(approval.id, answers.toMap()) },
            enabled = questions.isNotEmpty() && answers.values.any(String::isNotBlank) &&
                questions.filter { it.required }.all { !answers[it.id].isNullOrBlank() } && !busy,
            modifier = Modifier.fillMaxWidth(),
        ) { Text("提交回答") }
    }
}

private val approvalTimeFormatter = DateTimeFormatter.ofPattern("MM-dd HH:mm").withZone(ZoneId.systemDefault())
private fun formatTime(epochMs: Long): String = approvalTimeFormatter.format(Instant.ofEpochMilli(epochMs))
