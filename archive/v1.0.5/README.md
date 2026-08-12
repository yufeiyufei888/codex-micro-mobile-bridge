# Codex Micro v1.0.5 历史归档

> **严重同步回归，不建议使用。**

这是根据本机保留的已发布二进制、版本号、校验值、发布说明和实机记录建立的历史归档。
本仓库建立时没有找到可独立验证的该版本完整源码快照，因此该标签不声明包含完整源码。
Android APK 与 Windows ZIP 保存在同名 GitHub Release 资产中。

## 版本事实

- Android：`versionName 1.0.5`，`versionCode 14`
- Windows Bridge：`1.0.5.0`
- 自动验证基线：Windows 80、Android 29、protocol 38 cases / 1 pair
- APK 包名：`com.codexmicro.mobile.debug`

## 严重回归

活动 rollout 文件的 Windows 共享模式冲突使正确 root 会话被误判；手机发送后又提前清除当前会话绑定和游标，导致现有会话同步在提示匹配期间暂停。实机最终表现为手机和电脑发送的新消息与回复都无法在手机显示。

二进制校验见 [SHA256SUMS.txt](./SHA256SUMS.txt)，详细说明见 [v1.0.5 Release Notes](../../docs/releases/v1.0.5.md)。
