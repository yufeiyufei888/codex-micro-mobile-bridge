using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CodexMicroBridge.App;

public partial class App : System.Windows.Application
{
    private BridgeRuntime? _runtime;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private SingleInstanceGuard? _singleInstance;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!SingleInstanceGuard.TryAcquire(out _singleInstance))
        {
            System.Windows.MessageBox.Show(
                "当前 Windows 用户已经运行了 Codex Micro 桌面桥接。请从通知区域打开已有窗口。",
                "Codex Micro 桌面桥接",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _runtime = new BridgeRuntime(ParseOptions(e.Args));
        _mainWindow = new MainWindow(_runtime, () => _exiting);
        MainWindow = _mainWindow;
        CreateTrayIcon();
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            _mainWindow.Show();
        }

        try
        {
            await _runtime.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                _mainWindow,
                $"桥接程序启动失败。\n\n{exception.Message}",
                "Codex Micro 桌面桥接",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static BridgeRuntimeOptions ParseOptions(IReadOnlyList<string> arguments)
    {
        string? codexExecutablePath = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--codex-executable", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
            {
                codexExecutablePath = arguments[++index];
            }
            else if (arguments[index].StartsWith("--codex-executable=", StringComparison.OrdinalIgnoreCase))
            {
                codexExecutablePath = arguments[index]["--codex-executable=".Length..];
            }
        }

        return new BridgeRuntimeOptions { CodexExecutablePath = codexExecutablePath };
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Environment.ProcessPath is { } executablePath
                ? Icon.ExtractAssociatedIcon(executablePath) ?? SystemIcons.Application
                : SystemIcons.Application,
            Text = "Codex Micro 桌面桥接",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开桌面桥接", null, (_, _) => ShowWindow());
        menu.Items.Add("开放 60 秒配对", null, (_, _) =>
        {
            ShowWindow();
            _runtime?.OpenPairingWindow();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync().ConfigureAwait(true));
        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(true);
        }

        _singleInstance?.Dispose();
        _singleInstance = null;

        Shutdown();
    }
}
