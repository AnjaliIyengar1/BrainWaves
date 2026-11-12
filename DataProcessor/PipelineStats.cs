namespace DataProcessor;

public sealed class PipelineStats
{
    public long DroppedFrames; // field (not property) so Interlocked can update it
}