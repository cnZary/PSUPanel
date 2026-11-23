using System.Buffers.Binary;
using System.Text;

namespace PSUPanel.Protocol;

public static class FrameCodec
{
    public const byte HeaderFirst = 0x55;
    public const byte HeaderSecond = 0x7E;
    public const byte Tail = 0xAE;
    public const int BaudRate = 115200;
    public const int DataBits = 8;

    public static byte[] CreateTxFrame(byte command, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > 253)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload must contain at most 253 bytes.");
        }

        var length = payload.Length + 2;
        var frame = new byte[length + 4];
        frame[0] = HeaderFirst;
        frame[1] = HeaderSecond;
        frame[2] = (byte)length;
        frame[3] = command;
        payload.CopyTo(frame.AsSpan(4));

        var sum = 0;
        for (var index = 2; index < frame.Length - 2; index++)
        {
            sum += frame[index];
        }

        frame[^2] = (byte)sum;
        frame[^1] = Tail;
        return frame;
    }

    public static byte[] CreateTelemetryQuery() => CreateTxFrame(0x04);

    public static byte[] CreateModelQuery() => CreateTxFrame(0x1E, [0x00, 0x00]);

    public static byte[] CreateFanModeFrame(int mode)
    {
        if (mode is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Fan mode must be in the range 0..4.");
        }

        return CreateTxFrame(0x13, [0x00, (byte)mode]);
    }

    public static byte[] CreateFanCurveFrame(ReadOnlySpan<byte> dutyCyclePercent)
    {
        if (dutyCyclePercent.Length != 21)
        {
            throw new ArgumentException("A fan curve must contain exactly 21 values.", nameof(dutyCyclePercent));
        }

        foreach (byte value in dutyCyclePercent)
        {
            if (value > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(dutyCyclePercent), "Duty cycle values must be in the range 0..100.");
            }
        }

        return CreateTxFrame(0x1B, dutyCyclePercent);
    }

    public static byte[] CreatePollingPeriodFrame(int milliseconds)
    {
        int[] periods = [1000, 1500, 2000, 2500, 3000];
        var index = Array.IndexOf(periods, milliseconds);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "Unsupported polling period.");
        }

        return CreateTxFrame(0x16, [0x00, (byte)index]);
    }

    public static byte[] CreateFaultClearFrame() => CreateModelQuery();

    public static bool IsValid(ReadOnlySpan<byte> frame)
    {
        return frame.Length >= 6
            && frame.Length == frame[2] + 4
            && frame[0] == HeaderFirst
            && frame[1] == HeaderSecond
            && frame[^1] == Tail
            && Crc8.Compute(frame[..^2]) == frame[^2];
    }

    public static void Validate(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || frame.Length != frame[2] + 4)
        {
            throw new InvalidDataException("Invalid frame length.");
        }

        if (frame[0] != HeaderFirst || frame[1] != HeaderSecond || frame[^1] != Tail)
        {
            throw new InvalidDataException("Invalid frame header or tail.");
        }

        if (Crc8.Compute(frame[..^2]) != frame[^2])
        {
            throw new InvalidDataException("CRC-8/SMBUS mismatch.");
        }
    }

    public static ParsedFrame Parse(ReadOnlySpan<byte> frame, bool strict = true)
    {
        if (strict)
        {
            Validate(frame);
        }
        else if (frame.Length < 6 || frame.Length != frame[2] + 4)
        {
            throw new InvalidDataException("Invalid frame length.");
        }

        var raw = frame.ToArray();
        switch (frame[3])
        {
            case 0x02 when frame.Length == 34:
            {
                var rawPower = ReadUInt16LittleEndian(frame, 16);
                return new TelemetryFrame(
                    ReadUInt16LittleEndian(frame, 4) / 1000.0,
                    ReadUInt16LittleEndian(frame, 6) / 1000.0,
                    ReadUInt16LittleEndian(frame, 8) / 1000.0,
                    ReadUInt16LittleEndian(frame, 10) / 1000.0,
                    frame[12],
                    rawPower < 200 ? 10.0 : rawPower / 20.0,
                    ReadUInt16LittleEndian(frame, 18) / 10.0,
                    ReadUInt16LittleEndian(frame, 20) / 256.0,
                    ReadUInt16LittleEndian(frame, 22) / 256.0,
                    ReadUInt16LittleEndian(frame, 24) / 256.0,
                    ReadUInt16LittleEndian(frame, 26) / 10.0,
                    ReadUInt16LittleEndian(frame, 28) * 30,
                    ReadUInt16LittleEndian(frame, 30),
                    rawPower,
                    raw);
            }
            case 0x03 when frame.Length == 31:
                return new ModelInfoFrame(
                    Encoding.ASCII.GetString(frame.Slice(4, 9)),
                    frame[14],
                    frame[15],
                    frame[16],
                    frame[17],
                    BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(18, 2)),
                    frame[20],
                    (char)frame[21],
                    frame[13] < 10,
                    Encoding.ASCII.GetString(frame.Slice(22, 7)),
                    raw);
            case 0x05:
                return new SerialNumberFrame(Encoding.UTF8.GetString(frame[4..^2]), raw);
            default:
                return new UnmappedFrame(frame[3], frame[4..^2].ToArray(), raw);
        }
    }

    private static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> frame, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset, 2));
    }
}
