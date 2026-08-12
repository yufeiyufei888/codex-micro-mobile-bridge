using System.Diagnostics;

namespace CodexMicroBridge.Core.AppServer;

public static class CodexCliVersionVerifier
{
    public const string PinnedVersion = "0.147.0-alpha.6.5";

    public static async Task<string> VerifyAsync(
        string executablePath,
        bool allowUnverifiedVersion = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start codex --version.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"codex --version failed ({process.ExitCode}): {error}");
        }

        ValidateOutput(output, allowUnverifiedVersion);
        return output;
    }

    public static void ValidateOutput(string output, bool allowUnverifiedVersion = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        var expected = $"codex-cli {PinnedVersion}";
        if (!string.Equals(output.Trim(), expected, StringComparison.Ordinal) && !allowUnverifiedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported Codex CLI. Expected exactly '{expected}', received '{output}'. " +
                "Regenerate and review the App Server schema before changing the pin.");
        }
    }
}
