using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PSUPanel.Shared;

namespace PSUPanel.Tray;

internal sealed class TrayApplication : IDisposable
{
    private const uint OpenPanelCommand = 1001;
    private const uint RescanCommand = 1002;
    private const uint ExitCommand = 1004;
    private const uint AutomaticPortCommand = 1100;
    private const uint FirstPortCommand = 1200;

    private readonly MonitorService _monitor;
    private readonly NativeMethods.WndProc _wndProc;
    private readonly IntPtr _icon = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IdiApplication);
    private readonly uint _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr _window;
    private bool _notifyAdded;
    private bool _disposed;
    private Process? _panelProcess;

    public TrayApplication(MonitorService monitor)
    {
        _monitor = monitor;
        _wndProc = WindowProcedure;
        var className = "PSUPanel.Tray.MessageWindow";
        var windowClass = new NativeMethods.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = NativeMethods.GetModuleHandle(null),
            Icon = _icon,
            ClassName = className,
            IconSmall = _icon
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册托盘窗口类。");
        }

        _window = NativeMethods.CreateWindowEx(
            0,
            className,
            "PSUPanel",
            NativeMethods.WsOverlapped,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            windowClass.Instance,
            IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘消息窗口。");
        }

        AddNotifyIcon();
        if (!_notifyAdded)
        {
            NativeMethods.DestroyWindow(_window);
            throw new InvalidOperationException("无法创建托盘图标。");
        }
    }

    public void Run()
    {
        while (true)
        {
            var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result == 0)
            {
                break;
            }

            if (result == -1)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "托盘消息循环失败。");
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveNotifyIcon();
        if (_window != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_window);
        }

        if (_panelProcess is not null)
        {
            try
            {
                if (!_panelProcess.HasExited)
                {
                    _panelProcess.CloseMainWindow();
                }
            }
            catch (InvalidOperationException)
            {
            }

            _panelProcess.Dispose();
        }
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            AddNotifyIcon();
            return IntPtr.Zero;
        }

        switch (message)
        {
            case NativeMethods.TrayCallbackMessage when (uint)(lParam.ToInt64() & 0xFFFFFFFF) == NativeMethods.WmRButtonUp:
                ShowContextMenu();
                return IntPtr.Zero;
            case NativeMethods.TrayCallbackMessage when (uint)(lParam.ToInt64() & 0xFFFFFFFF) == NativeMethods.WmLButtonDoubleClick:
                OpenPanel();
                return IntPtr.Zero;
            case NativeMethods.WmClose:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            case NativeMethods.WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void AddNotifyIcon()
    {
        var data = CreateNotifyData();
        data.Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip;
        data.CallbackMessage = NativeMethods.TrayCallbackMessage;
        data.Icon = _icon;
        data.Tip = "PSUPanel 电源监控";
        _notifyAdded = NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data);
    }

    private void RemoveNotifyIcon()
    {
        if (!_notifyAdded)
        {
            return;
        }

        var data = CreateNotifyData();
        NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
        _notifyAdded = false;
    }

    private void ShowContextMenu()
    {
        var snapshot = _monitor.GetSnapshot();
        UpdateToolTip(snapshot);
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        var ports = new Dictionary<uint, string>();
        try
        {
            AppendText(menu, "PSUPanel · 只读电源监控");
            AppendText(menu, $"状态：{StateText(snapshot)}");
            AppendText(menu, $"型号：{snapshot.Identity?.Model ?? "未读取"}");
            AppendText(menu, $"序列号：{snapshot.Identity?.SerialNumber ?? "未读取"}");
            if (snapshot.Telemetry is { } telemetry)
            {
                AppendText(menu, $"输入：{telemetry.InputPowerWatts:F1} W  |  {telemetry.InputFrequencyHz:F2} Hz");
                AppendText(menu, $"输入电压：{telemetry.InputVoltage:F1} V");
                AppendText(menu, $"温度 高/低/环境：{telemetry.HighVoltageTemperatureC:F1} / {telemetry.LowVoltageTemperatureC:F1} / {telemetry.AmbientTemperatureC:F1} ℃");
                AppendText(menu, $"风扇：{telemetry.FanRpm} RPM  |  故障码：0x{telemetry.FaultCode:X4}");
            }
            else
            {
                AppendText(menu, "实时遥测：—");
            }

            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, (UIntPtr)OpenPanelCommand, "打开完整面板");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, (UIntPtr)RescanCommand, "重新自动检测");

            var portMenu = NativeMethods.CreatePopupMenu();
            var automaticFlags = NativeMethods.MfString | (snapshot.PreferredPort is null ? NativeMethods.MfChecked : 0);
            NativeMethods.AppendMenu(portMenu, automaticFlags, (UIntPtr)AutomaticPortCommand, "自动检测");
            for (var index = 0; index < snapshot.AvailablePorts.Length && index < 100; index++)
            {
                var command = FirstPortCommand + (uint)index;
                var portName = snapshot.AvailablePorts[index];
                ports.Add(command, portName);
                var flags = NativeMethods.MfString
                    | (string.Equals(snapshot.PreferredPort, portName, StringComparison.OrdinalIgnoreCase) ? NativeMethods.MfChecked : 0);
                NativeMethods.AppendMenu(portMenu, flags, (UIntPtr)command, portName);
            }

            NativeMethods.AppendMenu(menu, NativeMethods.MfPopup, (UIntPtr)portMenu, "串口");
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, (UIntPtr)ExitCommand, "退出");

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_window);
            var selected = (uint)NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmReturnCommand | NativeMethods.TpmNoNotify | NativeMethods.TpmRightButton,
                point.X,
                point.Y,
                0,
                _window,
                IntPtr.Zero);
            NativeMethods.PostMessage(_window, 0, UIntPtr.Zero, IntPtr.Zero);

            if (selected == OpenPanelCommand)
            {
                OpenPanel();
            }
            else if (selected == RescanCommand || selected == AutomaticPortCommand)
            {
                SetPreferredPort(null);
            }
            else if (selected == ExitCommand)
            {
                NativeMethods.PostQuitMessage(0);
            }
            else if (ports.TryGetValue(selected, out var selectedPort))
            {
                SetPreferredPort(selectedPort);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private void SetPreferredPort(string? portName)
    {
        try
        {
            _monitor.SetPreferredPort(portName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            NativeMethods.MessageBox(_window, exception.Message, "PSUPanel 串口设置", NativeMethods.MbOk | NativeMethods.MbIconWarning);
        }
    }

    private void OpenPanel()
    {
        try
        {
            if (_panelProcess is not null)
            {
                if (!_panelProcess.HasExited)
                {
                    var window = _panelProcess.MainWindowHandle;
                    if (window != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
                        NativeMethods.SetForegroundWindow(window);
                    }

                    return;
                }

                _panelProcess.Dispose();
                _panelProcess = null;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "PSUPanel.Panel.exe");
            if (!File.Exists(path))
            {
                NativeMethods.MessageBox(
                    _window,
                    "未找到 PSUPanel.Panel.exe。请将面板与 PSUPanel.Tray.exe 放在同一目录。",
                    "PSUPanel",
                    NativeMethods.MbOk | NativeMethods.MbIconWarning);
                return;
            }

            _panelProcess = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            NativeMethods.MessageBox(_window, $"无法打开面板：{exception.Message}", "PSUPanel", NativeMethods.MbOk | NativeMethods.MbIconError);
        }
    }

    private void UpdateToolTip(MonitorSnapshot snapshot)
    {
        if (!_notifyAdded)
        {
            return;
        }

        var data = CreateNotifyData();
        data.Flags = NativeMethods.NifTip;
        data.Tip = snapshot.Telemetry is { } telemetry
            ? $"PSUPanel · {snapshot.Identity?.Model ?? snapshot.PortName ?? "电源"}\n{telemetry.InputPowerWatts:F0} W | {telemetry.InputFrequencyHz:F2} Hz | {telemetry.FanRpm} RPM"
            : $"PSUPanel\n{StateText(snapshot)}";
        NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
    }

    private NativeMethods.NotifyIconData CreateNotifyData()
    {
        return new NativeMethods.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            Window = _window,
            Id = 1,
            Tip = string.Empty,
            Info = string.Empty,
            InfoTitle = string.Empty
        };
    }

    private static void AppendText(IntPtr menu, string text)
    {
        NativeMethods.AppendMenu(menu, NativeMethods.MfString | NativeMethods.MfGrayed, UIntPtr.Zero, text);
    }

    private static string StateText(MonitorSnapshot snapshot)
    {
        return snapshot.State switch
        {
            "monitoring" => $"{snapshot.PortName} 在线",
            "scanning" => snapshot.Message,
            "no_data" => "遥测超时",
            "error" => snapshot.Message,
            "unavailable" => "未识别到电源",
            _ => snapshot.Message
        };
    }
}
