using System.Collections.Concurrent;
using System.Text.Json;
using NVorbis;

namespace OrtamSesi.Audio;

/// <summary>An ambient sound: either a recording file (sounds/*.ogg) or a generated noise.</summary>
internal sealed class SoundInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>OGG Vorbis file in the sounds folder (recording) — or null.</summary>
    public string? File { get; set; }
    /// <summary>white | pink | brown (generated noise) — or null.</summary>
    public string? Generator { get; set; }
    /// <summary>Gain that brings the recording to the common loudness (A-weighted -20 dBFS). Measured with --measure-sounds.</summary>
    public double GainDb { get; set; }
    /// <summary>Loop start (s). Used to skip a slow fade-in etc. at the start of the recording.</summary>
    public double LoopStart { get; set; }
    /// <summary>Loop end (s). 0 = end of file. Used to skip the fade-out/ending part at the end.</summary>
    public double LoopEnd { get; set; }
    public string? Author { get; set; }
    public string? License { get; set; }
    public string? Source { get; set; }
}

internal sealed class SoundManifest
{
    public List<SoundInfo> Sounds { get; set; } = new();
}

/// <summary>Reads sounds/sounds.json. To add a new sound, just put the file there and add one line to the list.</summary>
internal sealed class SoundLibrary
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Directory { get; }
    public IReadOnlyList<SoundInfo> Sounds { get; }

    private SoundLibrary(string dir, IReadOnlyList<SoundInfo> sounds) { Directory = dir; Sounds = sounds; }

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "sounds");

    public static SoundLibrary Load(string? dir = null)
    {
        dir ??= DefaultDirectory;
        var list = new List<SoundInfo>();
        try
        {
            var path = Path.Combine(dir, "sounds.json");
            if (System.IO.File.Exists(path))
            {
                var m = JsonSerializer.Deserialize<SoundManifest>(System.IO.File.ReadAllText(path), Json);
                if (m != null)
                    list.AddRange(m.Sounds.Where(s => !string.IsNullOrWhiteSpace(s.Id) &&
                        (s.Generator != null || (s.File != null && System.IO.File.Exists(Path.Combine(dir, s.File))))));
            }
        }
        catch (Exception ex) { Core.Log.Info($"Could not read sound list: {ex.Message}"); }

        // Generated sounds are always available, even if files are missing
        foreach (var (id, name, group) in new[]
                 {
                     ("brown", "Brown noise", "Noise"), ("pink", "Pink noise", "Noise"),
                     ("white", "White noise", "Noise"), ("tone", "Tone generator", "Tone"),
                 })
            if (!list.Exists(s => s.Id == id)) list.Add(new SoundInfo { Id = id, Name = name, Group = group, Generator = id });
        return new SoundLibrary(dir, list);
    }

    public SoundInfo Get(string? id) =>
        Sounds.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Sounds[0];

    /// <summary>Opens the sound as a ready-to-play source; its output is normalized to the common loudness (≈ -20 dBFS A).</summary>
    public ISoundSource Open(SoundInfo info, int sampleRate)
    {
        if (info.Generator == "tone") return new ToneSource(sampleRate);
        if (info.Generator != null) return new NoiseSource(info.Generator, sampleRate);
        // Recordings: sudden peaks (wave crashes, drips, chirps, crackles) are softened for sleep
        return new SmoothedSource(
            new LoopFileSource(Path.Combine(Directory, info.File!), sampleRate, info.GainDb, info.LoopStart, info.LoopEnd),
            sampleRate);
    }
}

internal interface ISoundSource : IDisposable
{
    /// <summary>Writes the next samples (normalized, ≈ -20 dBFS A-weighted RMS).</summary>
    void Render(Span<float> left, Span<float> right);
}

/// <summary>White / pink / brown noise. The extremes are gently softened to make it suitable for sleep.</summary>
internal sealed class NoiseSource : ISoundSource
{
    private static readonly ConcurrentDictionary<(string, int), double> NormCache = new();
    private readonly string _kind;
    private readonly Rng _a, _b;
    private readonly PinkFilter _pl = new(), _pr = new();
    private readonly Biquad _hl1 = new(), _hl2 = new(), _hr1 = new(), _hr2 = new(), _ll = new(), _lr = new();
    private readonly double _leak, _mixA, _mixB;
    private double _bl, _br, _norm = 1;

