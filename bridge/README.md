# Codex Micro Desktop Sync Bridge V2.0.3

V2.0.3 继续适配新版 Codex Desktop 标题栏，并过滤回复完成后短暂出现的“查看活动/需要关注”等动态文案。短暂无法唯一识别标题时，Bridge 保留上一次已确认会话以维持消息同步，同时将发送入口锁定；标题重新确认后自动恢复发送。Android V2.0.0 与本热修复兼容。

Windows Bridge 为 Android Codex Micro 客户端提供证书绑定、设备认证的局域网 WSS 服务，并将 Codex Desktop 当前与最近对话映射为彼此独立的手机会话。

## V2.0.0 基础行为（V2.0.3 继续保留）

- 当前可操作的可见 Codex 对话绑定到槽位 1；最近根会话最多占用其余槽位。
- 本地 JSONL 的 `task_started`、`task_complete` 和 `turn_aborted` 事件决定生命周期，不再以 UI 轮询误判运行状态。
- 回复和审批按稳定会话 ID 归属；后台会话事件不会改写当前会话。
- Goal、计划、提问等上下文即使没有标准编辑器，也保持同步服务可用，且不会错误显示为降级。
- UI Automation 均做窗口、控件、焦点和审批指纹复核；不满足条件时拒绝写入或批准。
- 诊断页保留“发送手机审批测试”，仅用于验证手机审批链路。

## 运行

先启动并登录 Codex Desktop，选择要控制的对话，再启动 `CodexMicroBridge.exe`。窗口关闭后会隐藏到通知区；同一 Windows 用户只允许一个 Bridge 实例。

端点为 `/v1/mobile`，默认仅绑定私有 Wi-Fi/Ethernet 地址，使用端口 47127 及 `_codexmicro._tcp` 发现提示。二维码和后续连接都必须通过 TLS SPKI 固定与设备签名认证。

## 构建与测试

```powershell
work\dotnet10\dotnet.exe test bridge\CodexMicroBridge.sln -c Release --no-restore
work\dotnet10\dotnet.exe publish bridge\src\CodexMicroBridge.App\CodexMicroBridge.App.csproj -c Release -r win-x64 --self-contained true --no-restore
```

自动化覆盖协议、认证、会话生命周期、消息归属、审批绑定及 UI 自动化的适配逻辑。实际 Codex Desktop 控件结构、真实手机连接和真实 Computer Use 审批仍需人工联机验收。
