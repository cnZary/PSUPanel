using System.Text;
using System.Text.Json;
using PSUPanel.Protocol;

var checks = 0;

var realTelemetry = Hex("55 7e 1e 02 eb 0c 8d 13 61 2f 92 13 00 0f 26 02 ac 23 f7 01 d6 3e 14 41 b0 27 78 08 17 00 00 00 ad ae");
var realModel = Hex("55 7e 1b 03 4b 45 2d 31 33 30 30 50 44 04 14 6e 64 f0 05 14 0a 50 73 65 67 6f 74 65 70 2b ae");
var realSerial = Hex("55 7e 12 05 53 42 52 43 38 42 34 30 39 30 30 30 34 38 39 20 16 ae");
var realCommand04 = Hex("55 7e 1e 04 34 00 4f 00 48 01 fc 00 7d 00 46 01 5a 10 e2 01 db 00 3c 08 c7 03 a3 03 ce 03 a6 03 01 ae");

Equal((byte)0xF4, Crc8.Compute("123456789"u8), "CRC-8/SMBUS standard vector");
Equal(Hex("55 7E 02 04 06 AE"), FrameCodec.CreateTelemetryQuery(), "Telemetry query checksum");
Equal(Hex("55 7E 04 1E 00 00 22 AE"), FrameCodec.CreateModelQuery(), "Model query checksum");
Equal(FrameCodec.CreateModelQuery(), FrameCodec.CreateFaultClearFrame(), "Shared model and fault-clear frame");

var telemetry = IsType<TelemetryFrame>(FrameCodec.Parse(realTelemetry), "Telemetry frame type");
Equal(3.307, telemetry.Voltage3V3, "3.3 V rail");
Equal(5.005, telemetry.Voltage5V, "5 V rail");
Equal(12.129, telemetry.Voltage12V, "12 V rail");
Equal(5.010, telemetry.StandbyVoltage, "Standby rail");
Equal((byte)0, telemetry.FanMode, "Raw fan mode");
Equal(456.6, telemetry.InputPowerWatts, "Input power");
Equal(50.3, telemetry.InputFrequencyHz, "Input frequency is not current");
Equal(62.8359375, telemetry.HighVoltageTemperatureC, "High-voltage temperature");
Equal(65.078125, telemetry.LowVoltageTemperatureC, "Low-voltage temperature");
Equal(39.6875, telemetry.AmbientTemperatureC, "Ambient temperature");
Equal(216.8, telemetry.InputVoltage, "Input voltage");
Equal(690, telemetry.FanRpm, "Fan speed");
Equal((ushort)0, telemetry.FaultCode, "Fault code");
Equal((ushort)0x23AC, telemetry.RawInputPower, "Raw input power");

var model = IsType<ModelInfoFrame>(FrameCodec.Parse(realModel), "Model frame type");
Equal("KE-1300PD", model.ModelRaw, "Nine-byte model");
Equal(1300, model.RatedPowerWatts, "Big-endian rated power");
Equal((byte)100, model.InputVoltageMinimum, "Minimum rated input voltage");
Equal((byte)240, model.InputVoltageMaximum, "Maximum rated input voltage");
Equal((byte)10, model.WarrantyYears, "Warranty years");
Equal('P', model.EfficiencyCode, "Efficiency code");
Equal(true, model.LifetimeFlag, "Lifetime flag");
Equal("segotep", model.VendorText, "Vendor text");

var serial = IsType<SerialNumberFrame>(FrameCodec.Parse(realSerial), "Serial frame type");
Equal("SBRC8B409000489 ", serial.SerialNumberRaw, "Raw serial number");
Equal("SBRC8B409000489", serial.SerialNumberRaw.Trim(), "Display serial number");

