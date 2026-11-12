using Protocol;

namespace DataProcessor;
// the receiver. It reads packets, checks them, and catches lost ones.
/// Reads validated frames from a byte stream. One instance per connection; not thread-safe.
/// Returned frame memory is reused: valid only until the next ReadAsync call.
public sealed class FrameReader
{
    public const int MaxFrameBytes = 4 * 1024 * 1024; // rejects corrupt size fields

    private readonly Stream _stream;
    private byte[] _buffer = new byte[64 * 1024];
    private ulong? _expectedSeq;

    public long FramesOk { get; private set; }
    public long CrcErrors { get; private set; }
    public long ResyncBytes { get; private set; }
    public long LostPackets { get; private set; }
    public long SessionResets { get; private set; }

    public FrameReader(Stream stream) => _stream = stream;

    /// Returns null on end of stream.
    public async ValueTask<(FrameHeader Header, ReadOnlyMemory<byte> Frame)?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (!await FillAsync(0, FrameProtocol.HeaderSize, ct)) return null;

            // Resync: slide one byte at a time until a valid header appears.
            FrameHeader header;
            while (!FrameProtocol.TryReadHeader(_buffer.AsSpan(0, FrameProtocol.HeaderSize), out header))
            {
                Buffer.BlockCopy(_buffer, 1, _buffer, 0, FrameProtocol.HeaderSize - 1);
                ResyncBytes++;
                if (!await FillAsync(FrameProtocol.HeaderSize - 1, 1, ct)) return null;
            }

            int size = FrameProtocol.FrameSize(header.Channels, header.SamplesPerChannel);
            if (size > MaxFrameBytes)
            {
                ResyncBytes += FrameProtocol.HeaderSize;
                continue;
            }

            if (_buffer.Length < size) Array.Resize(ref _buffer, size); // keeps header bytes
            if (!await FillAsync(FrameProtocol.HeaderSize, size - FrameProtocol.HeaderSize, ct)) return null;

            ReadOnlyMemory<byte> frame = _buffer.AsMemory(0, size);
            if (!FrameProtocol.VerifyCrc(frame.Span))
            {
                CrcErrors++;
                continue;
            }

            TrackSequence(header.Sequence);
            FramesOk++;
            return (header, frame);
        }
    }

    private void TrackSequence(ulong seq)
    {
        if (_expectedSeq is ulong expected)
        {
            if (seq > expected) LostPackets += (long)(seq - expected);
            else if (seq < expected) SessionResets++;
        }
        _expectedSeq = seq + 1;
    }

    // Reads exactly count bytes into _buffer[offset..]; false on end of stream.
    private async ValueTask<bool> FillAsync(int offset, int count, CancellationToken ct)
    {
        try
        {
            await _stream.ReadExactlyAsync(_buffer.AsMemory(offset, count), ct);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}