using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace CodexMicroBridge.App;

internal sealed record DesktopApprovalTarget(
    string Fingerprint,
    string AllowConversationName,
    string? AlwaysAllowName,
    string? DeclineName,
    string Summary);

internal enum DesktopApprovalButtonKind
{
    None,
    AllowConversation,
    AlwaysAllow,
    Decline,
}

internal sealed record DesktopCodexInspection(
    bool Available,
    bool CanSend,
    bool IsRunning,
    string ConversationTitle,
    DesktopApprovalTarget? Approval,
    string? Error);

internal sealed class DesktopCodexAutomation
{
    private static readonly string[] SendButtonNames =
    [
        "发送", "发送消息", "Send", "Send message",
    ];

    private static readonly string[] AllowConversationButtonNames =
    [
        "允许此对话", "允许本次对话", "仅允许此对话",
        "Allow for this conversation", "Allow this conversation",
        "批准", "允许", "确认", "继续", "运行", "运行一次", "允许一次", "批准一次", "继续执行",
        "Approve", "Allow", "Confirm", "Continue", "Run",
    ];

    private static readonly string[] AlwaysAllowButtonNames =
    [
        "始终允许", "Always allow",
    ];

    private static readonly string[] DeclineButtonNames =
    [
        "拒绝", "取消", "不允许", "否", "Decline", "Deny", "Cancel",
    ];

    private static readonly string[] ApprovalKeywords =
    [
        "审批", "批准", "确认", "允许", "权限", "运行命令", "文件更改", "网络访问",
        "approval", "approve", "permission", "confirm", "allow command", "computer use",
    ];

    private static readonly HashSet<string> ApprovalSummaryChromeText = new(StringComparer.OrdinalIgnoreCase)
    {
        "确认", "权限", "审批", "批准", "待批准", "等待批准", "允许", "拒绝", "取消",
        "Computer Use", "Confirm", "Permission", "Approval", "Approve", "Allow", "Deny", "Cancel",
    };

    private static readonly HashSet<string> HeaderChromeText = new(StringComparer.OrdinalIgnoreCase)
    {
        "Codex", "ChatGPT", "打开位置", "Open location", "更多", "More", "最小化", "最大化", "关闭",
    };

    public DesktopCodexInspection Inspect()
    {
        try
        {
            var contexts = FindContexts();
            foreach (var approvalContext in contexts)
            {
                var approvalButtons = FindVisibleDescendants(approvalContext.Root, ControlType.Button);
                var detectedApproval = FindApproval(approvalContext, approvalButtons);
                if (detectedApproval is null)
                {
                    continue;
                }

                var approvalRunning = approvalButtons.Any(element => NameEquals(element, "停止") || NameEquals(element, "Stop"));
                var approvalTitle = FindConversationTitle(approvalContext) ?? "当前桌面对话";
                return new DesktopCodexInspection(
                    Available: true,
                    CanSend: approvalContext.Input is not null,
                    IsRunning: approvalRunning,
                    ConversationTitle: approvalTitle,
                    Approval: detectedApproval,
                    Error: null);
            }

            // A trusted, visible Codex window remains online when Goal/Plan mode adds a
            // panel above the composer or replaces the composer with a user question.
            // Writable composer availability is a narrower capability than bridge health.
            var context = contexts.FirstOrDefault();
            if (context is null)
            {
                var windowVisible = HasTrustedVisibleCodexWindow();
                return new DesktopCodexInspection(
                    Available: windowVisible,
                    CanSend: false,
                    IsRunning: windowVisible,
                    ConversationTitle: "当前桌面对话",
                    Approval: null,
                    Error: windowVisible
                        ? "Codex 目标/计划面板正在刷新，普通输入框暂不可用。"
                        : "未找到可见的 Codex 桌面窗口。请打开目标对话并保持窗口未最小化。");
            }

            var buttons = FindVisibleDescendants(context.Root, ControlType.Button);
            var hasStopButton = buttons.Any(element => NameEquals(element, "停止") || NameEquals(element, "Stop"));
            var approval = FindApproval(context, buttons);
            var title = FindConversationTitle(context) ?? "当前桌面对话";
            // With no normal composer and no approval control, Codex is usually running
            // or displaying a Plan-mode question. Preserve an active state instead of
            // incorrectly completing the task or degrading the whole bridge.
            var running = hasStopButton || (context.Input is null && approval is null);
            return new DesktopCodexInspection(
                Available: true,
                CanSend: context.Input is not null,
                IsRunning: running,
                ConversationTitle: title,
                Approval: approval,
                Error: context.Input is null ? "当前对话正在执行或等待桌面回答，普通输入框暂不可用。" : null);
        }
        catch (ElementNotAvailableException)
        {
            return InspectionFailure("Codex 窗口正在切换，请稍后重试。");
        }
        catch (InvalidOperationException exception)
        {
            return InspectionFailure(exception.Message);
        }
        catch (ArgumentOutOfRangeException)
        {
            return InspectionFailure("Codex 控件树正在刷新，桌面窗口仍保持在线。");
        }
    }

