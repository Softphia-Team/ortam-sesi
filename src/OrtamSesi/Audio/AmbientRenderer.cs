namespace OrtamSesi.Audio;

/// <summary>Desired state for one block (the audio thread updates it every block).</summary>
internal struct RenderInputs
{
    /// <summary>Should the ambient sound be audible (playing, and no other app is playing audio)?</summary>
    public bool WantAmbient;
    public string SoundId;
    /// <summary>Frequency (Hz) when the tone generator is selected.</summary>
    public double ToneHz;
    public double FadeInSeconds;
    public double FadeOutSeconds;
    /// <summary>Inaudibly quiet floor sound that keeps the speaker from going to sleep while silent.</summary>
    public bool KeepAlive;
}

/// <summary>
/// Sound source + gain: smooth fade in/out, 2 s crossfade when the sound changes, keep-awake floor and
/// a soft limiter as the last stage. Output ≈ -20 dBFS (A-weighted); the actual attenuation (at most 1%) is set
/// in AudioEngine via the session volume, taking the Windows volume into account.
/// </summary>
internal sealed class AmbientRenderer : IDisposable
{
    /// Fade: as the position goes 0→1, gain goes -50 dB → 0 dB (linear in dB: rises/falls at an even rate to the ear).
    /// Starts below the keep-awake floor (-45): no jump between the floor and the fade-in.
    public const double FadeRangeDb = 50;
    /// Keep-awake floor: 45 dB below the ambient sound — inaudible, but the stream is never "digital silence".
    public const double KeepAliveDb = -45;
    public const double CrossfadeSeconds = 2.0;
    public const double LimiterKnee = 0.5;      // -6 dBFS
    public const double LimiterCeiling = 0.89;  // -1 dBFS
    private const int Block = 256;

    public int SampleRate { get; }
    public long LimiterHits { get; private set; }
    public long Errors { get; private set; }
    public double FadePosition => _fade;
    public double CurrentGain => _prevGain;
    public bool LastRenderSilent { get; private set; }
    public string SoundId => _soundId;

    private readonly SoundLibrary _library;
    private ISoundSource _src;
    private ISoundSource? _old;
    private string _soundId;
    private double _xfade = 1, _fade, _prevGain;
    private readonly float[] _l = new float[Block], _r = new float[Block], _ol = new float[Block], _or = new float[Block];

    public AmbientRenderer(int sampleRate, SoundLibrary library, string soundId)
    {
        SampleRate = sampleRate;
        _library = library;
        _soundId = library.Get(soundId).Id;
        _src = OpenSafe(_soundId);
    }

    private ISoundSource OpenSafe(string id)
    {
        try { return _library.Open(_library.Get(id), SampleRate); }
        catch (Exception ex)
        {
            Errors++;
            Core.Log.Info($"Could not open sound ({id}): {ex.Message} — playing brown noise instead");
            return new NoiseSource("brown", SampleRate);
        }
    }

    public void ResetFade() { _fade = 0; _prevGain = 0; }

    public static double DbToLin(double db) => Math.Pow(10, db / 20);

    public void Render(Span<float> left, Span<float> right, in RenderInputs inp)
    {
        int total = left.Length;
        LastRenderSilent = true;
        for (int off = 0; off < total; off += Block)
        {
            int n = Math.Min(Block, total - off);
            double dt = (double)n / SampleRate;

            // Sound changed: switch directly when silent, with an equal-power crossfade when playing
            string want = _library.Get(inp.SoundId).Id;
            if (!string.Equals(want, _soundId, StringComparison.OrdinalIgnoreCase) && _old == null)
            {
                var next = OpenSafe(want);
                if (_prevGain > 0) { _old = _src; _xfade = 0; }
                else _src.Dispose();
                _src = next;
                _soundId = want;
            }

            if (_src is ToneSource tone && inp.ToneHz > 0) tone.TargetHz = inp.ToneHz;

            if (inp.WantAmbient) _fade = Math.Min(1, _fade + dt / Math.Max(0.05, inp.FadeInSeconds));
            else _fade = Math.Max(0, _fade - dt / Math.Max(0.05, inp.FadeOutSeconds));

            double gDb = _fade > 0 ? (_fade - 1) * FadeRangeDb : double.NegativeInfinity;
            if (inp.KeepAlive) gDb = Math.Max(gDb, KeepAliveDb);
            double gain = double.IsNegativeInfinity(gDb) ? 0 : DbToLin(gDb);

            var outL = left.Slice(off, n);
            var outR = right.Slice(off, n);
            if (gain == 0 && _prevGain == 0)
            {
                outL.Clear(); outR.Clear();
                if (_old != null) { _old.Dispose(); _old = null; _xfade = 1; }
                continue;
            }
            LastRenderSilent = false;

            var bl = _l.AsSpan(0, n);
            var br = _r.AsSpan(0, n);
            try
            {
                _src.Render(bl, br);
                if (_old != null)
                {
                    var ol = _ol.AsSpan(0, n);
                    var or = _or.AsSpan(0, n);
                    _old.Render(ol, or);
                    for (int i = 0; i < n; i++)
                    {
                        double x = Math.Min(1, _xfade + (double)i / SampleRate / CrossfadeSeconds);
                        double gNew = Math.Sin(x * Math.PI / 2), gOld = Math.Cos(x * Math.PI / 2);
                        bl[i] = (float)(bl[i] * gNew + ol[i] * gOld);
                        br[i] = (float)(br[i] * gNew + or[i] * gOld);
                    }
                    _xfade += dt / CrossfadeSeconds;
                    if (_xfade >= 1) { _old.Dispose(); _old = null; _xfade = 1; }
                }
            }
            catch (Exception ex)
            {
                // Corrupt file etc.: silence this block, reopen the source, start slowly from silence
                Errors++;
                Core.Log.Info($"Sound read error ({_soundId}): {ex.Message}");
                outL.Clear(); outR.Clear();
                _src.Dispose();
                _src = OpenSafe(_soundId);
                _old?.Dispose(); _old = null;
                _fade = 0; _prevGain = 0;
                continue;
            }

            double g0 = _prevGain, dg = (gain - _prevGain) / n;
            for (int i = 0; i < n; i++)
            {
                double g = g0 + dg * (i + 1);
                double xl = bl[i] * g, xr = br[i] * g;
                if (!double.IsFinite(xl)) xl = 0;
                if (!double.IsFinite(xr)) xr = 0;
                outL[i] = Limit(xl);
                outR[i] = Limit(xr);
            }
            _prevGain = gain;
        }
    }

    private float Limit(double x)
    {
        double a = Math.Abs(x);
        if (a <= LimiterKnee) return (float)x;
        double y = LimiterKnee + (LimiterCeiling - LimiterKnee) * Math.Tanh((a - LimiterKnee) / (LimiterCeiling - LimiterKnee));
        if (a > y * 1.122) LimiterHits++;
        return (float)(x < 0 ? -y : y);
    }

    public void Dispose()
    {
        _src.Dispose();
        _old?.Dispose();
    }
}
