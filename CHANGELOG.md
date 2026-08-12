# Changelog

本项目采用[语义化版本](https://semver.org/lang/zh-CN/)和 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 风格记录正式版本。开发测试阶段的演进另见 `docs/Codex-Micro-开发测试版本演进记录.md`。

## [1.0.1] - 2026-08-12

### Fixed

- Computer Use 审批弹窗出现时优先识别审批，不再因为输入框被遮挡而误报服务降级。
- 手机普通批准只映射“允许此对话”，拒绝只作用于当前审批，不会误选“始终允许”。
- 顶部状态卡不再重复展示完整回复正文。
- 新增可点击的完整对话历史，并在重连或重启后恢复本地记录。
- Windows EXE、主窗口和托盘使用统一应用图标。

### Security

- 记录 v1.0.0 与 v1.0.1 APK 签名不连续的问题；从 v1.0.2 起将包名、签名指纹与递增 `versionCode` 列为发布硬门禁。

### Verification

- Windows 解决方案构建与桌面测试基线：70 项。
- Android JVM 测试基线：23 项；APK 构建和签名校验通过。
- 共享协议 fixtures 基线：38 cases / 1 pair。
- 真实 Computer Use 手机批准与拒绝仍需安装后的实机验收，自动测试不替代真实 UI 操作。

## [1.0.0] - 2026-08-11

### Added

- 手机消息发送到当前 Codex Desktop 对话。
- 完整长回复同步、Wi-Fi 自动重连与后台/锁屏监控。
- 手机审批测试链路，以及批准、拒绝的一次性执行保护。
- 白色 Material 3 界面和整卡状态颜色。

### Verification

- 连通性、长回复、两轮 Wi-Fi 重连、后台保活、锁屏保活和 Bridge 审批测试完成实机验收。

### Archive note

- 建仓时未找到可独立验证的 v1.0.0 精确源码快照。`v1.0.0` 标签记录已验收二进制的发布说明和校验值，不声明包含完整源码。
