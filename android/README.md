# Codex Micro Android V2.0.0

原生 Android 客户端采用 Kotlin、Jetpack Compose、Room、DataStore 与 Ktor。它只通过同一局域网中的证书绑定 WSS 连接 Windows Bridge，不保存 Codex 登录凭据。

## V2.0.0

- 首页槽位 1 始终表示当前可操作的 Codex Desktop 对话；下面最多展示五个最近对话，并按稳定会话 ID 隔离。
- 当前与最近五个同步会话的回复历史、运行/完成状态、未读状态和审批归属分别存储，避免跨对话串消息。超出这六个会话的桌面历史不属于本版手机长期保存范围。
- Goal、计划与提问卡片不会被误报为电脑服务降级；没有普通输入框时仅禁用发送。
- 审批页展示简洁的“操作 / 目标”信息，批准必须长按 600ms；批准前由 Bridge 再次核对桌面审批。
- Room 数据库升级至 v4，清理历史演示记录但保留真实对话数据。
- 已删除演示模式。Release 构建启用 R8 与资源压缩，产物保持通用 APK，不做手机型号或 ABI 专用发布。

## 构建

要求 JDK 17、Android SDK 36 和 Build Tools 36.0.0。

```powershell
$env:JAVA_HOME = 'C:\Path\To\JDK17'
$env:ANDROID_SDK_ROOT = 'C:\Path\To\AndroidSDK'
.\gradlew.bat -p . --no-daemon testDebugUnitTest assembleRelease
```

Gradle Wrapper 锁定 8.11.1 并校验发行包 SHA-256；无需仓库外的固定 `work` 目录。

发布包需保持 `com.codexmicro.mobile.debug`、递增 versionCode 和既有签名连续性，才能覆盖安装。JVM 测试不能替代真机 WSS、后台保活或真实 Codex 审批验收。
