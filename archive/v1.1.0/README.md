# Codex Micro v1.1.0 历史候选归档

这是根据本机保留的原始 APK、Windows ZIP、版本号、校验值和发布说明建立的历史候选归档。
本仓库建立该记录时没有找到可独立验证的 v1.1.0 精确完整源码快照，因此本提交和标签不声明包含完整 v1.1.0 源码。
Android APK 与 Windows ZIP 保存在同名 GitHub Pre-release 资产中，不进入 Git 历史。

## 版本事实

- Android：`versionName 1.1.0`，`versionCode 16`
- APK 包名：`com.codexmicro.mobile.debug`
- APK 签名与 v1.0.1–v1.0.6 连续
- Windows Bridge：`1.1.0.0`

## 历史结论

该版首次尝试按稳定 threadId 隔离多个 root 对话，并在手机显示当前对话与最多五个最近对话。原发布说明明确记录联合实机验收未完成；后续仍发现会话归属问题，因此只能作为多对话开发候选归档，不能描述为稳定版本。

二进制校验见 [SHA256SUMS.txt](./SHA256SUMS.txt)，详细变化见 [v1.1.0 Release Notes](../../docs/releases/v1.1.0.md)。
