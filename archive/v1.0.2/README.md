# Codex Micro v1.0.2 历史归档

这是根据本机保留的已发布二进制、版本号、校验值、发布说明和实机记录建立的历史归档。
本仓库建立时没有找到可独立验证的该版本完整源码快照，因此该标签不声明包含完整源码。
Android APK 与 Windows ZIP 保存在同名 GitHub Release 资产中。

## 版本事实

- Android：`versionName 1.0.2`，`versionCode 11`
- Windows Bridge：`1.0.2.0`
- APK 包名：`com.codexmicro.mobile.debug`
- APK 与 v1.0.1 使用相同签名，可覆盖安装并保留配对和历史数据

## 历史结论

该版引入 rollout 文件监听和 canonical 消息去重，但实机仍存在新回复不能即时到达手机、审批结束后状态不恢复的问题，不推荐安装。

二进制校验见 [SHA256SUMS.txt](./SHA256SUMS.txt)，详细说明见 [v1.0.2 Release Notes](../../docs/releases/v1.0.2.md)。
