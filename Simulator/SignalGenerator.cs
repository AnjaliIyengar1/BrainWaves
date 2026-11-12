namespace Simulator;

/// Generates synthetic EEG-like signals in microvolts.
/// Not thread-safe: use one instance per producer thread.
public sealed class SignalGenerator
{
    private const double AlphaHz = 10.0;
    private const double BetaHz = 20.0;
    private const double LineNoiseHz = 60.0;
    private const double LineNoiseUv = 5.0;
    private const double NoiseStdUv = 2.0;

    private readonly int _channels;
    private readonly double _sampleRateHz;
    private readonly double[] _alphaAmp;
    private readonly double[] _betaAmp;
    private readonly double[] _phase;
    private readonly Random _rng;
    private long _sampleIndex;

    public SignalGenerator(int channels, double sampleRateHz, int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        _channels = channels;
        _sampleRateHz = sampleRateHz;
        _rng = seed is int s ? new Random(s) : new Random();

        _alphaAmp = new double[channels];
        _betaAmp = new double[channels];
        _phase = new double[channels];

        // Per-channel variation so channels are distinguishable.
        // All 64 channels use the same recipe (alpha + beta + 60 Hz hum + noise), 
        // but each channel gets its own strength and timing, so no two look the same.
        for (int c = 0; c < channels; c++)
        {
            _alphaAmp[c] = 10.0 + 20.0 * _rng.NextDouble(); // 10-30 uV
            _betaAmp[c] = 2.0 + 6.0 * _rng.NextDouble();    // 2-8 uV
            _phase[c] = Math.Tau * _rng.NextDouble();
        }
    }

    /// Total samples generated per channel since start; continuous across calls.
    public long SampleIndex => _sampleIndex;

    /// Fills dest sample-major: dest[s * channels + c].
    /// Fill puts 640 brain numbers into one packet.
    /// Packet 0 → moments 0–9
    /// Packet 1 → moments 10–19
    /// Packet 2 → moments 20–29...
    public void Fill(Span<float> dest, int samplesPerChannel)
    {
        int required = samplesPerChannel * _channels;
        if (dest.Length < required)
            throw new ArgumentException($"Need {required} floats, got {dest.Length}.", nameof(dest));

        for (int s = 0; s < samplesPerChannel; s++)
        {
            double t = _sampleIndex / _sampleRateHz;
            double line = LineNoiseUv * Math.Sin(Math.Tau * LineNoiseHz * t); // common to all channels
            int row = s * _channels;

            for (int c = 0; c < _channels; c++)
            {
                double v = _alphaAmp[c] * Math.Sin(Math.Tau * AlphaHz * t + _phase[c])
                         + _betaAmp[c] * Math.Sin(Math.Tau * BetaHz * t + _phase[c])
                         + line
                         + NoiseStdUv * NextGaussian();
                dest[row + c] = (float)v;
            }

            _sampleIndex++;
        }
    }

    // Box-Muller transform; (1 - NextDouble) avoids Log(0).
    private double NextGaussian()
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(Math.Tau * u2);
    }
}