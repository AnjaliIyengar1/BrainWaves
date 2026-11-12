using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Protocol;

public readonly record struct FrameHeader(
    byte Version,
    ushort Channels,
    ushort SamplesPerChannel,
    ulong Sequence,
    long TimestampMicros);

/// Wire format v1, little-endian.
///  Offset  Size  Field
///  0       4     Magic "EEG1"
///  4       1     Version
///  5       1     Reserved (0)
///  6       2     Channels
///  8       2     SamplesPerChannel
///  10      2     Reserved (0)
///  12      8     Sequence
///  20      8     TimestampMicros (Unix epoch, first sample)
///  28      N     Payload: float32, sample-major (s0c0, s0c1, ..., s1c0, ...)
///  28+N    4     CRC-32 over bytes [0, 28+N)
public static class FrameProtocol
{
    /// Magic number for frame header, used to identify the start of a frame.
    /// public static ReadOnlySpan<byte> MagicBytes => "EEG1"u8;
    public const uint Magic = 0x31474545; // bytes on wire: 'E' 'E' 'G' '1'
    public const byte CurrentVersion = 1;
    public const int HeaderSize = 28;     // multiple of 4 keeps payload float-aligned
    public const int CrcSize = 4;

    /*
    1 second = 1,000 moments (1 ms each)
    1 packet = 10 moments
    1,000 / 10 = 100 packets per second
    */
    static FrameProtocol()
    {
        // Payload is cast directly to float; requires little-endian host (x64/ARM64).
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Big-endian hosts are not supported.");
    }

    public static int FrameSize(int channels, int samplesPerChannel) =>
        HeaderSize + channels * samplesPerChannel * sizeof(float) + CrcSize;

    // fills bytes 0 to 27.
    public static void WriteHeader(Span<byte> frame, in FrameHeader h)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
        frame[4] = h.Version;
        frame[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame[6..], h.Channels);   // frame[6..] means from byte 6 to the end,
        BinaryPrimitives.WriteUInt16LittleEndian(frame[8..], h.SamplesPerChannel);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[10..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(frame[12..], h.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(frame[20..], h.TimestampMicros);
    }

    /*
    Rate        = 1,000 samples/s/channel to be sent over the wire
    Per ms      = 64 ch × 1msec        = 64 values; Each channel has 1 sample per ms
    Per packet  = 64 × 10 ms       = 640 values; 64 channels × 10 samples per channel
    Payload     = 640 × 4 Bytes        = 2,560 Bytes; 4 Bytes per float32 value
    Packet      = 2,560 + 28 + 4   = 2,592 B;   header + data + CRC
    Packets/s   = 1,000 / 10       = 100; packets per second    
    Throughput  = 100 × 2,592      = 259,200 Bytes/s ≈ 259 KB/s; 
    */
    // For 'Simulator', which fills in data
    public static Span<float> Payload(Span<byte> frame) =>
        MemoryMarshal.Cast<byte, float>(frame[HeaderSize..^CrcSize]);

    // For 'DataProcessor', which only reads data
    public static ReadOnlySpan<float> Payload(ReadOnlySpan<byte> frame) =>
        MemoryMarshal.Cast<byte, float>(frame[HeaderSize..^CrcSize]);

    /// Writes the CRC trailer. Call last, after header and payload are filled.
    public static void Seal(Span<byte> frame) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame[^CrcSize..], Crc32.HashToUInt32(frame[..^CrcSize]));

    //checks that there are at least 28 bytes, that the packet starts with EEG1, 
    // that the version is 1, and that channels and samples are both above 0. 
    // If all of these pass, it reads the fields into a FrameHeader.
    //  If any fails, it returns false.
    public static bool TryReadHeader(ReadOnlySpan<byte> src, out FrameHeader header)
    {
        header = default;
        if (src.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(src) != Magic) return false;
        if (src[4] != CurrentVersion) return false;

        header = new FrameHeader(
            Version: src[4],
            Channels: BinaryPrimitives.ReadUInt16LittleEndian(src[6..]),
            SamplesPerChannel: BinaryPrimitives.ReadUInt16LittleEndian(src[8..]),
            Sequence: BinaryPrimitives.ReadUInt64LittleEndian(src[12..]),
            TimestampMicros: BinaryPrimitives.ReadInt64LittleEndian(src[20..]));

        return header.Channels > 0 && header.SamplesPerChannel > 0;
    }

    // CRC (Cyclic Redundancy Check): one specific kind of checksum. It’s better at catching common transmission errors, like several flipped bits in a row, than a simple add-up-the-bytes checksum.
    public static bool VerifyCrc(ReadOnlySpan<byte> frame) =>
        frame.Length > CrcSize &&
        Crc32.HashToUInt32(frame[..^CrcSize]) ==
        BinaryPrimitives.ReadUInt32LittleEndian(frame[^CrcSize..]);
}