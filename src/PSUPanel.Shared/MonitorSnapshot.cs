namespace PSUPanel.Shared;

public sealed record MonitorSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string State { get; init; } = "starting";
    public string Message { get; init; } = "正在启动";
    public string? PortName { get; init; }
    public string? PreferredPort { get; init; }
    public string[] AvailablePorts { get; init; } = [];
    public bool IdentityRequestPending { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastFrameAt { get; init; }
    public DateTimeOffset? LastTelemetryAt { get; init; }
    public TelemetrySnapshot? Telemetry { get; init; }
    public DeviceIdentitySnapshot? Identity { get; init; }
    public RawCommand04Snapshot? Command04 { get; init; }
}

public sealed record TelemetrySnapshot
{
    public double Voltage3V3 { get; init; }
    public double Voltage5V { get; init; }
    public double Voltage12V { get; init; }
    public double StandbyVoltage { get; init; }
    public byte FanMode { get; init; }
    public double InputPowerWatts { get; init; }
    public double InputFrequencyHz { get; init; }
    public double HighVoltageTemperatureC { get; init; }
    public double LowVoltageTemperatureC { get; init; }
    public double AmbientTemperatureC { get; init; }
    public double InputVoltage { get; init; }
    public int FanRpm { get; init; }
    public ushort FaultCode { get; init; }
    public string RawFrameHex { get; init; } = string.Empty;
}

public sealed record DeviceIdentitySnapshot
{
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public int? RatedPowerWatts { get; init; }
    public int? InputVoltageMinimum { get; init; }
    public int? InputVoltageMaximum { get; init; }
    public int? RatedCurrentLow { get; init; }
    public int? RatedCurrentPeak { get; init; }
    public int? WarrantyYears { get; init; }
    public bool? LifetimeFlag { get; init; }
    public string? EfficiencyCode { get; init; }
    public string? VendorText { get; init; }
}

public sealed record RawCommand04Snapshot
{
    public ushort[] RawUInt16LittleEndian { get; init; } = [];
    public string RawPayloadHex { get; init; } = string.Empty;
    public string RawFrameHex { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
}

public sealed record AppSettings
{
    public string? PreferredPort { get; set; }
    public int PollIntervalMilliseconds { get; set; } = 1000;
}
