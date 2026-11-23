namespace PSUPanel.Protocol;

public sealed class StreamDecoder
{
    private readonly List<byte> _buffer = [];
    private readonly bool _validate;

    public StreamDecoder(bool validate = true)
    {
        _validate = validate;
    }

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            _buffer.Add(value);
        }

        List<byte[]>? frames = null;
        while (true)
        {
            var start = FindHeader();
            if (start < 0)
            {
                var keepPartialHeader = _buffer.Count > 0 && _buffer[^1] == FrameCodec.HeaderFirst;
                _buffer.Clear();
                if (keepPartialHeader)
                {
                    _buffer.Add(FrameCodec.HeaderFirst);
                }

                break;
            }

            if (start > 0)
            {
                _buffer.RemoveRange(0, start);
            }

            if (_buffer.Count < 3)
            {
                break;
            }

            var size = _buffer[2] + 4;
            if (size < 6)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < size)
            {
                break;
            }

            var frame = _buffer.GetRange(0, size).ToArray();
            if (_validate && !FrameCodec.IsValid(frame))
            {
                _buffer.RemoveAt(0);
                continue;
            }

            _buffer.RemoveRange(0, size);
            frames ??= [];
            frames.Add(frame);
        }

        return frames ?? (IReadOnlyList<byte[]>)Array.Empty<byte[]>();
    }

    public void Reset() => _buffer.Clear();

    private int FindHeader()
    {
        for (var index = 0; index < _buffer.Count - 1; index++)
        {
            if (_buffer[index] == FrameCodec.HeaderFirst && _buffer[index + 1] == FrameCodec.HeaderSecond)
            {
                return index;
            }
        }

        return -1;
    }
}
