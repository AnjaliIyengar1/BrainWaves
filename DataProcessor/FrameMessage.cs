using Protocol;

namespace DataProcessor;

/// What goes into the queue.
/// One validated packet, copied out of the reader's reused buffer.
public sealed record FrameMessage(FrameHeader Header, float[] Samples, long ReceivedAtMicros);