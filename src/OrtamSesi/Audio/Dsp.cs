namespace OrtamSesi.Audio;

/// <summary>xorshift64* — fast pseudo-random generator whose repetition (2^64) is never seen in practice.</summary>
internal sealed class Rng
{
    private ulong _s;
    public Rng(ulong seed) { _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; }

    /// <summary>Uniformly distributed number in [-1, 1).</summary>
    public double NextSigned()
    {
        _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27;
        ulong r = _s * 2685821657736338717UL;
        return (r >> 11) * (2.0 / (1UL << 53)) - 1.0;
    }

    public double Next01() => (NextSigned() + 1.0) * 0.5;
}

/// <summary>RBJ "Audio EQ Cookbook" biquad, transposed direct form II, double precision.</summary>
internal sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;

    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset() { _z1 = _z2 = 0; }

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    private static (double cos, double alpha) Pre(double fs, double f, double q)
    {
        f = Math.Clamp(f, 5, fs * 0.45);
        double w0 = 2 * Math.PI * f / fs;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }

    public void SetLowpass(double fs, double f, double q)
    {
        var (c, a) = Pre(fs, f, q);
        Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a);
    }

    public void SetHighpass(double fs, double f, double q)
    {
        var (c, a) = Pre(fs, f, q);
        Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a);
    }

    /// <summary>Bandpass (peak gain 0 dB).</summary>
    public void SetBandpass(double fs, double f, double q)
    {
        var (c, a) = Pre(fs, f, q);
        Set(a, 0, -a, 1 + a, -2 * c, 1 - a);
    }

    public void SetPeaking(double fs, double f, double q, double gainDb)
    {
        var (c, a) = Pre(fs, f, q);
        double A = Math.Pow(10, gainDb / 40);
        Set(1 + a * A, -2 * c, 1 - a * A, 1 + a / A, -2 * c, 1 - a / A);
    }

    public void SetHighShelf(double fs, double f, double q, double gainDb)
    {
        var (c, a) = Pre(fs, f, q);
        double A = Math.Pow(10, gainDb / 40), s = 2 * Math.Sqrt(A) * a;
        Set(A * ((A + 1) + (A - 1) * c + s),
            -2 * A * ((A - 1) + (A + 1) * c),
            A * ((A + 1) + (A - 1) * c - s),
            (A + 1) - (A - 1) * c + s,
            2 * ((A - 1) - (A + 1) * c),
            (A + 1) - (A - 1) * c - s);
    }
}

/// <summary>
/// Very slow, smooth random drift (-1..1). Glides to a new target along a cosine curve every 12–32 seconds.
/// Because it is random rather than a periodic LFO, it gives no sense of a "loop".
/// </summary>
internal sealed class SlowDrift
{
    private readonly Rng _rng;
    private double _from, _to, _t, _dur;

    private readonly double _minDur, _maxDur;

    public SlowDrift(Rng rng, double minSeconds = 12, double maxSeconds = 32)
    {
        _rng = rng;
        _minDur = minSeconds;
        _maxDur = maxSeconds;
        _to = rng.NextSigned() * 0.5;
        _dur = 1;
    }

    public double Next(double dt)
    {
        _t += dt;
        if (_t >= _dur)
        {
            _from = _to;
            _to = _rng.NextSigned();
            _t = 0;
            _dur = _minDur + _rng.Next01() * (_maxDur - _minDur);
        }
        double s = 0.5 - 0.5 * Math.Cos(Math.PI * (_t / _dur));
        return _from + (_to - _from) * s;
    }
}

/// <summary>Paul Kellet's pink noise filter (white → pink, ±0.05 dB, above 9 Hz).</summary>
internal sealed class PinkFilter
{
    private double _p0, _p1, _p2, _p3, _p4, _p5, _p6;

    public double Next(double white)
    {
        _p0 = 0.99886 * _p0 + white * 0.0555179;
        _p1 = 0.99332 * _p1 + white * 0.0750759;
        _p2 = 0.96900 * _p2 + white * 0.1538520;
        _p3 = 0.86650 * _p3 + white * 0.3104856;
        _p4 = 0.55000 * _p4 + white * 0.5329522;
        _p5 = -0.7616 * _p5 - white * 0.0168980;
        double y = (_p0 + _p1 + _p2 + _p3 + _p4 + _p5 + _p6 + white * 0.5362) * 0.11;
        _p6 = white * 0.115926;
        return y;
    }
}

internal static class Spectrum
{
    /// <summary>IEC 61672 A-weighting (linear amplitude factor).</summary>
    public static double AWeight(double f)
    {
        double f2 = f * f;
        const double c1 = 20.6 * 20.6, c2 = 107.7 * 107.7, c3 = 737.9 * 737.9, c4 = 12194.0 * 12194.0;
        double ra = c4 * f2 * f2 / ((f2 + c1) * Math.Sqrt((f2 + c2) * (f2 + c3)) * (f2 + c4));
        return ra * 1.2589254; // +2.0 dB
    }

    /// <summary>In-place radix-2 complex FFT.</summary>
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    /// <summary>One-sided power spectrum via Welch's method (relative; its sum is proportional to the mean square).</summary>
    public static double[] PowerSpectrum(ReadOnlySpan<float> x, int n = 4096)
    {
        var psd = new double[n / 2 + 1];
        var re = new double[n];
        var im = new double[n];
        var win = new double[n];
        for (int i = 0; i < n; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        int frames = 0;
        for (int start = 0; start + n <= x.Length; start += n / 2)
        {
            for (int i = 0; i < n; i++) { re[i] = x[start + i] * win[i]; im[i] = 0; }
            Fft(re, im);
            for (int k = 0; k <= n / 2; k++) psd[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }
        if (frames > 0) for (int k = 0; k < psd.Length; k++) psd[k] /= frames;
        return psd;
    }

    /// <summary>Ratio of A-weighted power to unweighted power.</summary>
    public static double AWeightedRatio(ReadOnlySpan<float> x, int fs)
    {
        const int n = 4096;
        var psd = PowerSpectrum(x, n);
        double total = 0, weighted = 0;
        for (int k = 1; k < psd.Length; k++)
        {
            double f = (double)k * fs / n;
            double w = AWeight(f);
            total += psd[k];
            weighted += psd[k] * w * w;
        }
        return total > 0 ? weighted / total : 0;
    }

    public static double Db(double linPower) => linPower > 0 ? 10 * Math.Log10(linPower) : -200;
}
