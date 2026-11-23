using System.Diagnostics;
using System.Globalization;
using OrtamSesi.Audio;
using OrtamSesi.Core;

namespace OrtamSesi.Tools;

/// <summary>
/// Self-test that needs no device: every sound actually plays (loop point included), equal levels,
/// the 1% volume calculation, fade in/out, ducking-decision scenarios and CPU load.
/// </summary>
internal static class SelfTest
{
    private static int _fail;
    /// The first seconds are skipped in measuring and testing: the smoother is still learning the sound's typical level (the sound fades in slowly anyway).
    private const int WarmupSeconds = 8;

    private static void Check(TextWriter w, bool ok, string what)
    {
        w.WriteLine($"  [{(ok ? "OK " : "FAIL")}] {what}");
        if (!ok) _fail++;
    }

    private static string F(double v, string fmt = "0.0") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public static int Run(TextWriter w, string? soundsDir)
    {
        _fail = 0;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var lib = SoundLibrary.Load(soundsDir);
        w.WriteLine($"Sound list: {lib.Sounds.Count} sounds ({lib.Directory})");
        Check(w, lib.Sounds.Count >= 20, $"at least 20 options available ({lib.Sounds.Count})");

        w.WriteLine();
        w.WriteLine("=== 1) Every sound: one full loop + loop point, level, clicks/pops/dropouts ===");
        const int fs = 44100; // mix rate of the Xiaomi Sound Party; 48 kHz recordings are resampled
        foreach (var s in lib.Sounds)
        {
            ISoundSource src;
            try { src = lib.Open(s, fs); }
            catch (Exception ex) { Check(w, false, $"{s.Id}: could not open: {ex.Message}"); continue; }
            using (src)
            {
                double seconds = 20;
                if (s.File != null)
                {
                    var m = Loudness.MeasureFile(Path.Combine(lib.Directory, s.File));
                    double loopLen = (s.LoopEnd > 0 ? s.LoopEnd : m.Seconds) - s.LoopStart;
                    seconds = Math.Clamp(loopLen + 8, 48, 420); // always get past the loop point
                }
                int n = (int)(seconds * fs);
                var l = new float[n]; var r = new float[n];
                for (int off = 0; off < n; off += 1024)
                {
                    int k = Math.Min(1024, n - off);
                    src.Render(l.AsSpan(off, k), r.AsSpan(off, k));
                }
                int skip = fs * WarmupSeconds; // skip the resampler's and smoother's initial learning period
                bool finite = true;
                for (int i = 0; i < n && finite; i++) finite = float.IsFinite(l[i]) && float.IsFinite(r[i]);
                double aDb = Loudness.LevelDb(l.AsSpan(skip), r.AsSpan(skip), fs);

                // RMS of 50 ms windows: look for sudden rises (clicks/pops) and sudden drops (dropouts) relative to the median
                int win = fs / 20;
                var wins = new List<double>();
                for (int off = skip; off + win <= n; off += win)
                {
                    double e = 0;
                    for (int i = off; i < off + win; i++) e += l[i] * (double)l[i] + r[i] * (double)r[i];
                    wins.Add(Spectrum.Db(e / (2.0 * win)));
                }
                var sorted = wins.OrderBy(x => x).ToList();
                double med = sorted[sorted.Count / 2];
                double max = sorted[^1], min = sorted[0];
                double minAt = (skip + wins.IndexOf(min) * (double)win) / fs, maxAt = (skip + wins.IndexOf(max) * (double)win) / fs;
                double peak = 0;
                for (int i = skip; i < n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                double peakDb = Spectrum.Db(peak * peak);
                w.WriteLine($" {s.Id,-13} {F(seconds, "0")} s: level {F(aDb)} dB, 50ms RMS median {F(med)} (lowest {F(min - med, "+0;-0")} dB @{F(minAt, "0.0")}s, highest {F(max - med, "+0;-0")} dB @{F(maxAt, "0.0")}s), peak {F(peakDb)} dBFS");
                Check(w, finite, $"{s.Id}: no NaN/infinity");
                Check(w, Math.Abs(aDb - Loudness.ReferenceDb) < (s.File != null ? 2.0 : 0.8), $"{s.Id}: level matches the common reference (±{(s.File != null ? 2 : 0.8)} dB)");
                // Wider limit for sounds with natural pauses (wind dies down, crickets fall silent, birds stop singing)
                bool pauses = s.Id is "storm" or "birds" or "city" or "coffee-shop" or "boat" or "train" or "fireplace" or "wind" or "summer-night";
                Check(w, min - med > (pauses ? -30 : -18), $"{s.Id}: no silence/dropout, loop included");
                Check(w, max - med < 15, $"{s.Id}: no sudden pop (loudest moment less than 15 dB above the typical level)");
                // Single-sample crackle peaks are softened by the final-stage limiter (-6 → -1 dBFS)
                Check(w, peakDb < 3, $"{s.Id}: peak within what the limiter can handle (< +3 dBFS)");

                if (s.File != null) LoopContinuity(w, lib, s);
            }
        }

        w.WriteLine();
        w.WriteLine("=== 2) Volume: at most 1%, the program never raises its own volume ===");
        {
            double target = new Settings().TargetLevelDb();
            w.WriteLine($" Maximum level: {F(target)} dBFS A-weighted (= 1% of the ≈ -20 dBFS amplitude of a normal loud sound)");
            Check(w, Math.Abs(target - -60) < 0.01, "default maximum level is -60 dB (1%)");
            float v = AudioEngine.SessionVolumeFor(target);
            double out1 = Loudness.ReferenceDb + 20 * Math.Log10(v);
            w.WriteLine($" session volume: {F(v * 100, "0.00")}% → {F(out1)} dBFS to the speaker");
            Check(w, Math.Abs(out1 - target) < 0.1, "level sent to the speaker = 1% target");
            for (int i = 1; i <= Settings.VolumeSteps.Length; i++)
            {
                var si = new Settings { Volume = i };
                Check(w, AudioEngine.SessionVolumeFor(si.TargetLevelDb()) <= v + 1e-6, $"\"{Settings.VolumeSteps[i - 1].Name}\" does not exceed 1%");
            }
            var s5 = new Settings { Volume = 5 };
            Check(w, s5.TargetLevelDb() < target - 20, "Whisper level is 24 dB below the maximum");
            var bad = new Settings { Advanced = { MaxLevelDb = 0 } }; bad.Normalize();
            Check(w, bad.TargetLevelDb() <= AdvancedSettings.HardMaxLevelDb, "cannot exceed 10% (-40 dB), whatever is written in the settings file");
        }

        w.WriteLine();
        w.WriteLine("=== 3) Fade in/out and sound switching ===");
        {
            const int rate = 44100;
            var rend = new AmbientRenderer(rate, lib, "brown");
            var inp = new RenderInputs { WantAmbient = true, SoundId = "brown", KeepAlive = true, FadeInSeconds = 6, FadeOutSeconds = 1.5 };
            var l = new float[441]; var r = new float[441];
            var gains = new List<double>();
            void Run(double sec, bool want, string id)
            {
                inp.WantAmbient = want; inp.SoundId = id;
                for (int i = 0; i < sec * 100; i++) { rend.Render(l, r, inp); gains.Add(AudioMath.LinToDb(rend.CurrentGain)); }
            }
            Run(8, true, "brown"); Run(4, false, "brown"); Run(8, true, "brown"); Run(4, true, "pink");
            double maxStep = 0;
            for (int i = 1; i < gains.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(gains[i] - gains[i - 1]));
            int reach = gains.FindIndex(g => g >= -1);
            int quiet = gains.FindIndex(800, g => g <= AmbientRenderer.KeepAliveDb + 0.1);
            w.WriteLine($" fade-in {F(reach / 100.0, "0.00")} s, fade-out {F((quiet - 800) / 100.0, "0.00")} s, largest gain change in 10 ms {F(maxStep, "0.00")} dB");
            Check(w, gains[0] <= AmbientRenderer.KeepAliveDb + 0.1, "starts from silence (no pop)");
            Check(w, reach is > 500 and < 650, "fades in gradually over ~6 s");
            Check(w, quiet > 800 && quiet - 800 <= 160, "fades out smoothly over ~1.5 s");
            Check(w, maxStep < 0.6, "no jump larger than 0.6 dB within 10 ms");
            Check(w, rend.SoundId == "pink", "sound switch applied (2 s crossfade)");
            rend.Dispose();
        }

        w.WriteLine();
        w.WriteLine("=== 4) Ducking decision (150 ms measurement interval, threshold -50/-60 dB, 2.5 s) ===");
        {
            const double dt = 0.15;
            double loud = AudioMath.DbToLin(-25), quietScene = AudioMath.DbToLin(-57), anydeskIdle = AudioMath.DbToLin(-66);
            (double duckAt, double unduckAt, int flips) Sim(Func<double, double> peakAt, double seconds)
            {
                var d = new DuckDetector();
                d.Configure(new AdvancedSettings());
                double duckAt = -1, unduckAt = -1; int flips = 0; bool prev = false;
                for (double t = 0; t < seconds; t += dt)
                {
                    bool now = d.Update(dt, peakAt(t));
                    if (now != prev) { flips++; if (now && duckAt < 0) duckAt = t; if (!now) unduckAt = t; prev = now; }
                }
                return (duckAt, unduckAt, flips);
            }
            Check(w, Sim(t => t >= 2 && t < 2.6 ? loud : 0, 20).flips == 0, "0.6 s notification sound → no duck");
            Check(w, Sim(t => t >= 2 && t < 3.8 ? loud : 0, 20).flips == 0, "1.8 s notification (like Telegram) → no duck");
            Check(w, Sim(t => t >= 2 && t < 600 ? anydeskIdle : 0, 620).flips == 0, "-66 dB constant background (AnyDesk idle) → no duck");
            var c = Sim(t => t >= 2 && t < 12 ? loud : 0, 30);
            w.WriteLine($"   10 s video: ducked after {F(c.duckAt - 2, "0.00")} s, resumed {F(c.unduckAt - 12, "0.00")} s after it ended");
            Check(w, c.duckAt - 2 is >= 2.3 and <= 2.9 && c.flips == 2, "video → ducks at ~2.5 s, resumes once when it ends");
            Check(w, c.unduckAt - 12 is >= 4.8 and <= 5.5, "waits ~5 s after a short video");
            Check(w, Sim(t => t >= 2 && t < 40 ? ((t % 1.2) < 0.8 ? loud : 0) : 0, 60).flips == 2, "intermittent speech → stays ducked without a break");
            var e = Sim(t => t >= 2 && t < 6 ? loud : t < 40 ? quietScene : 0, 60);
            Check(w, e.flips == 2 && e.unduckAt > 40, "quiet scene in a film (-57 dB) → does not resume");
            Check(w, Sim(t => (t >= 2 && t < 12) || (t >= 15 && t < 25) ? loud : 0, 40).flips == 2, "tab switch (3 s gap) → stays stable");
            var g = Sim(t => t >= 2 && t < 122 ? loud : 0, 160);
            Check(w, g.unduckAt - 122 is >= 9.8 and <= 10.5, $"waits ~10 s after long viewing ({F(g.unduckAt - 122, "0.0")} s)");
        }

        w.WriteLine();
        w.WriteLine("=== 5) CPU load (44.1 kHz) ===");
        foreach (var id in new[] { "waves", "waterfall", "brown", "tone" })
        {
            if (!lib.Sounds.Any(x => x.Id == id)) continue;
            var rend = new AmbientRenderer(44100, lib, id);
            var inp = new RenderInputs { WantAmbient = true, SoundId = id, ToneHz = 100, FadeInSeconds = 1, FadeOutSeconds = 1 };
            var l = new float[1764]; var r = new float[1764];
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 60 * 25; i++) rend.Render(l, r, inp);
            double ms = sw.Elapsed.TotalMilliseconds;
            rend.Dispose();
            w.WriteLine($" {id,-10}: 60 s of audio in {F(ms, "0")} ms → {F(ms / 600.0, "0.00")}% of one core");
            Check(w, ms < 3000, $"{id}: at least 20x faster than real time");
        }

        w.WriteLine();
        w.WriteLine(_fail == 0 ? "RESULT: all checks passed." : $"RESULT: {_fail} checks FAILED.");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Loop point: the file is played at its own sample rate (no resampler); the largest sample jump and level change
    /// within ±10 ms of the wrap-around moment are compared with the rest of the recording.
    /// </summary>
    private static void LoopContinuity(TextWriter w, SoundLibrary lib, SoundInfo s)
    {
        string path = Path.Combine(lib.Directory, s.File!);
        int rate; double fileSec;
        using (var v = new NVorbis.VorbisReader(path)) { rate = v.SampleRate; fileSec = (double)v.TotalSamples / rate; }
        double loopLen = (s.LoopEnd > 0 ? s.LoopEnd : fileSec) - s.LoopStart;
        int n = (int)((loopLen + 4) * rate);
        var l = new float[n]; var r = new float[n];
        var src = new LoopFileSource(path, rate, 0, s.LoopStart, s.LoopEnd);
        for (int off = 0; off < n; off += 4096) { int k = Math.Min(4096, n - off); src.Render(l.AsSpan(off, k), r.AsSpan(off, k)); }
        src.Dispose();
        if (src.Swaps.Count == 0) { Check(w, false, $"{s.Id}: loop did not wrap around"); return; }

        var jumps = new List<double>(n / 5);
        for (int i = rate; i < n; i += 5) jumps.Add(Math.Max(Math.Abs(l[i] - l[i - 1]), Math.Abs(r[i] - r[i - 1])));
        jumps.Sort();
        double p999 = jumps[(int)(jumps.Count * 0.999)];

        // Level difference between consecutive 100 ms windows: the recording's own natural fluctuation (99th percentile)
        int w100 = rate / 10;
        var deltas = new List<double>();
        for (int at = rate; at + 2 * w100 <= n; at += w100)
            deltas.Add(Math.Abs(Loudness.ZDb(l.AsSpan(at + w100, w100), r.AsSpan(at + w100, w100)) - Loudness.ZDb(l.AsSpan(at, w100), r.AsSpan(at, w100))));
        deltas.Sort();
        double natural = deltas[(int)(deltas.Count * 0.99)];

        int wnd = rate / 100;
        double worst = 0, worstLevel = 0;
        foreach (long sw in src.Swaps)
        {
            int at = (int)sw;
            if (at - w100 < 1 || at + w100 >= n) continue;
            double local = 0;
            for (int i = at - wnd; i < at + wnd; i++) local = Math.Max(local, Math.Max(Math.Abs(l[i] - l[i - 1]), Math.Abs(r[i] - r[i - 1])));
            double before = Loudness.ZDb(l.AsSpan(at - w100, w100), r.AsSpan(at - w100, w100));
            double after = Loudness.ZDb(l.AsSpan(at, w100), r.AsSpan(at, w100));
            worst = Math.Max(worst, local / Math.Max(1e-9, p999));
            worstLevel = Math.Max(worstLevel, Math.Abs(after - before));
        }
        w.WriteLine($"   loop point: wrapped around {src.Swaps.Count} times; largest jump {F(worst, "0.0")}x the recording's 99.9th percentile; level difference {F(worstLevel, "0.0")} dB (recording's natural 100 ms fluctuation {F(natural, "0.0")} dB)");
        Check(w, worst < 2.0 && worstLevel <= Math.Max(natural, 3), $"{s.Id}: no click/dropout at the loop point");
    }

    /// <summary>--peak-scan id: compares the largest peaks of the raw recording and the smoothed output (diagnostic tool).</summary>
    public static int PeakScan(TextWriter w, string id)
    {
        var lib = SoundLibrary.Load();
        var s = lib.Get(id);
        const int fs = 44100;
        int n = fs * 200;
        var path = Path.Combine(lib.Directory, s.File!);
        var raw = new LoopFileSource(path, fs, s.GainDb, s.LoopStart, s.LoopEnd);
        var sm = lib.Open(s, fs);
        var rl = new float[n]; var rr = new float[n]; var sl = new float[n]; var sr = new float[n];
        for (int off = 0; off < n; off += 4096)
        {
            int k = Math.Min(4096, n - off);
            raw.Render(rl.AsSpan(off, k), rr.AsSpan(off, k));
            sm.Render(sl.AsSpan(off, k), sr.AsSpan(off, k));
        }
        int look = fs * 5 / 1000;
        // Run a separate smoother over the raw copy and watch its internal state around the pop
        int pop = Enumerable.Range(fs * 8, n - fs * 8).OrderByDescending(i => Math.Abs(sl[i])).First() - look;
        var ts = new TransientSmoother(fs);
        var cl = (float[])rl.Clone(); var cr = (float[])rr.Clone();
        for (int i = 0; i < n; i++)
        {
            ts.Process(cl.AsSpan(i, 1), cr.AsSpan(i, 1));
            if (i >= pop - 16 && i <= pop + look + 8 && (i - pop) % 24 == 0)
                w.WriteLine($"   i-pop={i - pop,4}  raw {F(Spectrum.Db(rl[i] * (double)rl[i]))}  gain {F(ts.GainDb)} dB  typical {F(ts.TypicalDb)} dB");
        }
        var top = Enumerable.Range(fs * 8, n - fs * 8).OrderByDescending(i => Math.Abs(sl[i])).Take(5);
        foreach (int i in top)
            w.WriteLine($" t={F((double)i / fs, "0.000")}s  output {F(Spectrum.Db(sl[i] * (double)sl[i]))} dBFS  raw(5 ms earlier) {F(Spectrum.Db(rl[Math.Max(0, i - look)] * (double)rl[Math.Max(0, i - look)]))} dBFS  raw(same instant) {F(Spectrum.Db(rl[i] * (double)rl[i]))} dBFS");
        return 0;
    }

    /// <summary>--level-profile file.ogg: level second by second (for choosing loop start/end points).</summary>
    public static int LevelProfile(TextWriter w, string path)
    {
        using var v = new NVorbis.VorbisReader(path);
        int ch = v.Channels, fs = v.SampleRate;
        var buf = new float[fs * ch];
        int sec = 0, got;
        var line = new System.Text.StringBuilder();
        while ((got = v.ReadSamples(buf, 0, buf.Length)) > 0)
        {
            double e = 0;
            for (int i = 0; i < got; i++) e += buf[i] * (double)buf[i];
            line.Append($"{sec}s:{F(Spectrum.Db(e / got), "0")} ");
            if (++sec % 15 == 0) { w.WriteLine(line.ToString()); line.Clear(); }
        }
        if (line.Length > 0) w.WriteLine(line.ToString());
        return 0;
    }

    /// <summary>--measure-sounds: measures the level of every recording in sounds.json and writes its gainDb.</summary>
    public static int MeasureSounds(TextWriter w, string? dir)
    {
        dir ??= SoundLibrary.DefaultDirectory;
        string path = Path.Combine(dir, "sounds.json");
        var m = System.Text.Json.JsonSerializer.Deserialize<SoundManifest>(File.ReadAllText(path), SoundLibrary.Json)!;
        foreach (var s in m.Sounds.Where(x => x.File != null))
        {
            var f = Path.Combine(dir, s.File!);
            if (!File.Exists(f)) { w.WriteLine($" {s.Id}: FILE MISSING ({s.File})"); continue; }
            // Measure the loop region as it actually sounds (after the smoother)
            var src = new SmoothedSource(new LoopFileSource(f, 44100, 0, s.LoopStart, s.LoopEnd), 44100);
            var meas = Loudness.MeasureFile(f);
            double loopLen = (s.LoopEnd > 0 ? s.LoopEnd : meas.Seconds) - s.LoopStart;
            double len = Math.Clamp(loopLen * 2 + WarmupSeconds, 48, 240); // short recordings are measured over several loops
            int n = (int)(len * 44100);
            var l = new float[n]; var r = new float[n];
            for (int off = 0; off < n; off += 4096) { int k = Math.Min(4096, n - off); src.Render(l.AsSpan(off, k), r.AsSpan(off, k)); }
            src.Dispose();
            int skip = 44100 * WarmupSeconds;
            double aDb = Loudness.LevelDb(l.AsSpan(skip), r.AsSpan(skip), 44100);
            s.GainDb = Math.Round(Loudness.ReferenceDb - aDb, 2);
            w.WriteLine($" {s.Id,-13} {F(meas.Seconds, "0")} s, {meas.Rate} Hz, {meas.Channels} channels, peak {F(meas.PeakDb)} dBFS, A {F(aDb)} dB → gain {F(s.GainDb, "0.00")} dB");
        }
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(m, SoundLibrary.Json));
        w.WriteLine($"written: {path}");
        return 0;
    }
}
