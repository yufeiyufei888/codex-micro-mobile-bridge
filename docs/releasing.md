# 发布流程

本文定义 Codex Micro 的版本提交、标签和 GitHub Release 门禁。自动测试通过不等于实机验收通过；只有用户完成电脑与手机联合测试并明确确认后，才能正式发布新版本。

## 1. GitHub 身份与仓库门禁

任何创建仓库、推送、标签或 Release 写入之前，都必须重新核验本机 GitHub CLI 账号：

```powershell
$githubLogin = gh api user --jq .login
if ($githubLogin -ne 'yufeiyufei888') {
    throw "当前账号 $githubLogin 未获授权，停止 GitHub 写入。"
}
```

- 唯一授权账号：`yufeiyufei888`
- 仓库：`yufeiyufei888/codex-micro-mobile-bridge`
- 可见性：`PRIVATE`
- 默认分支：`main`
- 禁止向其他账号创建、推送、转移或发布仓库。

## 2. 版本号

每个新版本必须同时更新：

- Android `versionName`
- Android 递增的 `versionCode`
- Windows Bridge `Version`
- Windows `AssemblyVersion` 与 `FileVersion`
- README、CHANGELOG 和对应 `docs/releases/vX.Y.Z.md`

历史包名保持 `com.codexmicro.mobile.debug`。v1.0.1 及以后版本必须保持既有签名连续性；签名证书 SHA-256 为：

```text
B952979D47D4437B7BF694AB52B9F9165331EAD74EAF9A780E7B32F550FE7D9C
```

签名指纹可公开用于校验，但 keystore、密码和私钥禁止提交或上传。

## 3. 自动验证

在普通 ASCII 路径的干净仓库中执行：

```powershell
node .\shared\protocol-v1\validate-fixtures.mjs
dotnet test .\bridge\CodexMicroBridge.sln -c Release
.\android\gradlew.bat -p .\android --no-daemon testDebugUnitTest assembleDebug
```

记录实际测试数量和结果。构建成功、静态检查或编译通过不能替代真实测试执行。

发布前核验 APK：

- applicationId 与既有包名一致
- `versionName` 与标签一致
- `versionCode` 严格递增
- signer SHA-256 与既有发布密钥一致

任一项不符立即停止发布。

## 4. 用户实机验收

每个版本都必须由用户在真实 Windows Codex Desktop 和 Android 手机上测试。至少覆盖：

1. 手机发送本轮消息后，本轮回复无需第二次发送即可到达。
2. 电脑主动发送的消息和回复无需刷新、重连或手机主动发送即可到达。
3. 真实 Computer Use 批准和拒绝正确；批准只执行一次，拒绝不执行，审批完成后状态恢复。
4. 电脑与手机的桌面同步/降级状态一致。
5. Wi-Fi 重连、后台和锁屏连接符合该版本发布目标。

用户未明确确认时，只能保留源码提交或发布候选，不能创建正式标签、不能发布正式 Release、不能设为 Latest。

## 5. 提交与标签

- 一个版本只创建一次正式发布提交和一个不可移动的 annotated tag。
- 标签格式：`vX.Y.Z`。
- 禁止 force push、移动标签或重写已发布历史。
- 如果旧版本没有可独立验证的精确源码快照，只能建立明确标注的历史二进制归档，不能用当前源码改版本号伪造旧源码。

历史归档提交只包含：

```text
archive/vX.Y.Z/README.md
archive/vX.Y.Z/SHA256SUMS.txt
docs/releases/vX.Y.Z.md
```

## 6. Release 资产

每个正式 GitHub Release 固定上传：

```text
CodexMicroMobile-vX.Y.Z.apk
CodexMicroBridge-vX.Y.Z-zhCN-win-x64-desktop-sync.zip
SHA256SUMS.txt
```

- 二进制只进入 GitHub Releases，不进入 Git 历史。
- 历史版本必须使用本机保留的原始 APK 和对应 Windows 发布目录，不得把新版本产物改名伪装成旧版本。
- `SHA256SUMS.txt` 必须由该 Release 实际上传的 APK 和 ZIP 重新计算并复核。
- Release 正文使用 `docs/releases/vX.Y.Z.md`。
- 已知失败版本必须在标题或首段醒目标注；v1.0.5 必须写“严重同步回归，不建议使用”。

## 7. 提交前安全检查

确认 Git 只包含源码、测试、文档和历史校验文本，不包含：

- `work/`、`outputs/`、构建缓存、`bin/`、`obj/`、`build/`
- APK、EXE、ZIP、PDB、数据库、证书私钥、keystore、Token、日志
- Bridge 配对信息、真实设备 ID、私网地址或 Codex rollout 会话
- 本机绝对路径和未脱敏的用户内容

执行并逐条人工判断命中：

```powershell
git grep -n -I "C:\\Users\\yufei"
git grep -n -I -E "rollout-[0-9]|\.codex\\sessions|192\.168\.|47127"
git grep -n -I -E "(ghp_|github_pat_|sk-[A-Za-z0-9_-]+|BEGIN .*PRIVATE KEY|password|token)"
git diff --cached --check
git status --short
```

## 8. 发布后复核

- 再次核验仓库仍为 Private、默认分支仍为 `main`。
- 记录发布提交 SHA、标签 SHA 和 Release 链接。
- 下载或通过 API 核对每个 Release 的资产名、大小和 SHA-256。
- 在 CHANGELOG 和 Release Notes 中保留自动测试、实机测试和未完成项的真实边界。
