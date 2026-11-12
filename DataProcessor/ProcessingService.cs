using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace DataProcessor;

/// Message pushed to browsers. Samples are sample-major: Samples[s * Channels + c].
public sealed record SignalBatch(ulong Sequence, long TimestampMicros, int Channels, int SampleRateHz, float[] Samples);

/// Consumer: filters packets, decimates for display, pushes to browsers.
/// Filter state assumes a single device stream.
public sealed class ProcessingService(
    Channel<FrameMessage> channel,
    IHubContext<BrainHub> hub,
    PipelineStats stats,
    ILogger<ProcessingService> logger) : BackgroundService
{
    private const int SampleRateHz = 1000; // not in header yet; must match Simulator
    private const int Decimation = 5;      // 1000 Hz -> 200 Hz for display
    private const int PacketsPerPush = 4;  // 4 x 10 ms = 40 ms -> 25 pushes/s

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        NotchFilter? filter = null;
        int channels = 0, samplesPerPacket = 0;
        float[] pending = [];
        int pendingCount = 0, packetsInBatch = 0;
        ulong batchSeq = 0;
        long batchTs = 0;

        long packets = 0;
        long windowStartMs = Environment.TickCount64;

        await foreach (FrameMessage msg in channel.Reader.ReadAllAsync(ct))
        {
            var h = msg.Header;

            // (Re)initialize when stream shape changes.
            if (filter is null || h.Channels != channels || h.SamplesPerChannel != samplesPerPacket)
            {
                channels = h.Channels;
                samplesPerPacket = h.SamplesPerChannel;
                filter = new NotchFilter(channels, SampleRateHz, notchHz: 60.0, q: 30.0);
                int keptPerPacket = (samplesPerPacket + Decimation - 1) / Decimation;
                pending = new float[PacketsPerPush * keptPerPacket * channels];
                pendingCount = 0;
                packetsInBatch = 0;
                logger.LogInformation("Stream: {Channels} channels, {Spp} samples/packet", channels, samplesPerPacket);
            }

            filter.Process(msg.Samples, samplesPerPacket); // in place; we own this array

            if (packetsInBatch == 0)
            {
                batchSeq = h.Sequence;
                batchTs = h.TimestampMicros;
            }

            for (int s = 0; s < samplesPerPacket; s += Decimation)
            {
                msg.Samples.AsSpan(s * channels, channels).CopyTo(pending.AsSpan(pendingCount));
                pendingCount += channels;
            }

            if (++packetsInBatch == PacketsPerPush)
            {
                // pending[..n] copies, so pending can be reused immediately.
                var batch = new SignalBatch(batchSeq, batchTs, channels, SampleRateHz / Decimation, pending[..pendingCount]);
                await hub.Clients.All.SendAsync("signal", batch, ct);
                pendingCount = 0;
                packetsInBatch = 0;
            }

            packets++;
            long nowMs = Environment.TickCount64;
            if (nowMs - windowStartMs >= 1000)
            {
                logger.LogInformation("processed={Rate} packets/s  lastSeq={Seq}  dropped={Dropped}",
                    packets, h.Sequence, Interlocked.Read(ref stats.DroppedFrames));
                packets = 0;
                windowStartMs = nowMs;
            }
        }
    }
}