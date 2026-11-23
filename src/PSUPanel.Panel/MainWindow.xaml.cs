using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PSUPanel.Shared;

namespace PSUPanel.Panel;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Brush _onlineBrush = new SolidColorBrush(Color.FromRgb(0x35, 0xD0, 0xBA));
    private readonly Brush _warningBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x5D));
    private readonly Brush _offlineBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x7E, 0x8C));
    private readonly Brush _errorBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x7D, 0x7D));
    private MonitorSnapshot? _lastSnapshot;
    private bool _refreshInProgress;
    private bool _operationInProgress;
    private bool _suppressPortEvents;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (!_closing)
        {
            _timer.Start();
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _timer.Stop();
        _stopping.Cancel();
        _stopping.Dispose();
        base.OnClosed(e);
    }

    private async Task RefreshAsync()
    {
        if (_refreshInProgress || _closing)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            var snapshot = await SnapshotClient.GetSnapshotAsync(_stopping.Token);
            if (snapshot.SchemaVersion != 1)
            {
                throw new InvalidDataException("托盘与面板的快照版本不兼容。");
            }

            if (!_closing)
            {
                _lastSnapshot = snapshot;
                Render(snapshot);
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (!_closing && !_stopping.IsCancellationRequested)
            {
                _lastSnapshot = null;
                RenderUnavailable("无法连接托盘监控。请先启动 PSUPanel.Tray.exe。");
            }
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void Render(MonitorSnapshot snapshot)
    {
        StatusText.Text = snapshot.State switch
        {
            "monitoring" => $"{snapshot.PortName} · 在线监控",
            "scanning" => snapshot.Message,
            "no_data" => $"{snapshot.PortName} · 遥测超时",
            "unavailable" => "未识别到电源",
            "error" => "串口错误",
            "offline" => "串口离线，等待重试",
            _ => snapshot.Message
        };
        StatusDot.Background = snapshot.State switch
        {
            "monitoring" => _onlineBrush,
            "scanning" or "no_data" or "waiting" => _warningBrush,
            "error" => _errorBrush,
            _ => _offlineBrush
        };
        MessageText.Text = snapshot.Message;
        LastUpdateText.Text = $"快照 {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss} · 最近帧 {FormatTime(snapshot.LastFrameAt)}";
        UpdatePorts(snapshot);

        var identity = snapshot.Identity;
        ModelValue.Text = ValueOrDash(identity?.Model);
        SerialValue.Text = ValueOrDash(identity?.SerialNumber);
        RatedPowerValue.Text = identity?.RatedPowerWatts is { } ratedPower ? $"{ratedPower} W" : "—";
        InputRangeValue.Text = identity?.InputVoltageMinimum is { } minimum && identity.InputVoltageMaximum is { } maximum
            ? $"{minimum} – {maximum} V AC"
            : "—";
        EfficiencyValue.Text = FormatEfficiency(identity?.EfficiencyCode);
        VendorValue.Text = ValueOrDash(identity?.VendorText);
        RatedCurrentValue.Text = identity?.RatedCurrentLow is { } low && identity.RatedCurrentPeak is { } peak
            ? $"{low} A / {peak} A"
            : "—";
        WarrantyValue.Text = identity?.LifetimeFlag == true
            ? $"终身（原程序规则；年数字段 {identity.WarrantyYears}）"
            : identity?.WarrantyYears is { } years ? $"{years} 年" : "—";

        if (snapshot.Telemetry is { } telemetry)
        {
            InputPowerValue.Text = $"{telemetry.InputPowerWatts:F1} W";
            InputFrequencyValue.Text = $"{telemetry.InputFrequencyHz:F2} Hz";
            InputVoltageValue.Text = $"{telemetry.InputVoltage:F1} V";
            FanRpmValue.Text = $"{telemetry.FanRpm} RPM";
            Voltage3V3Value.Text = $"{telemetry.Voltage3V3:F3} V";
            Voltage5Value.Text = $"{telemetry.Voltage5V:F3} V";
            Voltage12Value.Text = $"{telemetry.Voltage12V:F3} V";
            StandbyVoltageValue.Text = $"{telemetry.StandbyVoltage:F3} V";
            HighTemperatureValue.Text = $"{telemetry.HighVoltageTemperatureC:F2} ℃";
            LowTemperatureValue.Text = $"{telemetry.LowVoltageTemperatureC:F2} ℃";
            AmbientTemperatureValue.Text = $"{telemetry.AmbientTemperatureC:F2} ℃";
            FanModeValue.Text = $"{telemetry.FanMode}（含义未确认）";
            FaultCodeValue.Text = $"0x{telemetry.FaultCode:X4}";
            RawTelemetryValue.Text = FormatHex(telemetry.RawFrameHex);
        }
        else
        {
            ClearTelemetry();
        }

        if (snapshot.Command04 is { } command04)
        {
            RawWords.ItemsSource = command04.RawUInt16LittleEndian.Select((value, index) => new RawWordItem(
                $"偏移 0x{4 + index * 2:X2}",
                value.ToString())).ToArray();
            RawPayloadValue.Text = FormatHex(command04.RawPayloadHex);
            RawCommand04Value.Text = FormatHex(command04.RawFrameHex);
        }
        else
        {
            RawWords.ItemsSource = null;
            RawPayloadValue.Text = "—";
            RawCommand04Value.Text = "—";
        }

        UpdateButtonState();
    }

    private void RenderUnavailable(string message)
    {
        StatusText.Text = "托盘服务不可用";
        StatusDot.Background = _errorBrush;
        MessageText.Text = message;
        LastUpdateText.Text = "—";
        _suppressPortEvents = true;
        PortCombo.ItemsSource = null;
        PortCombo.SelectedItem = null;
        _suppressPortEvents = false;
        ModelValue.Text = "—";
        SerialValue.Text = "—";
        RatedPowerValue.Text = "—";
        InputRangeValue.Text = "—";
        EfficiencyValue.Text = "—";
        VendorValue.Text = "—";
        RatedCurrentValue.Text = "—";
        WarrantyValue.Text = "—";
        RawWords.ItemsSource = null;
        RawPayloadValue.Text = "—";
        RawCommand04Value.Text = "—";
        ClearTelemetry();
        UpdateButtonState();
    }

    private void ClearTelemetry()
    {
        InputPowerValue.Text = "—";
        InputFrequencyValue.Text = "—";
        InputVoltageValue.Text = "—";
        FanRpmValue.Text = "—";
        Voltage3V3Value.Text = "—";
        Voltage5Value.Text = "—";
        Voltage12Value.Text = "—";
        StandbyVoltageValue.Text = "—";
        HighTemperatureValue.Text = "—";
        LowTemperatureValue.Text = "—";
        AmbientTemperatureValue.Text = "—";
        FanModeValue.Text = "—";
        FaultCodeValue.Text = "—";
        RawTelemetryValue.Text = "—";
    }

    private void UpdatePorts(MonitorSnapshot snapshot)
    {
        _suppressPortEvents = true;
        var ports = snapshot.AvailablePorts.ToList();
        if (snapshot.PreferredPort is { } preferred
            && !ports.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            ports.Insert(0, preferred);
        }

        PortCombo.ItemsSource = ports;
        PortCombo.SelectedItem = ports.FirstOrDefault(port => string.Equals(port, snapshot.PreferredPort, StringComparison.OrdinalIgnoreCase));
        PortCombo.ToolTip = snapshot.PreferredPort is null
            ? "自动检测：只接受通过长度、包尾和 CRC 校验的 CMD02 遥测帧"
            : $"固定使用 {snapshot.PreferredPort}";
        _suppressPortEvents = false;
    }

    private void UpdateButtonState()
    {
        var available = _lastSnapshot is not null && !_operationInProgress;
        PortCombo.IsEnabled = available;
        AutoButton.IsEnabled = available;
        RescanButton.IsEnabled = available;
        ReadIdentityButton.IsEnabled = available
            && _lastSnapshot!.State == "monitoring"
            && !_lastSnapshot.IdentityRequestPending;
        ReadIdentityButton.Content = _lastSnapshot?.IdentityRequestPending == true
            ? "读取命令已排队"
            : "手动读取型号 / 序列号";
    }

    private async void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressPortEvents && PortCombo.SelectedItem is string portName)
        {
            await ExecuteCommandAsync(SnapshotProtocol.PortPrefix + portName);
        }
    }

    private async void AutoButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(SnapshotProtocol.AutomaticPort);
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(SnapshotProtocol.Rescan);
    }

    private async void ReadIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        var first = MessageBox.Show(
            this,
            "读取型号与序列号需要发送 1E 00 00。此报文与故障清零按钮完全相同，软件无法判断设备当前上下文。日常监控不会发送它。\n\n是否继续？",
            "读取设备信息 · 第一次确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (first != MessageBoxResult.Yes)
        {
            return;
        }

        var second = MessageBox.Show(
            this,
            "请再次确认：该操作可能具有故障清零效果。仅在理解这一风险时继续；软件将只发送一次，失败也不会自动重试。\n\n确定现在发送吗？",
            "读取设备信息 · 第二次确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (second == MessageBoxResult.Yes)
        {
            await ExecuteCommandAsync(SnapshotProtocol.RequestIdentity);
        }
    }

    private async Task ExecuteCommandAsync(string command)
    {
        if (_operationInProgress || _closing)
        {
            return;
        }

        _operationInProgress = true;
        UpdateButtonState();
        try
        {
            var snapshot = await SnapshotClient.SendAsync(command, _stopping.Token);
            if (!_closing)
            {
                _lastSnapshot = snapshot;
                Render(snapshot);
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            if (!_closing && !_stopping.IsCancellationRequested)
            {
                MessageText.Text = $"操作未完成：{exception.Message}";
            }
        }
        finally
        {
            _operationInProgress = false;
            if (!_closing)
            {
                UpdateButtonState();
            }
        }
    }

    private static string FormatEfficiency(string? code)
    {
        return code switch
        {
            "P" => "P（白金）",
            "G" => "G（金牌）",
            "S" => "S（银牌）",
            "T" => "T（钛金）",
            null or "" => "—",
            _ => $"{code}（原程序按铜牌显示）"
        };
    }

    private static string FormatHex(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }

        var bytes = new string[value.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = value.Substring(index * 2, 2);
        }

        return string.Join(" ", bytes);
    }

    private static string FormatTime(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
    }

    private static string ValueOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private sealed record RawWordItem(string OffsetText, string ValueText);
}
