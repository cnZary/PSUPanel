using System.IO.Ports;
using PSUPanel.Protocol;
using PSUPanel.Shared;

namespace PSUPanel.Tray;

internal sealed class MonitorService : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SettingsStore _settingsStore = new();
    private AppSettings _settings;
    private MonitorSnapshot _snapshot;
    private SerialPort? _activePort;
    private Task? _worker;
    private int _generation;
    private int _identityRequested;
    private bool _disposed;

    public MonitorService(string? preferredPort = null)
    {
        _settings = _settingsStore.Load();
        if (preferredPort is not null)
        {
            if (string.IsNullOrWhiteSpace(preferredPort) || preferredPort.Length > 32 || preferredPort.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new ArgumentException("串口名称无效。", nameof(preferredPort));
            }

            _settings.PreferredPort = preferredPort.Trim();
        }

        _snapshot = new MonitorSnapshot
        {
            PreferredPort = _settings.PreferredPort,
            Message = "正在启动串口监控"
        };
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(Run);
        }
    }

    public MonitorSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public void SetPreferredPort(string? portName)
    {
        if (portName is not null)
        {
            portName = portName.Trim();
            if (portName.Length == 0 || portName.Length > 32 || portName.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new ArgumentException("串口名称无效。", nameof(portName));
            }
        }

        var settings = new AppSettings
        {
            PreferredPort = portName,
            PollIntervalMilliseconds = _settings.PollIntervalMilliseconds
        };
        _settingsStore.Save(settings);

        SerialPort? activePort;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _settings = settings;
            _identityRequested = 0;
            _generation++;
            activePort = _activePort;
            _snapshot = _snapshot with
            {
                State = "waiting",
                Message = portName is null ? "正在重新自动检测串口" : $"正在切换到 {portName}",
                PreferredPort = portName,
                PortName = portName,
                Telemetry = null,
                Identity = null,
                Command04 = null,
                IdentityRequestPending = false,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        ClosePort(activePort);
    }

    public bool RequestIdentity()
    {
        lock (_sync)
        {
            if (_activePort is null || _snapshot.State != "monitoring")
            {
                Update(current => current with { Message = "仅在线监控时可手动读取型号与序列号" });
                return false;
            }

            _identityRequested = 1;
            Update(current => current with
            {
                IdentityRequestPending = true,
                Message = "读取命令已排队，仅发送一次，不会自动重试"
            });
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _identityRequested = 0;
        }

        _stopping.Cancel();
        SerialPort? activePort;
        lock (_sync)
        {
            activePort = _activePort;
        }

        ClosePort(activePort);
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }

    private void Run()
    {
        var cancellationToken = _stopping.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            var version = Volatile.Read(ref _generation);
            OpenConnection? connection = null;
            try
            {
                Update(current => current with
                {
                    State = "waiting",
                    Message = "正在枚举串口",
                    Telemetry = null,
                    Identity = null,
                    Command04 = null,
                    LastTelemetryAt = null,
                    LastFrameAt = null,
                    IdentityRequestPending = false
                });

                connection = TryOpen(cancellationToken, version);
                if (connection is null)
                {
                    Wait(cancellationToken, 1500);
                    continue;
                }

                MonitorConnected(connection, cancellationToken, version);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or TimeoutException or ArgumentException)
            {
                if (version == Volatile.Read(ref _generation) && !cancellationToken.IsCancellationRequested)
                {
                    Update(current => current with
                    {
                        State = "error",
                        Message = DescribeError(current.PortName, exception),
                        Telemetry = null
                    });
                }
            }
            finally
            {
                _identityRequested = 0;
                lock (_sync)
                {
                    if (ReferenceEquals(_activePort, connection?.Port))
                    {
                        _activePort = null;
                    }
                }

                connection?.Port.Dispose();
                Update(current => current with { IdentityRequestPending = false });
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                Update(current => current with
                {
                    State = version == Volatile.Read(ref _generation) ? "offline" : current.State,
                    Message = version == Volatile.Read(ref _generation) ? "串口已断开，等待重试" : current.Message,
                    Telemetry = null,
                    PortName = current.PreferredPort
                });
                Wait(cancellationToken, 1500);
            }
        }
    }

    private OpenConnection? TryOpen(CancellationToken cancellationToken, int version)
    {
        string[] ports;
        try
        {
            ports = SerialPort.GetPortNames();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Update(current => current with { State = "error", Message = "无法枚举串口", AvailablePorts = [] });
            return null;
        }

        string? preferredPort;
        lock (_sync)
        {
            preferredPort = _settings.PreferredPort;
            _snapshot = _snapshot with
            {
                AvailablePorts = ports,
                PreferredPort = preferredPort,
                PortName = preferredPort,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        var candidates = preferredPort is null ? ports : [preferredPort];
        if (candidates.Length == 0)
        {
            Update(current => current with
            {
                State = "unavailable",
                Message = "未发现串口；连接设备后会自动重试"
            });
            return null;
        }

        string? lastError = null;
        foreach (var portName in candidates)
        {
            if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _generation))
            {
                return null;
            }

            Update(current => current with
            {
                State = "scanning",
                PortName = portName,
                Message = preferredPort is null ? $"正在检测 {portName}" : $"正在打开 {portName}"
            });

            var port = new SerialPort(portName, FrameCodec.BaudRate, Parity.None, FrameCodec.DataBits, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                ReadTimeout = 200,
                WriteTimeout = 2000
            };

            lock (_sync)
            {
                _activePort = port;
            }

            try
            {
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
                WriteFrame(port, FrameCodec.CreateTelemetryQuery());

                var decoder = new StreamDecoder();
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1500);
                var buffer = new byte[512];
                while (DateTimeOffset.UtcNow < deadline
                    && !cancellationToken.IsCancellationRequested
                    && version == Volatile.Read(ref _generation))
                {
                    foreach (var frame in ReadFrames(port, decoder, buffer))
                    {
                        ProcessFrame(frame);
                    }

                    if (GetSnapshot().Telemetry is not null)
                    {
                        return new OpenConnection(port, decoder);
                    }
                }
            }
            catch (TimeoutException)
            {
                lastError = $"{portName} 未返回有效 CMD02 遥测帧";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
            {
                if (version != Volatile.Read(ref _generation) || cancellationToken.IsCancellationRequested)
                {
                    ClosePort(port);
                    port.Dispose();
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activePort, port))
                        {
                            _activePort = null;
                        }
                    }

                    return null;
                }

                lastError = DescribeError(portName, exception);
            }

            ClosePort(port);
            port.Dispose();
            lock (_sync)
            {
                if (ReferenceEquals(_activePort, port))
                {
                    _activePort = null;
                }
            }
        }

        Update(current => current with
        {
            State = "unavailable",
            Message = lastError ?? "未识别到电源；请确认 HiMOS 已退出",
            PortName = preferredPort,
            Telemetry = null
        });
        return null;
    }

    private void MonitorConnected(OpenConnection connection, CancellationToken cancellationToken, int version)
    {
        var port = connection.Port;
        var decoder = connection.Decoder;
        var buffer = new byte[512];
        var nextQuery = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(GetPollInterval());
        var nextPortEnumeration = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        var staleAfter = TimeSpan.FromMilliseconds(Math.Max(3000, GetPollInterval() * 3));

        while (!cancellationToken.IsCancellationRequested
            && version == Volatile.Read(ref _generation)
            && port.IsOpen)
        {
            if (Interlocked.Exchange(ref _identityRequested, 0) != 0)
            {
                Update(current => current with { IdentityRequestPending = false });
                WriteFrame(port, FrameCodec.CreateModelQuery());
                Update(current => current with { Message = "已手动发送一次型号读取命令，不会自动重发" });
                nextQuery = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(GetPollInterval());
            }
            else if (DateTimeOffset.UtcNow >= nextQuery)
            {
                WriteFrame(port, FrameCodec.CreateTelemetryQuery());
                nextQuery = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(GetPollInterval());
            }

            foreach (var frame in ReadFrames(port, decoder, buffer))
            {
                ProcessFrame(frame);
            }

            if (DateTimeOffset.UtcNow >= nextPortEnumeration)
            {
                try
                {
                    var ports = SerialPort.GetPortNames();
                    Update(current => current with { AvailablePorts = ports });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }

                nextPortEnumeration = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            }

            var snapshot = GetSnapshot();
            if (snapshot.LastTelemetryAt is { } lastTelemetry
                && DateTimeOffset.UtcNow - lastTelemetry > staleAfter
                && snapshot.Telemetry is not null)
            {
                Update(current => current with
                {
                    State = "no_data",
                    Message = "串口已打开，但遥测已超时；实时值已清除",
                    Telemetry = null
                });
            }
        }
    }

    private static IReadOnlyList<byte[]> ReadFrames(SerialPort port, StreamDecoder decoder, byte[] buffer)
    {
        try
        {
            var count = port.Read(buffer, 0, buffer.Length);
            return count == 0 ? Array.Empty<byte[]>() : decoder.Feed(buffer.AsSpan(0, count));
        }
        catch (TimeoutException)
        {
            return Array.Empty<byte[]>();
        }
    }

    private static void WriteFrame(SerialPort port, byte[] frame)
    {
        port.Write(frame, 0, frame.Length);
        port.BaseStream.Flush();
    }

    private void ProcessFrame(byte[] frame)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        switch (FrameCodec.Parse(frame))
        {
            case TelemetryFrame telemetry:
                Update(current => current with
                {
                    State = "monitoring",
                    Message = "监控正常",
                    PortName = current.PortName,
                    LastFrameAt = receivedAt,
                    LastTelemetryAt = receivedAt,
                    Telemetry = new TelemetrySnapshot
                    {
                        Voltage3V3 = telemetry.Voltage3V3,
                        Voltage5V = telemetry.Voltage5V,
                        Voltage12V = telemetry.Voltage12V,
                        StandbyVoltage = telemetry.StandbyVoltage,
                        FanMode = telemetry.FanMode,
                        InputPowerWatts = telemetry.InputPowerWatts,
                        InputFrequencyHz = telemetry.InputFrequencyHz,
                        HighVoltageTemperatureC = telemetry.HighVoltageTemperatureC,
                        LowVoltageTemperatureC = telemetry.LowVoltageTemperatureC,
                        AmbientTemperatureC = telemetry.AmbientTemperatureC,
                        InputVoltage = telemetry.InputVoltage,
                        FanRpm = telemetry.FanRpm,
                        FaultCode = telemetry.FaultCode,
                        RawFrameHex = Convert.ToHexString(telemetry.Raw)
                    }
                });
                break;
            case ModelInfoFrame model:
                Update(current =>
                {
                    var identity = current.Identity ?? new DeviceIdentitySnapshot();
                    return current with
                    {
                        LastFrameAt = receivedAt,
                        Identity = identity with
                        {
                            Model = model.ModelRaw.Trim(),
                            RatedPowerWatts = model.RatedPowerWatts,
                            InputVoltageMinimum = model.InputVoltageMinimum,
                            InputVoltageMaximum = model.InputVoltageMaximum,
                            RatedCurrentLow = model.RatedCurrentLow,
                            RatedCurrentPeak = model.RatedCurrentPeak,
                            WarrantyYears = model.WarrantyYears,
                            LifetimeFlag = model.LifetimeFlag,
                            EfficiencyCode = model.EfficiencyCode.ToString(),
                            VendorText = model.VendorText.Trim()
                        }
                    };
                });
                break;
            case SerialNumberFrame serial:
                Update(current =>
                {
                    var identity = current.Identity ?? new DeviceIdentitySnapshot();
                    return current with
                    {
                        LastFrameAt = receivedAt,
                        Identity = identity with { SerialNumber = serial.SerialNumberRaw.Trim() }
                    };
                });
                break;
            case UnmappedFrame unmapped when unmapped.CommandCode == 0x04 && unmapped.Payload.Length == 28:
                Update(current => current with
                {
                    LastFrameAt = receivedAt,
                    Command04 = new RawCommand04Snapshot
                    {
                        RawUInt16LittleEndian = unmapped.ReadRawUInt16LittleEndian(),
                        RawPayloadHex = Convert.ToHexString(unmapped.Payload),
                        RawFrameHex = Convert.ToHexString(unmapped.Raw),
                        ReceivedAt = receivedAt
                    }
                });
                break;
            default:
                Update(current => current with { LastFrameAt = receivedAt });
                break;
        }
    }

    private int GetPollInterval()
    {
        lock (_sync)
        {
            return _settings.PollIntervalMilliseconds;
        }
    }

    private void Update(Func<MonitorSnapshot, MonitorSnapshot> update)
    {
        lock (_sync)
        {
            _snapshot = update(_snapshot) with { UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    private static void Wait(CancellationToken cancellationToken, int milliseconds)
    {
        cancellationToken.WaitHandle.WaitOne(milliseconds);
    }

    private static void ClosePort(SerialPort? port)
    {
        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static string DescribeError(string? portName, Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => $"{portName ?? "串口"} 被占用；请完全退出 HiMOS 后重试",
            IOException => $"{portName ?? "串口"} 通信失败：{exception.Message}",
            _ => $"{portName ?? "串口"} 不可用：{exception.Message}"
        };
    }

    private sealed record OpenConnection(SerialPort Port, StreamDecoder Decoder);
}
