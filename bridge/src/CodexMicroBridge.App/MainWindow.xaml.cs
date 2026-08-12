using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexMicroBridge.Core.Persistence;
using QRCoder;
using Forms = System.Windows.Forms;

namespace CodexMicroBridge.App;

public partial class MainWindow : Window
{
    private readonly BridgeRuntime _runtime;
    private readonly Func<bool> _isExiting;
    private readonly DispatcherTimer _timer;

    public MainWindow(BridgeRuntime runtime, Func<bool> isExiting)
    {
        InitializeComponent();
        _runtime = runtime;
        _isExiting = isExiting;
        _runtime.RuntimeChanged += RuntimeChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshPairingCountdown();
        _timer.Start();
        Loaded += async (_, _) =>
        {
            await RefreshAsync().ConfigureAwait(true);
            FirewallStatusText.Text = await FirewallDiagnostics
                .GetPrivateProfileStatusAsync(BridgeRuntime.DefaultPort)
                .ConfigureAwait(true);
        };
    }

    private void RuntimeChanged()
    {
        _ = Dispatcher.InvokeAsync(async () => await RefreshAsync().ConfigureAwait(true));
    }

    private async Task RefreshAsync()
    {
        FingerprintText.Text = _runtime.CertificateSpkiSha256;
        ConnectionUrlText.Text = _runtime.WssUrl;
        StartupCheckBox.IsChecked = WindowsStartupManager.IsEnabled();
        FirewallCommandText.Text = FirewallDiagnostics.CreatePrivateProfileCommand(BridgeRuntime.DefaultPort);
        ServerStatusText.Text = _runtime.IsDesktopCodexAvailable ? "桌面同步可用" : "桌面同步未就绪";
        ServerStatusText.ToolTip = _runtime.IsDesktopCodexAvailable
            ? "已识别当前 Codex 桌面对话输入框"
            : _runtime.DesktopStatusReason;
        ServerStatusDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            _runtime.IsDesktopCodexAvailable ? "#35D07F" : "#F5A524"));
        DiagnosticsBox.Text = string.Join(Environment.NewLine, _runtime.Diagnostics);
        DiagnosticsBox.ScrollToEnd();
        RefreshPairingCountdown();

        try
        {
            var projects = await _runtime.GetAllowedProjectsAsync().ConfigureAwait(true);
            ProjectList.ItemsSource = projects;
            PairedDeviceList.ItemsSource = await _runtime.GetPairedDevicesAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            ProjectList.ItemsSource = Array.Empty<AllowedProject>();
            PairedDeviceList.ItemsSource = Array.Empty<PairedDevice>();
        }
    }

    private void RefreshPairingCountdown()
    {
        var window = _runtime.CurrentPairingWindow;
        if (window is null || window.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            PairingCodeText.Text = "— — — — — —";
            PairingExpiryText.Text = "配对窗口已关闭";
            PairingQrImage.Source = null;
            return;
        }

        PairingCodeText.Text = string.Join(' ', window.Code.ToCharArray());
        var seconds = Math.Max(0, (int)Math.Ceiling((window.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        PairingExpiryText.Text = $"剩余 {seconds} 秒";
        if (PairingQrImage.Source is null)
        {
            PairingQrImage.Source = CreateQrBitmap(_runtime.CreatePairingQrPayload(window));
        }
    }

    private static BitmapImage CreateQrBitmap(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        var bytes = qrCode.GetGraphic(8);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OpenPairing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runtime.OpenPairingWindow();
            RefreshPairingCountdown();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void CopyFingerprint_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(_runtime.CertificateSpkiSha256);
    }

    private void StartupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsStartupManager.SetEnabled(StartupCheckBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            StartupCheckBox.IsChecked = WindowsStartupManager.IsEnabled();
            ShowError(exception.Message);
        }
    }

    private void CopyFirewallCommand_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(FirewallCommandText.Text);
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择允许手机用于 Codex 任务的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            await _runtime.AddAllowedProjectAsync(dialog.SelectedPath).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not AllowedProject project)
        {
            return;
        }

        try
        {
            await _runtime.RemoveAllowedProjectAsync(project.ProjectId).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void RevokeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (PairedDeviceList.SelectedItem is not PairedDevice device)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"确定撤销 {device.DisplayName}（{device.DeviceId}）吗？撤销后手机必须重新配对才能连接。",
            "撤销已配对手机",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _runtime.RevokePairedDeviceAsync(device.DeviceId).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void TestApproval_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.PublishApprovalTestAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void ShowError(string message)
    {
        System.Windows.MessageBox.Show(this, message, "Codex Micro 桌面桥接", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting())
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
