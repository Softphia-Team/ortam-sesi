using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OrtamSesi.Audio;
using OrtamSesi.Interop;

namespace OrtamSesi.Tools;

/// <summary>
/// Diagnostics: lists the output devices and the live level of every app (session peak meter).
/// With --csv every reading is written as a row (used to analyse the real-device tests).
/// </summary>
internal static class Diag
{
    /// <summary>--mute-state: mute state of the default device and of every app session (for tests).</summary>
    public static int MuteState(TextWriter w) => InMta(() =>
    {
        var en = CoreAudioUtil.CreateEnumerator();
        var dev = CoreAudioUtil.TryGetDefaultRender(en) ?? throw new InvalidOperationException("no output device");
        var ep = CoreAudioUtil.ActivateEndpointVolume(dev);
        ep.GetMute(out bool m);
        ep.GetMasterVolumeLevelScalar(out float v);
        w.WriteLine($"device: {CoreAudioUtil.GetFriendlyName(dev)} muted={(m ? 1 : 0)} volume={v * 100:0}%");
        var iid = AudioConst.IID_IAudioSessionManager2;
        dev.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out var o);
        ((IAudioSessionManager2)o).GetSessionEnumerator(out var sessions);
        sessions.GetCount(out int n);
        var names = new ProcessNames();
        for (int i = 0; i < n; i++)
        {
            sessions.GetSession(i, out var s);
            var c = (IAudioSessionControl2)s;
            c.GetProcessId(out uint pid);
            c.GetState(out int st);
            bool sys = c.IsSystemSoundsSession() == 0;
            ((ISimpleAudioVolume)s).GetMute(out bool sm);
            w.WriteLine($"session: {(sys ? "system" : names.Get(pid))} pid={pid} state={st} muted={(sm ? 1 : 0)}");
            CoreAudioUtil.Release(s);
        }
        CoreAudioUtil.Release(sessions); CoreAudioUtil.Release(o); CoreAudioUtil.Release(ep); CoreAudioUtil.Release(dev); CoreAudioUtil.Release(en);
        return 0;
    });

    /// <summary>--set-mute on|off: mutes/unmutes the default device (simulates the mute key; for tests).</summary>
    public static int SetMute(TextWriter w, bool mute) => InMta(() =>
    {
        var en = CoreAudioUtil.CreateEnumerator();
        var dev = CoreAudioUtil.TryGetDefaultRender(en) ?? throw new InvalidOperationException("no output device");
        var ep = CoreAudioUtil.ActivateEndpointVolume(dev);
        var ctx = Guid.Empty; // looks like a change made by the user
        ep.SetMute(mute, ref ctx);
        w.WriteLine($"device muted={(mute ? 1 : 0)}");
        CoreAudioUtil.Release(ep); CoreAudioUtil.Release(dev); CoreAudioUtil.Release(en);
        return 0;
    });

    private static int InMta(Func<int> body)
    {
        int rc = 0;
        Exception? err = null;
        var t = new Thread(() => { try { rc = body(); } catch (Exception ex) { err = ex; } });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (err != null) throw err;
        return rc;
    }

    public static int Run(TextWriter w, double seconds, string? csvPath, int intervalMs)
    {
        int rc = 0;
        var t = new Thread(() => rc = RunMta(w, seconds, csvPath, intervalMs));
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return rc;
    }

    private static int RunMta(TextWriter w, double seconds, string? csvPath, int intervalMs)
    {
        var en = CoreAudioUtil.CreateEnumerator();
        var def = CoreAudioUtil.TryGetDefaultRender(en);
        string? defId = def == null ? null : CoreAudioUtil.GetId(def);
        CoreAudioUtil.Release(def);
        var devices = CoreAudioUtil.ListActiveRender(en);
        w.WriteLine("OUTPUT DEVICES (active):");
        foreach (var d in devices)
            w.WriteLine($"  {(d.Id == defId ? "[default]" : "         ")} {d.Name}   {d.Id}");

        var managers = new List<(string Name, IAudioSessionManager2 Mgr)>();
        foreach (var d in devices)
        {
            en.GetDevice(d.Id, out var dev);
            var iid = AudioConst.IID_IAudioSessionManager2;
            dev.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out var o);
            managers.Add((d.Name, (IAudioSessionManager2)o));
            CoreAudioUtil.Release(dev);
        }

        using var csv = csvPath == null ? null : new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        csv?.WriteLine("t_ms,wall,device,pid,app,state,peak_db,muted,volume,system");
        var names = new ProcessNames();
        var sw = Stopwatch.StartNew();
        long lastPrint = -1000;
        w.WriteLine();
        w.WriteLine($"SESSIONS ({seconds:0.#} s, every {intervalMs} ms; active ones only):");
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            long ms = sw.ElapsedMilliseconds;
            string wall = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            bool print = ms - lastPrint >= 1000;
            if (print) lastPrint = ms;
            foreach (var (devName, mgr) in managers)
            {
                mgr.GetSessionEnumerator(out var sessions);
                sessions.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    object? o = null;
                    try
                    {
                        sessions.GetSession(i, out o);
                        var c = (IAudioSessionControl2)o;
                        c.GetState(out int st);
                        if (st != AudioConst.AudioSessionStateActive) continue;
                        c.GetProcessId(out uint pid);
                        bool sys = c.IsSystemSoundsSession() == 0;
                        ((IAudioMeterInformation)o).GetPeakValue(out float peak);
                        var v = (ISimpleAudioVolume)o;
                        v.GetMute(out bool mute);
                        v.GetMasterVolume(out float vol);
                        string app = sys ? "system" : names.Get(pid);
                        string db = AudioMath.LinToDb(peak).ToString("0.0", CultureInfo.InvariantCulture);
                        csv?.WriteLine($"{ms},{wall},\"{devName}\",{pid},{app},{st},{db},{(mute ? 1 : 0)},{vol.ToString("0.00", CultureInfo.InvariantCulture)},{(sys ? 1 : 0)}");
                        if (print) w.WriteLine($"  {ms / 1000.0,6:0.0}s  {app,-16} pid {pid,-6} peak {db,6} dB  vol {vol:0.00}{(mute ? "  [muted]" : "")}  @ {devName}");
                    }
                    catch (COMException) { }
                    finally { CoreAudioUtil.Release(o); }
                }
                CoreAudioUtil.Release(sessions);
            }
            Thread.Sleep(intervalMs);
        }
        foreach (var m in managers) CoreAudioUtil.Release(m.Mgr);
        CoreAudioUtil.Release(en);
        return 0;
    }
}