    public NoiseSource(string kind, int fs, bool calibrate = true, ulong seed = 0)
    {
        _kind = kind;
        seed = seed == 0 ? (ulong)Environment.TickCount64 * 2654435761UL : seed;
        _a = new Rng(seed ^ 0x5DEECE66DUL);
        _b = new Rng(seed * 31 + 7);
        _leak = Math.Exp(-2 * Math.PI * 30.0 / fs);
        double theta = Math.Asin(0.3) / 2; // left/right slightly correlated: wide but not diffuse
        _mixA = Math.Cos(theta); _mixB = Math.Sin(theta);
        double hp = kind == "brown" ? 45 : 30;
        foreach (var (h1, h2) in new[] { (_hl1, _hl2), (_hr1, _hr2) })
        {
            h1.SetHighpass(fs, hp, 0.5412);
            h2.SetHighpass(fs, hp, 1.3066);
        }
        // The highest part of white noise tires the ear: everything above 9 kHz is gently rolled off
        _ll.SetLowpass(fs, kind == "white" ? 9000 : 16000, 0.707);
        _lr.SetLowpass(fs, kind == "white" ? 9000 : 16000, 0.707);
        if (calibrate)
            _norm = NormCache.GetOrAdd((kind, fs), _ =>
            {
                var probe = new NoiseSource(kind, fs, calibrate: false, seed: 12345);
                var l = new float[fs * 4]; var r = new float[fs * 4];
                probe.Render(l, r);
                double lvl = Loudness.LevelDb(l.AsSpan(fs / 2), r.AsSpan(fs / 2), fs);
                return Math.Pow(10, (Loudness.ReferenceDb - lvl) / 20);
            });
    }

