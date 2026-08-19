# Codex Micro v2.0.1 历史候选归档

这是根据本机保留的 Windows ZIP、版本号、校验值和发布说明建立的历史候选归档。
本仓库建立该记录时没有找到可独立验证的 v2.0.1 精确完整源码快照，因此本提交和标签不声明包含完整 v2.0.1 源码。
Windows ZIP 保存在同名 GitHub Pre-release 资产中，不进入 Git 历史。

## 版本事实

- Windows Bridge：`2.0.1`
- Android：继续使用 V2.0.0，协议、证书、配对与数据库兼容
- ZIP：`CodexMicroBridge-v2.0.1-zhCN-win-x64-desktop-sync.zip`
- ZIP 大小：`90,121,720 bytes`
- ZIP 内 `CodexMicroBridge.exe` SHA-256：`431BAF065CEB3B72DDFAD25A8907DD65CB2E00645BF387126C0B9F0DB158013F`

## 历史结论

该版修复旧数据库保留任务超过六条时首次快照失败、手机持续重连的问题。它已被 V2.0.2 和 V2.0.3 完整包含，只作为 Windows 历史热修复候选归档，不描述为当前稳定版本。

二进制校验见 [SHA256SUMS.txt](./SHA256SUMS.txt)，详细变化见 [v2.0.1 Release Notes](../../docs/releases/v2.0.1.md)。
