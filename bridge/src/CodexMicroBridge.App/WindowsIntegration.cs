using System.Diagnostics;
using Microsoft.Win32;

namespace CodexMicroBridge.App;

public static class WindowsStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexMicroBridge";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The bridge executable path is unavailable.");
        key.SetValue(ValueName, $"\"{executable}\" --minimized", RegistryValueKind.String);
    }
}

public static class FirewallDiagnostics
{
    public static async Task<string> GetPrivateProfileStatusAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        var script = $$"""
            $hasPrivate = @(
              Get-NetConnectionProfile -ErrorAction SilentlyContinue |
                Where-Object { $_.NetworkCategory -eq 'Private' }
            ).Count -gt 0
            $rules = @(
              Get-NetFirewallRule -DisplayName 'Codex Micro Bridge' -ErrorAction SilentlyContinue |
                Where-Object {
                  $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and
                  $_.Action -eq 'Allow' -and "$($_.Profile)" -match 'Private|Any'
                }
            )
            $portMatch = $false
            foreach ($rule in $rules) {
              $filters = @(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue)
              if (@($filters | Where-Object { $_.Protocol -eq 'TCP' -and $_.LocalPort -eq '{{port}}' }).Count -gt 0) {
                $portMatch = $true
              }
            }
            Write-Output "$hasPrivate|$portMatch"
            """;
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell did not start.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return "防火墙状态检查超时；仍可使用手动地址和下方命令。";
            }

            var values = (await outputTask.ConfigureAwait(false)).Trim().Split('|');
            if (process.ExitCode != 0 || values.Length != 2 ||
                !bool.TryParse(values[0], out var hasPrivate) ||
                !bool.TryParse(values[1], out var hasMatchingRule))
            {
                return "无法读取防火墙状态；请手动检查并按需运行下方命令。";
            }

            return (hasPrivate, hasMatchingRule) switch
            {
                (true, true) => $"已检测到专用网络；TCP/{port} 入站允许规则已启用。",
                (true, false) => $"已检测到专用网络；未找到启用的 TCP/{port} 入站允许规则。",
                (false, _) => "未检测到 Windows 专用网络配置文件，局域网访问可能被阻止。",
            };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return "无法读取防火墙状态；请手动检查并按需运行下方命令。";
        }
    }

    public static string CreatePrivateProfileCommand(int port)
    {
        var executable = Environment.ProcessPath ?? "CodexMicroBridge.exe";
        return $"New-NetFirewallRule -DisplayName 'Codex Micro Bridge' -Direction Inbound -Action Allow " +
            $"-Protocol TCP -LocalPort {port} -Profile Private -Program '{executable.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
