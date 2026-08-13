# Security Policy

## 支持范围

当前稳定版本为 v1.0.6。它已完成自动验证和关键联合实机验收，但只保证单一当前桌面对话的基础链路；多个桌面对话同时活动时可能发生归属混流。v1.0.1–v1.0.5 只用于历史追溯，其中 v1.0.5 存在严重同步回归，不应继续使用。安全问题统一在新的补丁版本中处理，不回写或移动历史标签。

## 私下报告

请通过本私有仓库的私密协作渠道联系仓库所有者，不要在公开 Issue、公开聊天、截图或日志中披露漏洞细节、设备标识或凭据。如果以后开放 GitHub Private Vulnerability Reporting，应优先使用该入口。

报告请尽量包含：

- 受影响版本和 Android/Windows/Codex Desktop 版本；
- 最小复现步骤、预期结果和实际结果；
- 是否涉及错误目标、重复执行、审批范围扩大或凭据暴露；
- 已脱敏的日志或截图；
- 建议的临时缓解措施。

不要提交真实 Token、密码、私钥、证书私钥、Android keystore、配对码、配对数据库、完整本机路径、未脱敏会话内容或用户日志。

## 安全边界

- OpenAI/Codex 登录和 Token 只保留在 Windows 端。
- 手机和 Bridge 通过局域网 WSS、TLS SPKI 绑定和 P-256 设备签名建立信任。
- 写操作使用稳定 `clientCommandId`、epoch/seq 和审批指纹防止重放或错配。
- UI Automation 在执行前重新核验 Codex 进程、窗口、控件与焦点；无法证明目标时停止操作。
- 普通手机批准只能映射当前审批的“允许此对话”，不能扩大为“始终允许”。
- 本项目不声称能抵御已解锁手机或已登录 Windows 账户本身被完全控制后的攻击。

## 仓库敏感信息规则

以下内容禁止进入 Git 历史或 GitHub Release：

- OpenAI、GitHub 或其他服务的 Token；
- Android keystore、签名密码、私钥、PFX/PEM/KEY；
- Bridge SQLite/DPAPI 本地状态、证书、配对设备信息和日志；
- `android/local.properties`、本机 SDK 路径和服务账号 JSON；
- Codex 会话记录、项目路径白名单和未脱敏诊断包。

Release 只保存已验证的 APK、Windows ZIP、SHA-256 清单和版本说明。签名材料必须在仓库外安全备份，并通过本地忽略配置或 GitHub Actions Secrets 引用。
