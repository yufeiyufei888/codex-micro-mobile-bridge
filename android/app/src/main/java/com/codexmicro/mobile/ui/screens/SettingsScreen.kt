package com.codexmicro.mobile.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Link
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.PhonelinkLock
import androidx.compose.material.icons.rounded.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.codexmicro.mobile.data.settings.SettingsSnapshot
import com.codexmicro.mobile.ui.theme.Emerald300
import com.codexmicro.mobile.ui.theme.Rose300

@Composable
fun SettingsScreen(
    settings: SettingsSnapshot,
    hasCameraPermission: Boolean,
    hasNotificationPermission: Boolean,
    onSetKeepConnected: (Boolean) -> Unit,
    onOpenPairing: () -> Unit,
    onUnpair: () -> Unit,
    onRequestCamera: () -> Unit,
    onRequestNotifications: () -> Unit,
    onOpenSystemSettings: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var confirmUnpair by remember { mutableStateOf(false) }
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Column {
                Text("设置", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
                Text("连接、权限和本地数据", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        item {
            SettingSwitch(
                title = "后台保持连接",
                detail = "显示常驻通知并监听网络恢复；红米/小米还需在系统设置允许后台运行和无限制用电",
                checked = settings.keepConnected,
                enabled = settings.pairing != null,
                onCheckedChange = onSetKeepConnected,
            )
        }
        if (settings.keepConnected && settings.pairing != null) {
            item {
                OutlinedButton(onClick = onOpenSystemSettings, modifier = Modifier.fillMaxWidth()) {
                    Text("打开系统设置，检查后台与省电限制")
                }
            }
        }
        item { SectionTitle("设备") }
        item {
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Icon(Icons.Rounded.PhonelinkLock, contentDescription = null, tint = MaterialTheme.colorScheme.secondary)
                        Column(Modifier.weight(1f)) {
                            Text(settings.pairing?.deviceName ?: "尚未配对", style = MaterialTheme.typography.titleMedium)
                            Text(
                                settings.pairing?.let { "${it.host}:${it.port} · 证书已固定" } ?: "扫码或手动输入配对信息",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                    Button(onClick = onOpenPairing, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.Rounded.Link, contentDescription = null)
                        Text(if (settings.pairing == null) "连接设备" else "重新配对")
                    }
                    if (settings.pairing != null) {
                        OutlinedButton(onClick = { confirmUnpair = true }, modifier = Modifier.fillMaxWidth()) {
                            Text("解除配对", color = Rose300)
                        }
                    }
                }
            }
        }
        item { SectionTitle("权限状态") }
        item {
            PermissionRow(
                title = "相机",
                detail = if (hasCameraPermission) "已允许，仅用于二维码识别" else "未允许，无法扫码配对",
                granted = hasCameraPermission,
                onRequest = onRequestCamera,
                onSettings = onOpenSystemSettings,
            )
        }
        item {
            PermissionRow(
                title = "通知",
                detail = if (hasNotificationPermission) "已允许，可及时显示审批" else "未允许，后台审批可能被错过",
                granted = hasNotificationPermission,
                onRequest = onRequestNotifications,
                onSettings = onOpenSystemSettings,
            )
        }
        item {
            Text(
                "安全说明：应用只接受 WSS；配对后的 SPKI 指纹用于确认设备身份。Keystore 私钥不可导出，日志和通知不会显示令牌。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(vertical = 8.dp),
            )
        }
    }

    if (confirmUnpair) {
        AlertDialog(
            onDismissRequest = { confirmUnpair = false },
            title = { Text("解除设备配对？") },
            text = { Text("将移除保存的设备地址和证书指纹，并返回未配对状态。") },
            confirmButton = {
                TextButton(onClick = { confirmUnpair = false; onUnpair() }) { Text("解除", color = Rose300) }
            },
            dismissButton = { TextButton(onClick = { confirmUnpair = false }) { Text("取消") } },
        )
    }
}

@Composable
private fun SettingSwitch(
    title: String,
    detail: String,
    checked: Boolean,
    enabled: Boolean = true,
    onCheckedChange: (Boolean) -> Unit,
) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 64.dp)
                .clickable(enabled = enabled, role = Role.Switch) { onCheckedChange(!checked) }
                .padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Switch(checked = checked, onCheckedChange = null, enabled = enabled)
        }
    }
}

@Composable
private fun PermissionRow(
    title: String,
    detail: String,
    granted: Boolean,
    onRequest: () -> Unit,
    onSettings: () -> Unit,
) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Row(
            Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Icon(
                if (granted) Icons.Rounded.CheckCircle else Icons.Rounded.Warning,
                contentDescription = null,
                tint = if (granted) Emerald300 else MaterialTheme.colorScheme.error,
            )
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            TextButton(onClick = if (granted) onSettings else onRequest) {
                Text(if (granted) "管理" else "允许")
            }
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(text, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 8.dp))
}