    private static DesktopCodexInspection InspectionFailure(string error)
    {
        // Chromium can rebuild its accessibility tree while Goal/Plan panels or
        // question cards are inserted. A failed tree read does not mean the trusted
        // desktop process/window or the WSS control plane went offline.
        var windowVisible = HasTrustedVisibleCodexWindow();
        return new DesktopCodexInspection(
            Available: windowVisible,
            CanSend: false,
            IsRunning: windowVisible,
            ConversationTitle: "当前桌面对话",
            Approval: null,
            Error: error);
    }

    public void SendMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var context = FindContext(requireInput: true)
            ?? throw new InvalidOperationException("未找到可用的 Codex 桌面输入框。请先打开目标对话并保持窗口未最小化。");
        var input = context.Input
            ?? throw new InvalidOperationException("当前 Codex 对话没有可用输入框，可能正在显示其他页面。");

        ActivateAndVerify(context);
        input.SetFocus();
        if (!input.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern) || rawPattern is not ValuePattern valuePattern)
        {
            throw new InvalidOperationException("Codex 输入框暂不支持安全写入，请更新桌面客户端后重试。");
        }

        valuePattern.SetValue(message);
        if (!WaitForInputWrite(valuePattern, message))
        {
            throw new InvalidOperationException("Codex 输入框内容校验失败，消息未发送。");
        }

        if (GetForegroundWindow() != context.WindowHandle || !input.Current.HasKeyboardFocus)
        {
            valuePattern.SetValue(string.Empty);
            throw new InvalidOperationException("发送前 Codex 输入框失去焦点，已取消以避免误发到其他软件。");
        }

        var sendButton = FindSendButton(context, input);
        if (sendButton is null)
        {
            valuePattern.SetValue(string.Empty);
            throw new InvalidOperationException("未找到当前输入框对应的 Codex 发送按钮，消息未发送。");
        }
        if (!sendButton.TryGetCurrentPattern(InvokePattern.Pattern, out var sendPattern) || sendPattern is not InvokePattern invoke)
        {
            valuePattern.SetValue(string.Empty);
            throw new InvalidOperationException("Codex 发送按钮暂不支持安全调用，消息未发送。");
        }

        invoke.Invoke();
        if (!WaitForMessageCommit(valuePattern, message))
        {
            throw new InvalidOperationException("已调用 Codex 发送按钮，但桌面端未确认消息已发送；请检查当前输入框后重试。");
        }
    }

    public void StopCurrentTurn()
    {
        var context = FindContext(requireInput: false)
            ?? throw new InvalidOperationException("未找到正在运行的 Codex 桌面窗口。");
        var stop = FindVisibleDescendants(context.Root, ControlType.Button)
            .FirstOrDefault(element => NameEquals(element, "停止") || NameEquals(element, "Stop"))
            ?? throw new InvalidOperationException("当前桌面对话没有正在执行的任务。");
        ActivateAndVerify(context);
        InvokeVerifiedButton(context, stop);
    }

    public void ResolveApproval(string fingerprint, bool approve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        DesktopContext? context = null;
        DesktopApprovalTarget? approval = null;
        foreach (var candidate in FindContexts())
        {
            var detected = FindApproval(candidate, FindVisibleDescendants(candidate.Root, ControlType.Button));
            if (detected is null)
            {
                continue;
            }

            context = candidate;
            approval = detected;
            break;
        }
        if (context is null || approval is null)
        {
            throw new InvalidOperationException("当前桌面对话已经没有待确认权限。");
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(approval.Fingerprint),
                Encoding.UTF8.GetBytes(fingerprint)))
        {
            throw new InvalidOperationException("桌面审批界面已经变化，请刷新手机后重新确认。");
        }

        var requestedName = approve ? approval.AllowConversationName : approval.DeclineName;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            throw new InvalidOperationException(approve ? "当前审批没有可用的批准按钮。" : "当前审批没有可用的拒绝按钮。");
        }

        var target = FindVisibleDescendants(context.Root, ControlType.Button)
            .FirstOrDefault(element => NameEquals(element, requestedName))
            ?? throw new InvalidOperationException("桌面审批按钮已经失效，请刷新手机后重试。");
        ActivateAndVerify(context);
        InvokeVerifiedButton(context, target);
    }

    private static DesktopContext? FindContext(bool requireInput)
    {
        return FindContexts()
            .Where(context => !requireInput || context.Input is not null)
            .FirstOrDefault();
    }

    private static IReadOnlyList<DesktopContext> FindContexts()
    {
        var foregroundWindow = GetForegroundWindow();
        var candidates = new List<DesktopContext>();
        foreach (var process in Process.GetProcessesByName("ChatGPT")
                     .OrderByDescending(candidate => candidate.StartTime))
        {
            try
            {
                if (!IsTrustedCodexProcess(process))
                {
                    continue;
                }

                foreach (var windowHandle in EnumerateTopLevelWindows(process.Id)
                             .Where(handle => IsWindowVisible(handle) && !IsIconic(handle))
                             .OrderByDescending(handle => handle == foregroundWindow))
                {
                    var root = AutomationElement.FromHandle(windowHandle);
                    var rootCurrent = root.Current;
                    if (!IsTrustedCodexWindowIdentity(rootCurrent.Name, rootCurrent.ClassName))
                    {
                        continue;
                    }

                    var input = FindVisibleDescendants(root, ControlType.Edit)
                        .Where(IsPromptInput)
                        .OrderByDescending(element => element.Current.BoundingRectangle.Bottom)
                        .FirstOrDefault();
                    candidates.Add(new DesktopContext(process.Id, windowHandle, root, input));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               ElementNotAvailableException or
                                               ArgumentOutOfRangeException or
                                               System.ComponentModel.Win32Exception)
            {
                continue;
            }
        }

        return candidates
            .OrderByDescending(context => context.WindowHandle == foregroundWindow)
            .ThenByDescending(context => context.Input is not null)
            .ToArray();
    }

    private static bool HasTrustedVisibleCodexWindow()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                if (IsTrustedCodexProcess(process) &&
                    EnumerateTopLevelWindows(process.Id).Any(handle => IsWindowVisible(handle) && !IsIconic(handle)))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               ArgumentOutOfRangeException or
                                               System.ComponentModel.Win32Exception)
            {
                continue;
            }
        }

        return false;
    }

    private static bool IsTrustedCodexProcess(Process process)
    {
        if (!string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = process.MainModule?.FileName;
        return path is not null && IsTrustedCodexExecutablePath(path);
    }

    internal static bool IsTrustedCodexExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var windowsAppsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps") + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(windowsAppsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = fullPath[windowsAppsRoot.Length..];
        return relativePath.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
            relativePath.EndsWith($"{Path.DirectorySeparatorChar}app{Path.DirectorySeparatorChar}ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTrustedCodexWindowIdentity(string name, string className) =>
        (string.Equals(name, "Codex", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name, "ChatGPT", StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal);

    private static IReadOnlyList<IntPtr> EnumerateTopLevelWindows(int processId)
    {
        var windows = new List<IntPtr>();
        _ = EnumWindows((windowHandle, callbackState) =>
        {
            _ = callbackState;
            _ = GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
            if (ownerProcessId == processId)
            {
                windows.Add(windowHandle);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool IsPromptInput(AutomationElement element)
    {
        var current = element.Current;
        if (!current.IsEnabled || !current.IsKeyboardFocusable || current.IsOffscreen || current.BoundingRectangle.IsEmpty)
        {
            return false;
        }

        return current.ClassName.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase) &&
            (current.Name.Contains("随心输入", StringComparison.OrdinalIgnoreCase) ||
             current.Name.Contains("Message", StringComparison.OrdinalIgnoreCase) ||
             current.Name.Contains("输入", StringComparison.OrdinalIgnoreCase));
    }

    private static DesktopApprovalTarget? FindApproval(DesktopContext context, IReadOnlyList<AutomationElement> buttons)
    {
        var classified = buttons
            .Select(element => (Element: element, Kind: ClassifyApprovalButtonName(element.Current.Name)))
            .Where(entry => entry.Kind != DesktopApprovalButtonKind.None)
            .ToArray();
        var accept = classified.FirstOrDefault(entry => entry.Kind == DesktopApprovalButtonKind.AllowConversation).Element;
        if (accept is null)
        {
            return null;
        }

        var visibleText = FindVisibleDescendants(context.Root, ControlType.Text)
            .Select(element => element.Current.Name.Trim())
            .Where(text => text.Length is > 0 and <= 1000)
            .Where(text => ApprovalKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(8)
            .ToArray();
        if (visibleText.Length == 0)
        {
            return null;
        }

        var alwaysAllow = classified.FirstOrDefault(entry => entry.Kind == DesktopApprovalButtonKind.AlwaysAllow).Element;
        var decline = classified.FirstOrDefault(entry => entry.Kind == DesktopApprovalButtonKind.Decline).Element;
        var acceptCurrent = accept.Current;
        var runtimeId = string.Join('.', accept.GetRuntimeId());
        var rectangle = acceptCurrent.BoundingRectangle;
        var material = string.Join('|',
            context.ProcessId,
            context.WindowHandle.ToInt64(),
            runtimeId,
            acceptCurrent.Name,
            alwaysAllow?.Current.Name,
            decline?.Current.Name,
            acceptCurrent.AutomationId,
            acceptCurrent.ClassName,
            rectangle.X,
            rectangle.Y,
            rectangle.Width,
            rectangle.Height,
            string.Join('\n', visibleText));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        var summary = BuildApprovalSummary(visibleText);
        return new DesktopApprovalTarget(
            fingerprint,
            acceptCurrent.Name,
            alwaysAllow?.Current.Name,
            decline?.Current.Name,
            summary);
    }

    internal static string BuildApprovalSummary(IEnumerable<string> visibleText)
    {
        var meaningfulLines = visibleText
            .SelectMany(text => NormalizeComposerText(text).Split('\n'))
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0)
            .Where(line => !ApprovalSummaryChromeText.Contains(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var permissionQuestion = meaningfulLines
            .LastOrDefault(line =>
                (line.Contains("允许", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Allow", StringComparison.OrdinalIgnoreCase)) &&
                (line.Contains("使用", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("use", StringComparison.OrdinalIgnoreCase)) &&
                (line.EndsWith('?') || line.EndsWith('？')));
        var summary = permissionQuestion is not null
            ? SimplifyPermissionQuestion(permissionQuestion)
            : meaningfulLines.Length == 0
                ? "请确认是否允许当前 Codex 使用电脑功能。"
                : string.Join("\n", meaningfulLines.TakeLast(2));
        return summary.Length <= 4000 ? summary : summary[..4000];
    }

    private static string SimplifyPermissionQuestion(string question)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            question,
            @"(?:允许\s*(?:ChatGPT|Codex)?\s*使用|Allow\s+(?:ChatGPT|Codex)?\s*to\s+use)\s*(?<target>.+?)[?？]?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return question;
        }

        var target = match.Groups["target"].Value.Trim();
        return string.IsNullOrWhiteSpace(target)
            ? "请求使用电脑功能。"
            : $"请求使用电脑功能：{target}。";
    }

    internal static DesktopApprovalButtonKind ClassifyApprovalButtonName(string name)
    {
        var normalized = name.Trim();
        if (AlwaysAllowButtonNames.Any(candidate => NameStartsWithLabel(normalized, candidate)))
        {
            return DesktopApprovalButtonKind.AlwaysAllow;
        }
        if (AllowConversationButtonNames.Any(candidate => NameStartsWithLabel(normalized, candidate)))
        {
            return DesktopApprovalButtonKind.AllowConversation;
        }
        if (DeclineButtonNames.Any(candidate => NameStartsWithLabel(normalized, candidate)))
        {
            return DesktopApprovalButtonKind.Decline;
        }

        return DesktopApprovalButtonKind.None;
    }

    private static bool NameStartsWithLabel(string actual, string label)
    {
        if (string.Equals(actual, label, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!actual.StartsWith(label, StringComparison.OrdinalIgnoreCase) || actual.Length <= label.Length)
        {
            return false;
        }

        return char.IsWhiteSpace(actual[label.Length]) || actual[label.Length] is '↵' or '(' or '[';
    }

    private static AutomationElement? FindSendButton(DesktopContext context, AutomationElement input)
    {
        var inputBounds = input.Current.BoundingRectangle;
        return FindVisibleDescendants(context.Root, ControlType.Button)
            .Where(element => IsSendButtonName(element.Current.Name))
            .Where(element =>
            {
                var bounds = element.Current.BoundingRectangle;
                return bounds.Left >= inputBounds.Right - 180 &&
                    bounds.Left <= inputBounds.Right + 80 &&
                    bounds.Top >= inputBounds.Top - 20 &&
                    bounds.Top <= inputBounds.Bottom + 120;
            })
            .OrderBy(element => Math.Abs(element.Current.BoundingRectangle.Right - inputBounds.Right))
            .FirstOrDefault();
    }

    internal static bool IsSendButtonName(string name) =>
        SendButtonNames.Any(candidate => string.Equals(name.Trim(), candidate, StringComparison.OrdinalIgnoreCase));

    internal static bool ComposerTextEquals(string actual, string expected) =>
        string.Equals(NormalizeComposerText(actual), NormalizeComposerText(expected), StringComparison.Ordinal);

    private static string NormalizeComposerText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool WaitForInputWrite(ValuePattern valuePattern, string message)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (ComposerTextEquals(valuePattern.Current.Value, message))
                {
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private static bool WaitForMessageCommit(ValuePattern valuePattern, string message)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (!ComposerTextEquals(valuePattern.Current.Value, message))
                {
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
                // Codex recycles the ProseMirror element after a successful submit.
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static string? FindConversationTitle(DesktopContext context)
    {
        var rootBounds = context.Root.Current.BoundingRectangle;
        var candidates = FindVisibleDescendants(context.Root, ControlType.Text)
            .Concat(FindVisibleDescendants(context.Root, ControlType.Button))
            .Where(element =>
            {
                var bounds = element.Current.BoundingRectangle;
                return bounds.Top >= rootBounds.Top + 28 &&
                    bounds.Top <= rootBounds.Top + 125 &&
                    bounds.Left >= rootBounds.Left + Math.Min(300, rootBounds.Width * 0.15) &&
                    bounds.Left <= rootBounds.Right - Math.Min(320, rootBounds.Width * 0.16) &&
                    element.Current.Name.Length is > 0 and <= 200;
            })
            .Select(element => new
            {
                Text = element.Current.Name.Trim(),
                Bounds = element.Current.BoundingRectangle,
            })
            .Where(candidate => !HeaderChromeText.Contains(candidate.Text))
            .Where(candidate => candidate.Text is not "…" and not "...")
            .OrderBy(candidate => candidate.Bounds.Left)
            .ThenBy(candidate => candidate.Bounds.Top)
            .Select(candidate => candidate.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.FirstOrDefault();
    }

    private static List<AutomationElement> FindVisibleDescendants(AutomationElement root, ControlType controlType)
    {
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, controlType);
        var collection = root.FindAll(TreeScope.Descendants, condition);
        var result = new List<AutomationElement>(collection.Count);
        for (var index = 0; index < collection.Count; index++)
        {
            try
            {
                var element = collection[index];
                var current = element.Current;
                if (current.IsEnabled && !current.IsOffscreen && !current.BoundingRectangle.IsEmpty)
                {
                    result.Add(element);
                }
            }
            catch (ElementNotAvailableException)
            {
                // The web view can recycle virtualized elements while the tree is enumerated.
            }
            catch (ArgumentOutOfRangeException)
            {
                // Chromium can invalidate the UIA collection count while a virtualized subtree
                // is being replaced. Stop this snapshot and retry on the next inspection.
                break;
            }
        }

        return result;
    }

    private static bool NameEquals(AutomationElement element, string expected) =>
        string.Equals(element.Current.Name.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static void ActivateAndVerify(DesktopContext context)
    {
        _ = ShowWindow(context.WindowHandle, ShowRestore);
        if (!SetForegroundWindow(context.WindowHandle))
        {
            throw new InvalidOperationException("无法激活 Codex 桌面窗口，请先在电脑上点开目标对话。");
        }

        Thread.Sleep(120);
        if (GetForegroundWindow() != context.WindowHandle)
        {
            throw new InvalidOperationException("Codex 桌面窗口未处于前台，已取消操作以避免误发。");
        }
    }

    private static void InvokeVerifiedButton(DesktopContext context, AutomationElement element)
    {
        if (GetForegroundWindow() != context.WindowHandle || element.Current.IsOffscreen || !element.Current.IsEnabled)
        {
            throw new InvalidOperationException("Codex 审批控件已失效，请刷新后重试。");
        }

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) && pattern is InvokePattern invoke)
        {
            invoke.Invoke();
            return;
        }

        element.SetFocus();
        if (!element.Current.HasKeyboardFocus || GetForegroundWindow() != context.WindowHandle)
        {
            throw new InvalidOperationException("无法安全聚焦 Codex 审批按钮。");
        }

        PressVirtualKey(VirtualKeyEnter);
    }

    private static void PressVirtualKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey } } },
            new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = KeyEventKeyUp } } },
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            throw new InvalidOperationException("Windows 没有接受键盘操作，消息未发送。");
        }
    }

    private sealed record DesktopContext(
        int ProcessId,
        IntPtr WindowHandle,
        AutomationElement Root,
        AutomationElement? Input);

    private const int ShowRestore = 9;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyEnter = 0x0D;

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);
}
