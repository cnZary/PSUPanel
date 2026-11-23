namespace PSUPanel.Protocol;

public static class Crc8
{
    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0
                    ? (byte)((crc << 1) ^ 0x07)
                    : (byte)(crc << 1);
            }
        }

        return crc;
    }
}