    public void Render(Span<float> left, Span<float> right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            double n1 = _a.NextSigned(), n2 = _b.NextSigned();
            double xl = _mixA * n1 + _mixB * n2, xr = _mixB * n1 + _mixA * n2;
            switch (_kind)
            {
                case "pink": xl = _pl.Next(xl); xr = _pr.Next(xr); break;
                case "brown":
                    _bl = _bl * _leak + xl * 0.05; _br = _br * _leak + xr * 0.05;
                    xl = _bl; xr = _br; break;
            }
            xl = _ll.Process(_hl2.Process(_hl1.Process(xl)));
            xr = _lr.Process(_hr2.Process(_hr1.Process(xr)));
            left[i] = (float)(xl * _norm);
            right[i] = (float)(xr * _norm);
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Tone generator: pure sine at the selected frequency (like szynalski.com/tone-generator, single tone).
/// When the frequency changes it glides smoothly over 0.6 s (no clicks). Level: A-weighted loudness is brought
/// to the common reference, but gain is limited for low tones (unweighted level at most 10 dB above the reference) —
/// so the speaker is not strained.
/// </summary>
internal sealed class ToneSource : ISoundSource
{
    private readonly int _fs;
    private double _phase, _hz, _gain;
    private bool _started;
    public double TargetHz { get; set; } = 100;

    public ToneSource(int fs) { _fs = fs; _hz = TargetHz; _gain = GainFor(_hz); }

    /// A sine's RMS is 1/√2: first to 0 dBFS RMS, then to the reference; limited so A-weighting doesn't over-boost low tones.
    public static double GainFor(double hz)
    {
        double aDb = 20 * Math.Log10(Math.Max(1e-6, Spectrum.AWeight(hz)));
        double boostDb = Math.Min(-aDb, 10);
        return Math.Sqrt(2) * Math.Pow(10, (Loudness.ReferenceDb + boostDb) / 20);
    }

    public void Render(Span<float> left, Span<float> right)
    {
        double target = Math.Clamp(TargetHz, 20, 2000);
        double k = 1 - Math.Exp(-1.0 / (0.15 * _fs)); // reaches the target in ~0.6 s
        double gTarget = GainFor(target);
        if (!_started) { _hz = target; _gain = gTarget; _started = true; }
        for (int i = 0; i < left.Length; i++)
        {
            _hz += (target - _hz) * k;
            _gain += (gTarget - _gain) * k;
            _phase += _hz / _fs;
            if (_phase >= 1) _phase -= 1;
            float v = (float)(Math.Sin(2 * Math.PI * _phase) * _gain);
            left[i] = v; right[i] = v;
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Plays an OGG recording in an endless loop. The loop point is hidden by a 0.5–3 s equal-power crossfade
/// (the end blends smoothly into the start): no click, gap or "it rewound" feeling.
/// The file is streamed (not loaded fully into memory); the sample rate is converted if needed.
/// </summary>
internal sealed class LoopFileSource : ISoundSource
{
    private readonly string _path;
    private readonly double _gain;
    private VorbisReader _cur;
    private VorbisReader? _next;
    private readonly int _channels, _inRate;
    private readonly long _start, _total; // loop region: [_start, _start + _total)
    private readonly long _xfade;
    private long _pos, _nextPos;
    private float[] _bufA = new float[8192], _bufB = new float[8192];
    private readonly Resampler? _resampler;
    private float[] _inL = new float[4096], _inR = new float[4096];

    public LoopFileSource(string path, int outRate, double gainDb, double loopStartSec = 0, double loopEndSec = 0)
    {
        _path = path;
        _gain = Math.Pow(10, gainDb / 20);
        _cur = new VorbisReader(path);
        _channels = _cur.Channels;
        _inRate = _cur.SampleRate;
        long fileLen = Math.Max(1, _cur.TotalSamples);
        _start = Math.Clamp((long)(loopStartSec * _inRate), 0, fileLen - 1);
        long end = loopEndSec > 0 ? Math.Clamp((long)(loopEndSec * _inRate), _start + 1, fileLen) : fileLen;
        _total = end - _start;
        _xfade = Math.Clamp(Math.Min((long)(_inRate * 3.0), _total / 4), Math.Min(_inRate / 2, _total / 4), _total / 2);
        if (_start > 0) _cur.SamplePosition = _start;
        if (_inRate != outRate) _resampler = new Resampler(_inRate, outRate);
    }

    private VorbisReader OpenAtLoopStart()
    {
        var r = new VorbisReader(_path);
        if (_start > 0) r.SamplePosition = _start;
        return r;
    }

    public void Render(Span<float> left, Span<float> right)
    {
        if (_resampler == null) ReadLooped(left, right);
        else _resampler.Process(left, right, ReadLooped);
        if (_gain != 1)
            for (int i = 0; i < left.Length; i++) { left[i] *= (float)_gain; right[i] *= (float)_gain; }
    }

    /// <summary>Looped, crossfaded reading at the file's own sample rate.</summary>
    private void ReadLooped(Span<float> left, Span<float> right)
    {
        int n = left.Length, done = 0;
        while (done < n)
        {
            long regionStart = _total - _xfade;
            bool inFade = _pos >= regionStart;
            long boundary = inFade ? _total : regionStart;
            int chunk = (int)Math.Min(n - done, Math.Max(1, boundary - _pos));
            int got = Read(_cur, ref _bufA, chunk);
            if (got == 0)
            {
                // File ended earlier than expected: switch to the next one immediately
                Swap();
                continue;
            }
            if (inFade)
            {
                if (_next == null) { _next = OpenAtLoopStart(); _nextPos = 0; }
                int gotB = Read(_next, ref _bufB, got);
                _nextPos += gotB;
                if (gotB < got) Array.Clear(_bufB, gotB * 2, (got - gotB) * 2);
                for (int i = 0; i < got; i++)
                {
                    double x = Math.Clamp((double)(_pos + i - regionStart) / _xfade, 0, 1);
                    double gOld = Math.Cos(x * Math.PI / 2), gNew = Math.Sin(x * Math.PI / 2);
                    left[done + i] = (float)(_bufA[2 * i] * gOld + _bufB[2 * i] * gNew);
                    right[done + i] = (float)(_bufA[2 * i + 1] * gOld + _bufB[2 * i + 1] * gNew);
                }
            }
            else
            {
                for (int i = 0; i < got; i++) { left[done + i] = _bufA[2 * i]; right[done + i] = _bufA[2 * i + 1]; }
            }
            done += got;
            _pos += got;
            _framesOut += got;
            if (_pos >= _total) Swap();
        }
    }

    /// <summary>For tests: the moments the loop wrapped to the start (output frame index, at the file's own rate).</summary>
    public List<long> Swaps { get; } = new();
    private long _framesOut;

    private void Swap()
    {
        Swaps.Add(_framesOut);
        _cur.Dispose();
        if (_next != null)
        {
            _cur = _next;
            _next = null;
            _pos = _nextPos; // the new reader already advanced this far during the crossfade
        }
        else
        {
            _cur = OpenAtLoopStart();
            _pos = 0;
        }
    }

    /// <summary>Reads at most 'frames' frames and writes them to buf as stereo (L,R,L,R…).</summary>
    private int Read(VorbisReader r, ref float[] buf, int frames)
    {
        int need = frames * _channels;
        if (_inL.Length < need) { _inL = new float[need]; _inR = new float[need]; }
        if (buf.Length < frames * 2) buf = new float[frames * 2];
        int total = 0;
        while (total < need)
        {
            int got = r.ReadSamples(_inL, total, need - total);
            if (got <= 0) break;
            total += got;
        }
        int outFrames = total / _channels;
        for (int i = 0; i < outFrames; i++)
        {
            float l = _inL[i * _channels];
            float rr = _channels > 1 ? _inL[i * _channels + 1] : l;
            buf[2 * i] = l; buf[2 * i + 1] = rr;
        }
        return outFrames;
    }

    public void Dispose()
    {
        _cur.Dispose();
        _next?.Dispose();
    }
}

/// <summary>Passes a source through the sleep smoother.</summary>
internal sealed class SmoothedSource : ISoundSource
{
    private readonly ISoundSource _inner;
    private readonly TransientSmoother _smoother;
    public ISoundSource Inner => _inner;

    public SmoothedSource(ISoundSource inner, int fs) { _inner = inner; _smoother = new TransientSmoother(fs); }

    public void Render(Span<float> left, Span<float> right)
    {
        _inner.Render(left, right);
        _smoother.Process(left, right);
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Sleep smoother: a slow-reacting compressor with 5 ms lookahead. It tracks the sound's "typical" level over
/// the last few seconds (dB average); moments that rise more than 6 dB above it (wave crashes, drips,
/// bird chirps, fireplace crackles) are gently turned down and never allowed above 12 dB. Continuous sounds
/// (waterfall, noise) are unaffected. Goal: no sudden sound should wake a sleeping person.
/// </summary>
internal sealed class TransientSmoother
{
    private const double KneeDb = 6, LimitDb = 12, Ratio = 3;
    private const int Control = 8; // gain is computed once every 8 samples (fast enough, cheap)
    private readonly int _look;
    private readonly float[] _dl, _dr;
    private int _idx, _n;
    private double _env = 1e-10, _typicalDb = double.NaN, _gainDb, _gain = 1, _blockPeak, _peakHold;
    private int _holdLeft;
    /// For noise-like sounds the peak is ~12 dB above the average; moments spikier than that count as a "pop".
    private const double CrestPower = 15.85; // 10^(12/10)
    /// For diagnostics: the current gain and the learned typical level (dB).
    public double GainDb => _gainDb;
    public double TypicalDb => _typicalDb;
    private readonly double _aAtt, _aRel, _aTypical, _aTypicalFast, _aGainDown, _aGainUp;
    private readonly int _warmup;

    public TransientSmoother(int fs)
    {
        _look = Math.Max(1, fs * 5 / 1000);
        _dl = new float[_look]; _dr = new float[_look];
        _aAtt = 1 - Math.Exp(-1.0 / (0.002 * fs));
        _aRel = 1 - Math.Exp(-1.0 / (0.060 * fs));
        _aTypical = 1 - Math.Exp(-Control / (6.0 * fs));      // ~6 s "typical level"
        _aTypicalFast = 1 - Math.Exp(-Control / (0.5 * fs));  // learn fast for the first 2 s
        _aGainDown = 1 - Math.Exp(-Control / (0.001 * fs));   // attenuate: immediately (lookahead means nothing is missed)
        _aGainUp = 1 - Math.Exp(-Control / (0.300 * fs));     // release: slow, no "pumping"
        _warmup = fs * 2;
    }

    public void Process(Span<float> l, Span<float> r)
    {
        for (int i = 0; i < l.Length; i++)
        {
            double x2 = 0.5 * (l[i] * (double)l[i] + r[i] * (double)r[i]);
            _env += (x2 > _env ? _aAtt : _aRel) * (x2 - _env);
            double pk = Math.Max(l[i] * (double)l[i], r[i] * (double)r[i]);
            if (pk > _blockPeak) _blockPeak = pk;
            if (_n % Control == 0)
            {
                double envDb = 10 * Math.Log10(_env + 1e-12);
                // Single-sample spikes (wood pops) barely move the 2 ms average: for the attenuation decision,
                // the peak is used if the peak/average ratio is above normal (the typical level is still learned from the average)
                // The detected peak is held for the lookahead time (+2 ms): otherwise a single-sample pop would show up
                // in only one computation step and the gain would reopen before the pop reaches the output
                if (_blockPeak >= _peakHold) { _peakHold = _blockPeak; _holdLeft = _look + _look / 2; }
                else if ((_holdLeft -= Control) <= 0) _peakHold *= 0.7;
                double detDb = 10 * Math.Log10(Math.Max(_env, _peakHold / CrestPower) + 1e-12);
                _blockPeak = 0;
                if (double.IsNaN(_typicalDb)) _typicalDb = envDb;
                // Typical level = average in dB (behaves like a median); occasional peaks can't pull it much
                double a = _n < _warmup ? _aTypicalFast : _aTypical;
                _typicalDb += a * (Math.Min(envDb, _typicalDb + LimitDb) - _typicalDb);
                double over = detDb - _typicalDb;
                double target = over <= KneeDb ? 0
                    : over <= LimitDb ? -(over - KneeDb) * (1 - 1 / Ratio)
                    : -((LimitDb - KneeDb) * (1 - 1 / Ratio) + (over - LimitDb));
                _gainDb += (target < _gainDb ? _aGainDown : _aGainUp) * (target - _gainDb);
                _gain = Math.Exp(_gainDb * 0.11512925464970229); // 10^(dB/20)
            }
            _n++;
            float dl = _dl[_idx], dr = _dr[_idx];
            _dl[_idx] = l[i]; _dr[_idx] = r[i];
            if (++_idx == _look) _idx = 0;
            l[i] = (float)(dl * _gain);
            r[i] = (float)(dr * _gain);
        }
    }
}

/// <summary>
/// Windowed-sinc sample rate converter (32 taps, 512-phase table, linear interpolation between phases).
/// Near-lossless for ambient sounds; uses only a few tenths of a percent of one CPU core.
/// </summary>
internal sealed class Resampler
{
    private const int Half = 16, Taps = 2 * Half, Phases = 512;
    private readonly double _step;
    private readonly float[] _table;
    private float[] _l = new float[4096], _r = new float[4096];
    private int _count;
    private double _t;

    public Resampler(int inRate, int outRate)
    {
        _step = (double)inRate / outRate;
        double fc = Math.Min(1.0, (double)outRate / inRate) * 0.94; // cutoff slightly below Nyquist
        _table = new float[(Phases + 1) * Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                double x = k - (Half - 1) - frac;           // distance of the tap from the fractional position
                double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * fc * x) / (Math.PI * fc * x);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / Half) + 0.08 * Math.Cos(2 * Math.PI * x / Half); // Blackman
                double h = Math.Abs(x) >= Half ? 0 : fc * sinc * w;
                _table[p * Taps + k] = (float)h;
                sum += h;
            }
            for (int k = 0; k < Taps; k++) _table[p * Taps + k] /= (float)sum; // DC gain exactly 1
        }
        _count = Half; // initial history = silence
        _t = Half - 1;
    }

    public delegate void Pull(Span<float> left, Span<float> right);

    public void Process(Span<float> outL, Span<float> outR, Pull pull)
    {
        int n = outL.Length;
        int needEnd = (int)Math.Ceiling(_t + n * _step) + Half + 1;
        if (needEnd > _count)
        {
            if (needEnd > _l.Length)
            {
                Array.Resize(ref _l, needEnd * 2);
                Array.Resize(ref _r, needEnd * 2);
            }
            pull(_l.AsSpan(_count, needEnd - _count), _r.AsSpan(_count, needEnd - _count));
            _count = needEnd;
        }
        for (int j = 0; j < n; j++)
        {
            double pos = _t + j * _step;
            int i = (int)pos;
            double ph = (pos - i) * Phases;
            int p0 = (int)ph;
            float a = (float)(ph - p0);
            int baseIdx = i - (Half - 1);
            int t0 = p0 * Taps, t1 = t0 + Taps;
            float accL = 0, accR = 0;
            for (int k = 0; k < Taps; k++)
            {
                float c = _table[t0 + k] + (_table[t1 + k] - _table[t0 + k]) * a;
                accL += _l[baseIdx + k] * c;
                accR += _r[baseIdx + k] * c;
            }
            outL[j] = accL; outR[j] = accR;
        }
        _t += n * _step;
        int drop = (int)_t - Half;
        if (drop > 0)
        {
            Array.Copy(_l, drop, _l, 0, _count - drop);
            Array.Copy(_r, drop, _r, 0, _count - drop);
            _count -= drop;
            _t -= drop;
        }
    }
}

/// <summary>
/// Perceived loudness measurement. All sounds are matched by this measure: A-weighted RMS (close to the ear's
/// sensitivity at low levels) — but for bass-heavy sounds (underwater, tone) the unweighted level may not exceed it
/// by more than 10 dB; otherwise excessive bass would be pushed out for "equal loudness" and strain the speaker.
/// </summary>
internal static class Loudness
{
    /// Every sound is normalized to this level (dBFS). Leaves ample headroom (waves, drips, crackles don't clip);
    /// the actual level reaching the ear is set in AudioEngine via the session volume (at most 1%).
    public const double ReferenceDb = -32;
    public const double BassAllowanceDb = 10;

    public static double AWeightedDb(ReadOnlySpan<float> l, ReadOnlySpan<float> r, int fs)
    {
        double z = ZDb(l, r);
        double ratio = (Spectrum.AWeightedRatio(l, fs) + Spectrum.AWeightedRatio(r, fs)) / 2;
        return z + Spectrum.Db(ratio);
    }

    public static double ZDb(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        double z = 0;
        for (int i = 0; i < l.Length; i++) z += l[i] * (double)l[i] + r[i] * (double)r[i];
        return Spectrum.Db(z / Math.Max(1, 2.0 * l.Length));
    }

    /// <summary>Measure used for matching: max(A-weighted, unweighted − 10 dB).</summary>
    public static double LevelDb(ReadOnlySpan<float> l, ReadOnlySpan<float> r, int fs) =>
        Math.Max(AWeightedDb(l, r, fs), ZDb(l, r) - BassAllowanceDb);

    /// <summary>Decodes an entire OGG file and measures its A-weighted level (tool: --measure-sounds).</summary>
    public static (double ADb, double PeakDb, double Seconds, int Rate, int Channels) MeasureFile(string path)
    {
        using var v = new VorbisReader(path);
        int ch = v.Channels, fs = v.SampleRate;
        var all = new List<float>((int)Math.Min(v.TotalSamples * ch, 50_000_000));
        var buf = new float[fs * ch];
        int got;
        while ((got = v.ReadSamples(buf, 0, buf.Length)) > 0) for (int i = 0; i < got; i++) all.Add(buf[i]);
        int frames = all.Count / ch;
        var l = new float[frames]; var r = new float[frames];
        double peak = 0;
        for (int i = 0; i < frames; i++)
        {
            l[i] = all[i * ch]; r[i] = ch > 1 ? all[i * ch + 1] : l[i];
            peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        }
        return (AWeightedDb(l, r, fs), Spectrum.Db(peak * peak), (double)frames / fs, fs, ch);
    }
}
