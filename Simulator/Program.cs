using System.Diagnostics;
using System.Net.Sockets;
using Protocol;
using Simulator;

const string Host = "127.0.0.1";
const int Port = 5555;
const ushort Channels = 64;
const int SampleRateHz = 1000;
const ushort SamplesPerPacket = 10;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var generator = new SignalGenerator(Channels, SampleRateHz, seed: 42);
byte[] frame = new byte[FrameProtocol.FrameSize(Channels, SamplesPerPacket)]; // reused for every packet
long sessionStartMicros = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / 10;
TimeSpan packetInterval = TimeSpan.FromSeconds((double)SamplesPerPacket / SampleRateHz);
ulong sequence = 0;

Console.WriteLine($"Frame {frame.Length} B, {1 / packetInterval.TotalSeconds:F0} packets/s -> {Host}:{Port}. Ctrl+C to stop.");

while (!cts.IsCancellationRequested)
{
    try
    {
        using var client = new TcpClient { NoDelay = true }; // send immediately, no batching
        await client.ConnectAsync(Host, Port, cts.Token);
        Console.WriteLine("Connected.");
        await using NetworkStream stream = client.GetStream();
        await StreamAsync(stream, cts.Token);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex) when (ex is SocketException or IOException)
    {
        Console.WriteLine($"Connection problem: {ex.Message}. Retrying in 1 s.");
        await Task.Delay(1000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}

Console.WriteLine($"Stopped. Packets sent: {sequence}");

async Task StreamAsync(NetworkStream stream, CancellationToken ct)
{
    var clock = Stopwatch.StartNew();
    long sent = 0;
    long lastReportMs = 0;
    ulong lastReportSeq = sequence;

    while (!ct.IsCancellationRequested)
    {
        // Schedule by total elapsed time, not per-loop sleeps, so timing never drifts.
        long due = (long)(clock.Elapsed / packetInterval);
        while (sent < due)
        {
            BuildFrame();
            await stream.WriteAsync(frame, ct);
            sent++;
        }

        long nowMs = clock.ElapsedMilliseconds;
        if (nowMs - lastReportMs >= 1000)
        {
            Console.WriteLine($"seq={sequence}  rate={sequence - lastReportSeq} packets/s");
            lastReportMs = nowMs;
            lastReportSeq = sequence;
        }

        await Task.Delay(1, ct);
    }
}

void BuildFrame()
{
    // Timestamp from the sample clock (like a real device), not the wall clock.
    long timestampMicros = sessionStartMicros + generator.SampleIndex * 1_000_000L / SampleRateHz;

    FrameProtocol.WriteHeader(frame, new FrameHeader(
        FrameProtocol.CurrentVersion, Channels, SamplesPerPacket, sequence, timestampMicros));
    generator.Fill(FrameProtocol.Payload(frame.AsSpan()), SamplesPerPacket);
    FrameProtocol.Seal(frame);
    sequence++;
}