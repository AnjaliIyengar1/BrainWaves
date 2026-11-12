using System.Threading.Channels;
using DataProcessor;

var builder = WebApplication.CreateBuilder(args);

var stats = new PipelineStats();

// The queue between ingest (producer) and processing (consumer).
var channel = Channel.CreateBounded<FrameMessage>(
    new BoundedChannelOptions(capacity: 1000) // ~10 s of packets at 100/s
    {
        FullMode = BoundedChannelFullMode.DropOldest, // favor fresh data over old
        SingleReader = true,
        SingleWriter = false                          // one writer task per device
    },
    itemDropped: _ => Interlocked.Increment(ref stats.DroppedFrames));

builder.Services.AddSignalR();
builder.Services.AddSingleton(stats);
builder.Services.AddSingleton(channel);
builder.Services.AddHostedService<TcpIngestService>();
builder.Services.AddHostedService<ProcessingService>();

var app = builder.Build();
app.MapGet("/", () => "DataProcessor running");
app.MapHub<BrainHub>("/hubs/brain");
app.Run();