var command04 = IsType<UnmappedFrame>(FrameCodec.Parse(realCommand04), "CMD04 stays unmapped");
Equal((byte)0x04, command04.CommandCode, "CMD04 command code");
Equal(realCommand04[4..^2], command04.Payload, "CMD04 raw payload");
Equal((ushort)52, command04.ReadRawUInt16LittleEndian()[0], "CMD04 raw word zero");
Equal(14, command04.ReadRawUInt16LittleEndian().Length, "CMD04 raw word count");

var decoder = new StreamDecoder();
Equal(0, decoder.Feed([0x55]).Count, "Split header first byte");
Equal(1, decoder.Feed(realSerial[2..].Prepend((byte)0x7E).ToArray()).Count, "Split header second byte");

foreach (var sample in new[] { realTelemetry, realModel, realSerial, realCommand04 })
{
    for (var split = 0; split <= sample.Length; split++)
    {
        decoder = new StreamDecoder();
        var first = decoder.Feed(Encoding.ASCII.GetBytes("noise").Concat(sample[..split]).ToArray());
        Equal(split == sample.Length ? 1 : 0, first.Count, $"All split positions, first chunk {sample[3]}:{split}");
        var second = decoder.Feed(sample[split..]);
        Equal(split == sample.Length ? 0 : 1, second.Count, $"All split positions, second chunk {sample[3]}:{split}");
        if (second.Count == 1)
        {
            Equal(sample, second[0], $"Split frame payload {sample[3]}:{split}");
        }
    }

    decoder = new StreamDecoder();
    var decoded = new List<byte[]>();
    foreach (byte value in sample)
    {
        decoded.AddRange(decoder.Feed([value]));
    }

    Equal(1, decoded.Count, $"Byte-wise stream {sample[3]}");
    Equal(sample, decoded[0], $"Byte-wise payload {sample[3]}");
}

var corrupt = (byte[])realTelemetry.Clone();
corrupt[^2] ^= 0xFF;
var recovered = new StreamDecoder().Feed(corrupt.Concat(realSerial).ToArray());
Equal(1, recovered.Count, "Recovery after corrupt CRC");
Equal(realSerial, recovered[0], "Valid frame after corrupt CRC");
Throws<InvalidDataException>(() => FrameCodec.Parse(corrupt), "Corrupt CRC rejection");
var badTail = (byte[])realTelemetry.Clone();
badTail[^1] = 0;
Throws<InvalidDataException>(() => FrameCodec.Parse(badTail), "Bad tail rejection");

for (var mode = 0; mode <= 4; mode++)
{
    var expected = Hex($"55 7E 04 13 00 0{mode} {0x17 + mode:X2} AE");
    Equal(expected, FrameCodec.CreateFanModeFrame(mode), $"Fan mode vector {mode}");
}

Equal(Hex("55 7E 17 1B 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 9B AE"), FrameCodec.CreateFanCurveFrame(Enumerable.Repeat((byte)5, 21).ToArray()), "Fan curve vector");
foreach (var milliseconds in new[] { 1000, 1500, 2000, 2500, 3000 })
{
    Equal(8, FrameCodec.CreatePollingPeriodFrame(milliseconds).Length, $"Polling period vector {milliseconds}");
}

var syntheticPayload = new byte[28];
WriteUInt16(syntheticPayload, 4 - 4, 3300);
WriteUInt16(syntheticPayload, 6 - 4, 5010);
WriteUInt16(syntheticPayload, 8 - 4, 12010);
WriteUInt16(syntheticPayload, 10 - 4, 5000);
WriteUInt16(syntheticPayload, 16 - 4, 451 * 20);
WriteUInt16(syntheticPayload, 18 - 4, 214);
WriteUInt16(syntheticPayload, 20 - 4, 40 * 256 + 64);
WriteUInt16(syntheticPayload, 22 - 4, 36 * 256 + 128);
WriteUInt16(syntheticPayload, 24 - 4, 30 * 256);
WriteUInt16(syntheticPayload, 26 - 4, 1150);
WriteUInt16(syntheticPayload, 28 - 4, 21);
WriteUInt16(syntheticPayload, 30 - 4, 0x89);
syntheticPayload[12 - 4] = 1;
var synthetic = WithRxCrc(FrameCodec.CreateTxFrame(0x02, syntheticPayload));
var syntheticTelemetry = IsType<TelemetryFrame>(FrameCodec.Parse(synthetic), "Synthetic telemetry");
Equal(3.3, syntheticTelemetry.Voltage3V3, "Synthetic 3.3 V");
Equal(451.0, syntheticTelemetry.InputPowerWatts, "Synthetic input power");
Equal(21.4, syntheticTelemetry.InputFrequencyHz, "Synthetic frequency");
Equal(40.25, syntheticTelemetry.HighVoltageTemperatureC, "Synthetic high-voltage temperature");
Equal(36.5, syntheticTelemetry.LowVoltageTemperatureC, "Synthetic low-voltage temperature");
Equal((ushort)0x89, syntheticTelemetry.FaultCode, "Synthetic fault code");

