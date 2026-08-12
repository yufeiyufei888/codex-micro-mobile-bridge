# Codex Micro v1.0.1 测试与验收

## 验证层级

1. **共享协议 fixtures**：验证请求、响应、事件、epoch/seq、幂等和审批语义。
2. **Windows 自动测试**：覆盖协议互操作、配对/鉴权、TLS 与持久化边界、幂等、桌面状态、会话回复提取、审批映射和运行时通知。
3. **Android JVM 测试**：覆盖 wire decode、二维码/SPKI、pin/SAN、连接恢复、状态展示、历史与审批交互策略。
4. **构建验证**：编译 .NET 解决方案并生成 Android APK。
5. **签名验证**：核验 APK 包名、版本码和 signer SHA-256。
6. **电脑与手机实机验收**：验证真实 Wi-Fi、Codex Desktop 控件、Computer Use 审批、后台/锁屏和覆盖安装。

自动测试通过只证明对应代码层，不证明真实设备、真实网络或真实 Codex UI 已验收。

## 自动验证命令

共享协议：

```powershell
node .\shared\protocol-v1\validate-fixtures.mjs
```

Windows：

```powershell
dotnet test .\bridge\CodexMicroBridge.sln -c Release
```

Android：

```powershell
$env:JAVA_HOME = '<JDK 17 路径>'
$env:ANDROID_SDK_ROOT = '<Android SDK 路径>'
.\android\gradlew.bat -p .\android --no-daemon testDebugUnitTest assembleDebug
```

统一入口：

```powershell
.\scripts\verify.ps1
```

v1.0.1 建仓基线为共享协议 38 cases / 1 pair、Windows 70 项、Android JVM 23 项。每次发布必须记录命令实际输出，不能永久照抄该数字。

## 桌面实机验收矩阵

| 场景 | 必须结果 |
| --- | --- |
| Bridge 启动且 Codex 对话可见 | 显示“桌面同步可用” |
| 手机发送普通消息 | 只写入当前 Codex 对话并成功提交 |
| 发送瞬间切换窗口 | Bridge 拒绝操作，不向其他窗口发送 |
| 电脑直接输入并发送 | 手机保持连接并同步状态/回复 |
| 长回复 | 末尾完整显示，不被摘要截断 |
| 状态卡与最近回复 | 顶部只显示状态，正文不重复 |
| 历史记录 | 手机、电脑和助手消息可查看；重连后仍存在 |
| 停止 | 只调用当前 Codex 对话的停止控件 |
| Bridge 审批测试批准 | 精确执行一次预期测试动作 |
| Bridge 审批测试拒绝 | 不执行测试动作 |
| 真实 Computer Use 普通批准 | 只选择“允许此对话” |
| 真实 Computer Use 拒绝 | 只拒绝当前审批 |
| 审批变化或已解决 | 旧手机操作失败，不重复执行 |

## 网络与 Android 实机矩阵

| 场景 | 必须结果 |
| --- | --- |
| 首次二维码/手动配对 | SPKI、主机、有效期和设备签名均通过后连接 |
| 错误 SPKI 或过期配对 | 连接硬失败，没有继续选项 |
| Wi-Fi 断开再恢复 | 自动重连，本轮从第 1 次计数 |
| 第二轮 Wi-Fi 断开再恢复 | 再次从第 1 次计数，不沿用上轮 |
| 红米 K80 Pro 前台 | 持续连接 |
| 红米 K80 Pro 后台/锁屏 | 配置自启动、电池无限制和后台锁定后持续监控 |
| 重连快照 | 不重复完整回复，不覆盖已保存长回复 |
| 覆盖安装 | 新 APK 使用相同包名与 signer，版本码更高，保留本地数据 |

## APK 签名门禁

```powershell
apksigner verify --print-certs .\CodexMicroMobile-vX.Y.Z.apk
```

v1.0.1 signer SHA-256 应为：

```text
B952979D47D4437B7BF694AB52B9F9165331EAD74EAF9A780E7B32F550FE7D9C
```

v1.0.0 使用不同 signer，不能直接覆盖安装 v1.0.1。v1.0.2 及以后必须保持与 v1.0.1 连续，并递增 `versionCode`。
