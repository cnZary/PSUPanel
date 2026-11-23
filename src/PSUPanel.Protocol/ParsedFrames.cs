namespace PSUPanel.Protocol;

public abstract record ParsedFrame(byte[] Raw)
{
    public byte Command => Raw[3];
}

public sealed record TelemetryFrame(
    double Voltage3V3,
    double Voltage5V,
    double Voltage12V,
    double StandbyVoltage,
    byte FanMode,
    double InputPowerWatts,
    double InputFrequencyHz,
    double HighVoltageTemperatureC,
    double LowVoltageTemperatureC,
    double AmbientTemperatureC,
    double InputVoltage,
    int FanRpm,
    ushort FaultCode,
    ushort RawInputPower,
    byte[] Raw) : ParsedFrame(Raw);

public sealed record ModelInfoFrame(
    string ModelRaw,
    byte RatedCurrentLow,
    byte RatedCurrentPeak,
    byte InputVoltageMinimum,
    byte InputVoltageMaximum,
    ushort RatedPowerWatts,
    byte WarrantyYears,
    char EfficiencyCode,
    bool LifetimeFlag,
    string VendorText,
    byte[] Raw) : ParsedFrame(Raw);

public sealed record SerialNumberFrame(string SerialNumberRaw, byte[] Raw) : ParsedFrame(Raw);

public sealed record UnmappedFrame(byte CommandCode, byte[] Payload, byte[] Raw) : ParsedFrame(Raw)
{
    public ushort[] ReadRawUInt16LittleEndian()
    {
        if ((Payload.Length & 1) != 0)
        {
            throw new InvalidDataException("The payload length is not aligned to 16-bit values.");
        }

        var values = new ushort[Payload.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(Payload[index * 2] | (Payload[index * 2 + 1] << 8));
        }

        return values;
    }
}
