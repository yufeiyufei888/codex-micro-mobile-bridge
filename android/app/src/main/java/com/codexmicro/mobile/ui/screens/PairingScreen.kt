package com.codexmicro.mobile.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.CameraAlt
import androidx.compose.material.icons.rounded.Lan
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.codexmicro.mobile.network.DiscoveredHost
import com.codexmicro.mobile.scanner.QrScannerView

@Composable
@OptIn(ExperimentalMaterial3Api::class)
fun PairingScreen(
    hasCameraPermission: Boolean,
    discoveredHosts: List<DiscoveredHost>,
    discoveryRunning: Boolean,
    onRequestCamera: () -> Unit,
    onToggleDiscovery: () -> Unit,
    onPairCode: (String) -> Unit,
    onPairManual: (String, String, String, String, String) -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var deviceName by rememberSaveable { mutableStateOf("Codex Micro") }
    var host by rememberSaveable { mutableStateOf("") }
    var port by rememberSaveable { mutableStateOf("47127") }
    var pin by rememberSaveable { mutableStateOf("") }
    var pairingCode by rememberSaveable { mutableStateOf("") }
    var scannerEnabled by rememberSaveable { mutableStateOf(true) }

    Column(modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("连接设备") },
            navigationIcon = {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "返回")
                }
            },
        )
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            item {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("扫码配对", style = MaterialTheme.typography.titleLarge)
                    Text(
                        "在电脑端打开 Codex Micro 配对码。二维码只携带地址和一次性设备身份指纹。",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            item {
                if (hasCameraPermission) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(220.dp)
                            .clip(MaterialTheme.shapes.large)
                            .background(MaterialTheme.colorScheme.surfaceVariant),
                    ) {
                        QrScannerView(
                            enabled = scannerEnabled,
                            onCode = {
                                scannerEnabled = false
                                onPairCode(it)
                            },
                            modifier = Modifier.fillMaxSize(),
                        )
                        Text(
                            "将二维码放入取景框",
                            modifier = Modifier
                                .align(Alignment.BottomCenter)
                                .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.88f))
                                .padding(horizontal = 12.dp, vertical = 8.dp),
                        )
                    }
                } else {
                    PermissionCard(
                        icon = { Icon(Icons.Rounded.CameraAlt, contentDescription = null) },
                        title = "需要相机权限",
                        detail = "相机只用于本机识别配对二维码，不上传画面。",
                        action = "允许扫码",
                        onAction = onRequestCamera,
                    )
                }
            }
            if (hasCameraPermission && !scannerEnabled) {
                item {
                    OutlinedButton(
                        onClick = { scannerEnabled = true },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Icon(Icons.Rounded.Refresh, contentDescription = null)
                        Text("重新扫码")
                    }
                }
            }
            item {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f)) {
                        Text("局域网发现", style = MaterialTheme.typography.titleLarge)
                        Text("发现地址后仍需输入配对指纹。", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    OutlinedButton(onClick = onToggleDiscovery) {
                        if (discoveryRunning) CircularProgressIndicator(Modifier.padding(end = 8.dp).height(18.dp))
                        else Icon(Icons.Rounded.Refresh, contentDescription = null)
                        Text(if (discoveryRunning) "停止" else "扫描")
                    }
                }
            }
            discoveredHosts.forEach { item ->
                item(key = "host-${item.name}") {
                    Card(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable {
                                deviceName = item.name
                                host = item.host
                                port = item.port.toString()
                            }
                            .semantics { role = Role.Button },
                        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
                    ) {
                        Row(
                            Modifier.padding(14.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Icon(Icons.Rounded.Lan, contentDescription = null, tint = MaterialTheme.colorScheme.secondary)
                            Column {
                                Text(item.name, style = MaterialTheme.typography.titleMedium)
                                Text("${item.host}:${item.port}", color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                }
            }
            item { Text("手动配对", style = MaterialTheme.typography.titleLarge) }
            item {
                OutlinedTextField(
                    value = deviceName,
                    onValueChange = { deviceName = it },
                    label = { Text("设备名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                OutlinedTextField(
                    value = host,
                    onValueChange = { host = it },
                    label = { Text("主机名或 IP") },
                    supportingText = { Text("例如 codex-micro.local 或 192.168.1.8") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                OutlinedTextField(
                    value = port,
                    onValueChange = { port = it.filter(Char::isDigit) },
                    label = { Text("WSS 端口") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                OutlinedTextField(
                    value = pin,
                    onValueChange = { pin = it.trim() },
                    label = { Text("证书 SPKI SHA-256 指纹") },
                    supportingText = { Text("接受 sha256/base64、base64url 或 64 位十六进制") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                OutlinedTextField(
                    value = pairingCode,
                    onValueChange = { pairingCode = it.filter(Char::isDigit).take(6) },
                    label = { Text("6 位配对码") },
                    supportingText = { Text("电脑端开启配对后显示，有效期约 60 秒") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                Button(
                    onClick = { onPairManual(deviceName, host, port, pin, pairingCode) },
                    enabled = host.isNotBlank() && port.isNotBlank() && pin.isNotBlank() && pairingCode.length == 6,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Icon(Icons.Rounded.Lock, contentDescription = null)
                    Text("验证身份并连接")
                }
            }
        }
    }
}

@Composable
private fun PermissionCard(
    icon: @Composable () -> Unit,
    title: String,
    detail: String,
    action: String,
    onAction: () -> Unit,
) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Row(
            Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            icon()
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Button(onClick = onAction) { Text(action) }
        }
    }
}
