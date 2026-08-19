# Codex Micro v2.0.2 历史候选归档

这是根据本机保留的 Windows ZIP、版本号、校验值、发布说明和实机回归记录建立的历史候选归档。
本仓库建立该记录时没有找到可独立验证的 v2.0.2 精确完整源码快照，因此本提交和标签不声明包含完整 v2.0.2 源码。
Windows ZIP 保存在同名 GitHub Pre-release 资产中，不进入 Git 历史。

## 版本事实

- Windows Bridge：`2.0.2`
- Android：继续使用 V2.0.0，协议、证书、配对与数据库兼容
- ZIP：`CodexMicroBridge-v2.0.2-zhCN-win-x64-desktop-sync.zip`
- ZIP 大小：`90,122,208 bytes`
- ZIP 内 `CodexMicroBridge.exe` SHA-256：`64C9C08D6DC4798468919FFAE4FDD35785DABA0CE3ED582A81EBDA5F2EA710CE`

## 已知回归与取代状态

该版能识别新版 Codex Desktop 的常规标题栏，但回复完成后可能把“查看活动，需要关注”等动态入口误认为对话标题，导致手机错误显示“电脑服务降级”。V2.0.2 已被 V2.0.3 取代，不建议使用，也不能标为稳定版或 Latest。

二进制校验见 [SHA256SUMS.txt](./SHA256SUMS.txt)，详细变化见 [v2.0.2 Release Notes](../../docs/releases/v2.0.2.md)。