var lowPowerPayload = new byte[28];
WriteUInt16(lowPowerPayload, 16 - 4, 199);
var lowPower = WithRxCrc(FrameCodec.CreateTxFrame(0x02, lowPowerPayload));
Equal(10.0, IsType<TelemetryFrame>(FrameCodec.Parse(lowPower), "Low power telemetry").InputPowerWatts, "Original low-power clamp");

var capturePath = args.Length > 0 ? Path.GetFullPath(args[0]) : FindCapturePath();
var commandCounts = new Dictionary<int, int>();
var captureFrames = 0;
foreach (var line in File.ReadLines(capturePath, Encoding.UTF8))
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    using var document = JsonDocument.Parse(line);
    var frame = Hex(document.RootElement.GetProperty("raw").GetString()!);
    True(FrameCodec.IsValid(frame), "Capture frame validation");
    var parsed = FrameCodec.Parse(frame);
    commandCounts[parsed.Command] = commandCounts.GetValueOrDefault(parsed.Command) + 1;
    if (parsed is TelemetryFrame capturedTelemetry)
    {
        True(capturedTelemetry.InputFrequencyHz is 50.2 or 50.3, "Captured frequency field");
    }

    captureFrames++;
}

Equal(63, captureFrames, "Real capture frame count");
Equal(30, commandCounts[0x02], "Real capture CMD02 count");
Equal(31, commandCounts[0x04], "Real capture CMD04 count");
Equal(1, commandCounts[0x03], "Real capture CMD03 count");
Equal(1, commandCounts[0x05], "Real capture CMD05 count");

Console.WriteLine($"PASS: {checks} protocol checks; {captureFrames} real frames from {capturePath}");
return 0;

void Equal<T>(T expected, T actual, string name)
{
    if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
    {
        if (!expectedBytes.AsSpan().SequenceEqual(actualBytes))
        {
            throw new InvalidOperationException($"{name}: expected {Convert.ToHexString(expectedBytes)}, got {Convert.ToHexString(actualBytes)}");
        }
    }
    else if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }

    checks++;
}

void True(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }

    checks++;
}

T IsType<T>(object value, string name) where T : class
{
    if (value is not T typed)
    {
        throw new InvalidOperationException($"{name}: actual type {value.GetType().Name}");
    }

    checks++;
    return typed;
}

void Throws<T>(Action action, string name) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        checks++;
        return;
    }

    throw new InvalidOperationException($"{name}: expected {typeof(T).Name}");
}

static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", string.Empty));

static byte[] WithRxCrc(byte[] frame)
{
    frame[^2] = Crc8.Compute(frame.AsSpan(..^2));
    return frame;
}

static void WriteUInt16(byte[] payload, int offset, int value)
{
    payload[offset] = (byte)value;
    payload[offset + 1] = (byte)(value >> 8);
}

static string FindCapturePath()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "himos", "analysis", "psu-capture.jsonl");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    throw new FileNotFoundException("Pass the path to himos/analysis/psu-capture.jsonl as the first argument.");
}
