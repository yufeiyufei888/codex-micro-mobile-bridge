# Codex Micro Android

原生 Android V1.0.6 客户端，使用 Kotlin、Jetpack Compose Material 3 与单向数据流。它通过同一局域网内的证书绑定 WSS 连接 Windows Bridge；不开放 BLE、语音或 PTT。V1.0.6 保留可点击的完整对话历史页与重排后的审批卡片，并显示 Bridge 同时核验输入框与当前会话文件后的权威桌面同步状态。

## 桌面同步模式

- 首页使用白色主题，只显示“当前 Codex 桌面对话”及其运行、待确认、完成、错误或离线状态。
- 打开控制器后输入文字，即发送到电脑端当前 Codex 对话的输入框。
- “停止”对应当前桌面 Codex 的停止按钮。
- “桌面权限确认”展示当前对话的权限请求，并会在显示前过滤 Windows 控件树中的通用词碎片。批准仍需长按至少 600ms；Bridge 会在执行前重新核对审批身份，避免批准已经变化的请求。
- “最近回复”可点击进入对话历史，查看手机发送、电脑直接发送以及 Codex 回复；记录保存在本机数据库中，重连或重启后仍可读取。
- Computer Use 权限弹窗会同步到“确认”页；普通批准只点击电脑端“允许此对话”，不会扩大成“始终允许”。
- 当前模式没有手机端独立的六任务、新建线程、fork、模型或项目目录选择。切换桌面当前对话即切换手机控制目标。

## 配对与安全

二维码必须严格匹配：

```json
{"v":1,"hostId":"...","wssUrl":"wss://host:port/v1/mobile","certSpkiSha256":"...","nonce":"...","expiresAt":0,"pairingCode":"000000"}
```

应用只接受 `wss` 与固定 `/v1/mobile` 路径，并检查约 60 秒的有效期。首次安装生成稳定的手机 `deviceId` 和 Android Keystore P-256 密钥；配对与重连分别使用带长度前缀的 ECDSA proof。TLS 显式校验证书有效期、SHA-256 SPKI pin 与二维码主机的精确 SAN。

所有写操作使用持久化 `clientCommandId`，网络超时后的同动作重试复用原 ID。首个业务帧必须是 snapshot；epoch 变化或 seq 断档会停止应用增量并重新同步。

## 权限

- `INTERNET`、`ACCESS_NETWORK_STATE`：WSS 与连接状态。
- `CHANGE_WIFI_MULTICAST_STATE`：mDNS/NSD 发现。
- `CAMERA`：仅在扫码时由 CameraX 与 bundled ML Kit 本地识别二维码。
- `POST_NOTIFICATIONS`：待确认和前台连接通知。
- `FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_CONNECTED_DEVICE`：可选后台保持连接。深度省电状态下更新仍可能延迟。

## 构建与测试

要求 JDK 17、Android SDK 36、Build Tools 36.0.0：

```powershell
$env:JAVA_HOME = (Resolve-Path ..\work\jdk17\jdk-17.0.20+8)
$env:ANDROID_SDK_ROOT = (Resolve-Path ..\work\android-sdk)
..\work\gradle\gradle-8.11.1\bin\gradle.bat -p . testDebugUnitTest assembleDebug
```

JVM 测试覆盖 wire decode、epoch/seq reducer、状态映射、二维码/SPKI、pin/SAN、快照替换与 outbox 幂等。真实的 Codex 窗口输入、审批确认、CameraX、通知、Doze 和不同 Android 版本仍需电脑与手机联合验收。
