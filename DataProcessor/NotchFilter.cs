namespace DataProcessor;

/// This removes the 60 Hz hum and keeps everything else. Each channel remembers its own last values, which is how a filter works over time.
/// Not thread-safe: one instance per stream.
public sealed class NotchFilter
{
    private readonly int _channels;
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private readonly double[] _z1, _z2;

    public NotchFilter(int channels, double sampleRateHz, double notchHz, double q)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(q);
        if (notchHz <= 0 || notchHz >= sampleRateHz / 2)
            throw new ArgumentOutOfRangeException(nameof(notchHz), "Must be between 0 and Nyquist.");

        double w0 = Math.Tau * notchHz / sampleRateHz;
        double alpha = Math.Sin(w0) / (2.0 * q);
        double cos = Math.Cos(w0);
        double a0 = 1.0 + alpha;

        _b0 = 1.0 / a0;
        _b1 = -2.0 * cos / a0;
        _b2 = 1.0 / a0;
        _a1 = -2.0 * cos / a0;
        _a2 = (1.0 - alpha) / a0;

        _channels = channels;
        _z1 = new double[channels];
        _z2 = new double[channels];
    }

    /// Filters in place. Data is sample-major: data[s * channels + c].
    public void Process(Span<float> data, int samplesPerChannel)
    {
        for (int s = 0; s < samplesPerChannel; s++)
        {
            int row = s * _channels;
            for (int c = 0; c < _channels; c++)
            {
                // Transposed Direct Form II: 2 state values per channel.
                double x = data[row + c];
                double y = _b0 * x + _z1[c];
                _z1[c] = _b1 * x - _a1 * y + _z2[c];
                _z2[c] = _b2 * x - _a2 * y;
                data[row + c] = (float)y;
            }
        }
    }
}