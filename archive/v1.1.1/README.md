# Codex Micro v1.1.1 历史热修复候选归档

这是根据本机保留的原始 APK、Windows ZIP、版本号、校验值和发布说明建立的历史候选归档。
本仓库建立该记录时没有找到可独立验证的 v1.1.1 精确完整源码快照，因此本提交和标签不声明包含完整 v1.1.1 源码。
Android APK 与 Windows ZIP 保存在同名 GitHub Pre-release 资产中，不进入 Git 历史。

## 版本事实

- Android：`versionName 1.1.1`，`versionCode 17`
- APK 包名：`com.codexmicro.mobile.debug`
- APK 签名与 v1.0.1–v1.1.0 连续
- Windows Bridge：`1.1.1.0`
- Windows 自动验证记录：85/85

## 历史结论

该版针对 Goal、计划和计划提问界面的错误降级及后台对话抢绑定进行热修复，但后续真实反馈仍出现部分界面降级、当前对话抢占和任务状态串线。因此它是不完善的候选热修复，不建议作为稳定版使用。

二进制校验见 [SHA256SUMS.txt](./SHA256SUMS.txt)，详细变化见 [v1.1.1 Release Notes](../../docs/releases/v1.1.1.md)。
