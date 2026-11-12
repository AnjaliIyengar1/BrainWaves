using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Protocol;

namespace DataProcessor;

/// Accepts device connections and pushes validated frames into the channel (producer).
public sealed class TcpIngestService(
    Channel<FrameMessage> channel,
    ILogger<TcpIngestService> logger) : BackgroundService
{
    private const int Port = 5555;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        logger.LogInformation("Listening on port {Port}", Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken); // one task per device; errors handled inside
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        logger.LogInformation("Device connected: {Remote}", remote);
        FrameReader? reader = null;

        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using NetworkStream stream = client.GetStream();
                reader = new FrameReader(stream);

                while (await reader.ReadAsync(ct) is { } result)
                {
                    // Copy required: reader reuses its buffer on the next read.
                    float[] samples = FrameProtocol.Payload(result.Frame.Span).ToArray();
                    long nowMicros = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / 10;
                    await channel.Writer.WriteAsync(new FrameMessage(result.Header, samples, nowMicros), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            logger.LogWarning("Connection error from {Remote}: {Message}", remote, ex.Message);
        }
        finally
        {
            logger.LogInformation(
                "Device disconnected: {Remote}. ok={Ok} crcErrors={Crc} lost={Lost} resyncBytes={Resync} sessions={Resets}",
                remote, reader?.FramesOk, reader?.CrcErrors, reader?.LostPackets, reader?.ResyncBytes, reader?.SessionResets);
        }
    }
}