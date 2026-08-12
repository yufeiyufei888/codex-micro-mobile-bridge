# Codex Micro 发布流程

本流程适用于 v1.0.2 及以后正式版本。代码改完不等于可以发布；用户实机验收和明确确认是创建正式标签与 Release 的硬门禁。

## 1. 建立发布分支

从最新 `main` 创建 `release/vX.Y.Z`，或在功能开发阶段使用 `agent/<feature>`。不要直接在已发布标签上修改，也不要 force push 已发布历史。

## 2. 同步版本号

- Android `versionCode` 必须严格递增。
- Android `versionName` 改为目标 `X.Y.Z`。
- Windows `Version` 改为 `X.Y.Z`。
- Windows `AssemblyVersion` 和 `FileVersion` 改为 `X.Y.Z.0`。
- 更新 `CHANGELOG.md` 和 `docs/vX.Y.Z-release-notes.md`。

## 3. 自动验证

```powershell
node .\shared\protocol-v1\validate-fixtures.mjs
dotnet test .\bridge\CodexMicroBridge.sln -c Release

$env:JAVA_HOME = '<JDK 17 路径>'
$env:ANDROID_SDK_ROOT = '<Android SDK 路径>'
.\android\gradlew.bat -p .\android --no-daemon testDebugUnitTest assembleDebug
```

记录本次实际测试数量和失败/跳过项，不沿用旧版本的固定数字。静态检查、单元测试和构建成功不能写成真实手机、真实网络或真实 Codex UI 验收。

## 4. Android 签名连续性

v1.0.0 和 v1.0.1 的签名证书不同，导致 Android 拒绝覆盖安装。v1.0.2 起必须同时满足：

- 调试侧载包名保持 `com.codexmicro.mobile.debug`；
- 使用与 v1.0.1 相同的签名密钥；
- `versionCode` 大于上一正式版本；
- 使用 `apksigner verify --print-certs` 强制核验。

v1.0.1 预期 signer SHA-256：

```text
B952979D47D4437B7BF694AB52B9F9165331EAD74EAF9A780E7B32F550FE7D9C
```

签名密钥和密码不得提交到 Git。应在仓库外安全备份，并通过本地忽略配置引用。

## 5. 打包与校验

每个正式版本准备：

```text
CodexMicroMobile-vX.Y.Z.apk
CodexMicroBridge-vX.Y.Z-zhCN-win-x64-desktop-sync.zip
SHA256SUMS.txt
docs/vX.Y.Z-release-notes.md
```

Windows 自包含发布目录整体压缩为一个 ZIP，不把数百个运行文件逐个提交到 Git。`SHA256SUMS.txt` 只写哈希和文件名，不写本机绝对路径。

## 6. 用户实机验收

至少验证：

- 手机和电脑完成配对、绿色连接和普通消息发送；
- 电脑直接输入消息时手机保持连接；
- 长回复末尾完整显示；
- 两轮 Wi-Fi 断开和恢复都从第 1 次重新计算重连；
- 红米 K80 Pro 前台、后台和锁屏保活；
- 最近回复不在顶部状态卡重复，重连后回复不重复，历史可查看；
- Bridge 自带审批测试的批准、拒绝和一次性执行；
- 真实 Codex Computer Use 审批的批准与拒绝；
- 普通批准不会误触“始终允许”；
- Windows EXE、主窗口和托盘图标；
- 新 APK 能覆盖安装上一正式版本，签名和版本码均符合门禁。

## 7. 正式发布

只有用户明确回复“测试正常”或确认发布后，才可以：

1. 合并发布分支到 `main`；
2. 创建 annotated tag：`git tag -a vX.Y.Z -m "Codex Micro vX.Y.Z"`；
3. 推送 `main` 和标签；
4. 创建非 prerelease 的 GitHub Release；
5. 上传 APK、Windows ZIP 和 `SHA256SUMS.txt`。

发布后再次核验账号为 `yufeiyufei888`、仓库为 Private、默认分支为 `main`、标签和 Release 资产完整、远程哈希与本地一致。已发布标签禁止移动；出现问题时发布新的补丁版本。
