using System.Runtime.InteropServices;
using System.Text;
using OrtamSesi.Core;
using OrtamSesi.Interop;

namespace OrtamSesi.Audio;

/// <summary>
/// Ducking decision (pure logic, tested directly by the self-test).
///  - Start: other sound must add up to AttackSeconds (short notification sounds don't trigger it).
///    While there is no sound, the accumulated time decays at half speed: intermittent but ongoing sound (speech) still triggers.
///  - Hysteresis: DetectStart threshold to start, a lower DetectHold threshold to stay ducked.
///  - Release: ReleaseSeconds after the last sound; LongReleaseSeconds after long playback (a film).
/// </summary>
internal sealed class DuckDetector
{
    public double StartThreshold = AudioMath.DbToLin(-60);
    public double HoldThreshold = AudioMath.DbToLin(-70);
    public double AttackSeconds = 1.2;
    public double ReleaseSeconds = 6;
    public double LongReleaseSeconds = 12;
    public double LongPlaySeconds = 60;

    public bool Ducked { get; private set; }
    private double _heard;
    private double _sinceHeard = double.PositiveInfinity;
    private double _duckedFor;

    public void Configure(AdvancedSettings a)
    {
        StartThreshold = AudioMath.DbToLin(a.DetectStartDb);
        HoldThreshold = AudioMath.DbToLin(a.DetectHoldDb);
        AttackSeconds = a.AttackSeconds;
        ReleaseSeconds = a.ReleaseSeconds;
        LongReleaseSeconds = a.LongReleaseSeconds;
        LongPlaySeconds = a.LongPlaySeconds;
    }

    public void Reset() { Ducked = false; _heard = 0; _sinceHeard = double.PositiveInfinity; _duckedFor = 0; }

    public bool Update(double dt, double peak)
    {
        double threshold = Ducked || _heard > 0 ? HoldThreshold : StartThreshold;
        if (peak >= threshold)
        {
            _heard = Math.Min(_heard + dt, AttackSeconds + 1);
            _sinceHeard = 0;
        }
        else
        {
            _heard = Math.Max(0, _heard - dt * 0.5);
            _sinceHeard += dt;
        }

        if (!Ducked)
        {
            if (_heard >= AttackSeconds) { Ducked = true; _duckedFor = 0; }
        }
        else
        {
            _duckedFor += dt;
            double playedFor = _duckedFor - Math.Min(_sinceHeard, _duckedFor);
            double release = playedFor >= LongPlaySeconds ? LongReleaseSeconds : ReleaseSeconds;
            if (_sinceHeard >= release) { Ducked = false; _heard = 0; }
        }
        return Ducked;
    }
}

internal static class AudioMath
{
    public static double DbToLin(double db) => Math.Pow(10, db / 20);
    public static double LinToDb(double lin) => lin > 1e-10 ? 20 * Math.Log10(lin) : -200;
}

/// <summary>A single session measurement at one instant.</summary>
internal readonly record struct SessionReading(uint Pid, string App, float Peak, bool Muted, float Volume, bool System, bool Self);

/// <summary>
/// Watches the REAL audio output of other apps. Windows keeps a separate "audio session" for each app,
/// and each session has its own peak meter (the green bars in the Volume mixer).
/// This meter shows each app's sound going into the mix separately, so:
///  - Our own sound (our own process id) is never counted → no self-ducking loop.
///  - Browser open but the tab is silent/paused/muted → meter 0 → no trigger.
///  - App muted or set to volume 0 in the mixer → ignored.
/// All active output devices are watched (detected even if a video plays on another device).
/// </summary>
internal sealed class ActivityMonitor
{
    private readonly AppState _state;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private readonly uint _selfPid = (uint)Environment.ProcessId;
    /// Sound from another copy of the same exe (e.g. an old/test copy) doesn't count as "another app" either;
    /// otherwise the two copies would duck each other.
    private readonly string _selfName = (Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "ortamsesi").ToLowerInvariant();
    private readonly ProcessNames _names = new();
    /// List of apps we muted during a mute takeover (unmuted on the next launch if the program crashes).
    private readonly string _mutedListPath;

    public ActivityMonitor(AppState state, string dataDir)
    {
        _state = state;
        _mutedListPath = Path.Combine(dataDir, "muted-sessions.json");
        _thread = new Thread(Run) { IsBackground = true, Name = "OrtamSesi.Monitor" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start() => _thread.Start();
    public void Wake() => _wake.Set();

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
    }

    /// <summary>A cached audio session. COM wrappers are not recreated on every scan (no garbage).</summary>
    private sealed class Tracked
    {
        public object Com = null!;
        public IAudioSessionControl2 Ctl = null!;
        public IAudioMeterInformation Meter = null!;
        public ISimpleAudioVolume Volume = null!;
        public uint Pid;
        public bool System;
        public bool Self;
        public string App = "";
        /// <summary>The app's persistent session identifier (unchanged across restarts).</summary>
        public string Identifier = "";
    }

    private sealed class DeviceSessions
    {
        public IAudioSessionManager2 Manager = null!;
        public IAudioEndpointVolume? Endpoint;
        public string Name = "";
        public List<Tracked> Sessions = new();
    }

    /// The session list is refreshed at this interval (for newly opened apps); scans in between only read meters.
    private const int SessionRefreshMs = 1000;

    private void Run()
    {
        IMMDeviceEnumerator? en = null;
        var devices = new Dictionary<string, DeviceSessions>(StringComparer.OrdinalIgnoreCase);
        var detector = new DuckDetector();
        int seenVersion = -1;
        DateTime lastRefresh = DateTime.MinValue, lastDebug = DateTime.MinValue;
        long lastSessionRefresh = 0;
        long lastTick = Environment.TickCount64;
        bool wasDucked = false;
        DeviceChangeNotifier.Changed += Wake;

        try
        {
            en = CoreAudioUtil.CreateEnumerator();
            var readings = new List<SessionReading>(32);
            while (!_stop)
            {
                var s = _state.Settings;
                var a = s.Advanced;
                detector.Configure(a);
                var now = DateTime.Now;
                long tick = Environment.TickCount64;
                double dt = Math.Clamp((tick - lastTick) / 1000.0, 0, 2);
                lastTick = tick;

                if (DeviceChangeNotifier.Version != seenVersion || (now - lastRefresh).TotalSeconds >= 10)
                {
                    seenVersion = DeviceChangeNotifier.Version;
                    lastRefresh = now;
                    RefreshDevices(en, devices);
                    lastSessionRefresh = 0;
                }

                bool refreshSessions = tick - lastSessionRefresh >= SessionRefreshMs;
                if (refreshSessions) lastSessionRefresh = tick;
                readings.Clear();
                string? broken = null;
                foreach (var kv in devices)
                {
                    try
                    {
                        if (refreshSessions) RefreshSessions(kv.Value);
                        Measure(kv.Value, readings, a.IgnoreSystemSounds);
                    }
                    catch (COMException) { broken = kv.Key; }
                }
                if (broken != null)
                {
                    // The device may be gone: release it; the device list is refreshed on the next round
                    if (_takeover?.DeviceId == broken) EndTakeover(devices, "device lost", muteEndpointAfter: false);
                    Release(devices[broken]);
                    devices.Remove(broken);
                    lastRefresh = DateTime.MinValue;
                }

                if (refreshSessions && !restoredPreviousRun)
                {
                    RestoreFromPreviousRun(devices);
                    restoredPreviousRun = true;
                }
                try { HandleMute(devices, s); }
                catch (Exception ex) { Log.Info($"Could not read mute state: {ex.Message}"); }

                float best = 0;
                string? who = null;
                if (a.DuckMode != "off")
                {
                    foreach (var r in readings)
                    {
                        if (!Counts(r, s)) continue;
                        if (r.Peak > best) { best = r.Peak; who = r.App; }
                    }
                }
                else detector.Reset();

                bool ducked = a.DuckMode != "off" && detector.Update(dt, best);
                Publish(ducked, ducked ? (who ?? _state.DuckSource) : null, ref wasDucked, best);

                if (a.DebugLog && (now - lastDebug).TotalSeconds >= 1)
                {
                    lastDebug = now;
                    var sb = new StringBuilder("[debug] sessions:");
                    foreach (var r in readings)
                        sb.Append($" {r.App}#{r.Pid}={AudioMath.LinToDb(r.Peak):0}dB{(r.Self ? "(self)" : "")}{(r.System ? "(system)" : "")}{(r.Muted ? "(muted)" : "")}{(Counts(r, s) ? "*" : "")}");
                    Log.Info(sb.ToString());
                }

                // If the ambient sound is paused, check less often
                _wake.WaitOne(s.Playing || _takeover != null ? a.PollMs : Math.Max(a.PollMs, 500));
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Monitor thread unexpected error: {ex}");
        }
        finally
        {
            // On close: unmute the apps we muted and leave the PC muted, as the user left it
            try { if (_takeover != null) EndTakeover(devices, "app closing", muteEndpointAfter: true); } catch { }
            DeviceChangeNotifier.Changed -= Wake;
            foreach (var d in devices.Values) Release(d);
            CoreAudioUtil.Release(en);
        }
    }

    private static void Release(DeviceSessions d)
    {
        foreach (var t in d.Sessions) CoreAudioUtil.Release(t.Com);
        d.Sessions.Clear();
        CoreAudioUtil.Release(d.Endpoint);
        d.Endpoint = null;
        CoreAudioUtil.Release(d.Manager);
    }

    // ------------------------------------------------------------------------------------------
    // When the PC is muted: on Bluetooth speakers such as Xiaomi, Windows "mute" is applied in hardware
    // (the speaker itself goes silent and turns off after a while). No app can get past this.
    // So when the program sees the mute, it first mutes every other app one by one, then
    // unmutes the device. The ambient sound keeps playing and the speaker stays on.
    // Pressing the mute key again or changing the volume puts everything back as it was.
    // ------------------------------------------------------------------------------------------

    private sealed class TakeoverState
    {
        public string DeviceId = "";
        public float Volume;
        public readonly List<Tracked> MutedByUs = new();
        public readonly HashSet<Tracked> Seen = new();
    }

    private TakeoverState? _takeover;
    private bool restoredPreviousRun;

    private string? TargetDeviceId(Dictionary<string, DeviceSessions> devices, Settings s)
    {
        if (s.DeviceId != null)
        {
            if (devices.ContainsKey(s.DeviceId)) return s.DeviceId;
            foreach (var kv in devices)
                if (string.Equals(kv.Value.Name, s.DeviceName, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }
        return _state.DefaultDeviceId is string d && devices.ContainsKey(d) ? d : null;
    }

    private void HandleMute(Dictionary<string, DeviceSessions> devices, Settings s)
    {
        string? id = TargetDeviceId(devices, s);
        if (_takeover != null && (id == null || _takeover.DeviceId != id))
        {
            EndTakeover(devices, "speaker changed", muteEndpointAfter: false);
            return;
        }
        if (id == null || devices[id].Endpoint is not IAudioEndpointVolume ep) return;
        ep.GetMute(out bool muted);
        ep.GetMasterVolumeLevelScalar(out float vol);
        var ctx = AudioConst.OurEventContext;

        if (!s.KeepPlayingWhenMuted)
        {
            if (_takeover != null) EndTakeover(devices, "setting turned off", muteEndpointAfter: true);
            return;
        }

        if (muted)
        {
            if (_takeover == null)
            {
                BeginTakeover(id, devices[id], vol);
            }
            else
            {
                // Mute key pressed again during the takeover = "unmute"
                EndTakeover(devices, "user unmuted", muteEndpointAfter: false);
            }
            ep.SetMute(false, ref ctx);
            return;
        }

        if (_takeover == null) return;
        if (Math.Abs(vol - _takeover.Volume) > 0.004f)
        {
            // In Windows too, changing the volume unmutes: same behavior
            EndTakeover(devices, "user changed the volume", muteEndpointAfter: false);
            return;
        }
        // Apps opened during the takeover start muted too
        bool changed = false;
        foreach (var t in devices[id].Sessions)
        {
            if (t.Self || !_takeover.Seen.Add(t)) continue;
            if (MuteOne(t)) { _takeover.MutedByUs.Add(t); changed = true; }
        }
        if (changed) SaveMutedList();
    }

    private void BeginTakeover(string id, DeviceSessions d, float volume)
    {
        var tk = new TakeoverState { DeviceId = id, Volume = volume };
        foreach (var t in d.Sessions)
        {
            if (t.Self) continue;
            tk.Seen.Add(t);
            if (MuteOne(t)) tk.MutedByUs.Add(t);
        }
        _takeover = tk;
        _state.MuteTakeover = true;
        SaveMutedList();
        Log.Info($"PC muted: {tk.MutedByUs.Count} apps muted; ambient sound and speaker stay on.");
    }

    /// <summary>Mutes the session; leaves it alone if it is already muted (so we never unmute the user's own choice).</summary>
    private static bool MuteOne(Tracked t)
    {
        try
        {
            t.Volume.GetMute(out bool already);
            if (already) return false;
            var ctx = AudioConst.OurEventContext;
            t.Volume.SetMute(true, ref ctx);
            return true;
        }
        catch (COMException) { return false; }
    }

    private void EndTakeover(Dictionary<string, DeviceSessions> devices, string reason, bool muteEndpointAfter)
    {
        var tk = _takeover;
        if (tk == null) return;
        var ctx = AudioConst.OurEventContext;
        int restored = 0;
        foreach (var t in tk.MutedByUs)
        {
            try { t.Volume.SetMute(false, ref ctx); restored++; } catch (COMException) { }
        }
        if (muteEndpointAfter && devices.TryGetValue(tk.DeviceId, out var d) && d.Endpoint != null)
        {
            try { d.Endpoint.SetMute(true, ref ctx); } catch (COMException) { }
        }
        _takeover = null;
        _state.MuteTakeover = false;
        try { File.Delete(_mutedListPath); } catch { }
        Log.Info($"Mute takeover ended ({reason}): {restored} apps unmuted.");
    }

    private void SaveMutedList()
    {
        if (_takeover == null) return;
        try
        {
            var ids = _takeover.MutedByUs.Select(t => t.Identifier).Where(x => x.Length > 0).Distinct().ToArray();
            File.WriteAllText(_mutedListPath, System.Text.Json.JsonSerializer.Serialize(ids));
        }
        catch { }
    }

    /// <summary>If the program closed during a takeover (crash, power loss), unmutes the apps it muted.</summary>
    private void RestoreFromPreviousRun(Dictionary<string, DeviceSessions> devices)
    {
        RestoreFromFile(devices, _mutedListPath);
        // Older versions saved the list as "susturulanlar.json": restore and delete that file too
        RestoreFromFile(devices, Path.Combine(Path.GetDirectoryName(_mutedListPath) ?? "", "susturulanlar.json"));
    }

    private void RestoreFromFile(Dictionary<string, DeviceSessions> devices, string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>();
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var ctx = AudioConst.OurEventContext;
            int n = 0;
            foreach (var d in devices.Values)
                foreach (var t in d.Sessions)
                    if (set.Contains(t.Identifier))
                    {
                        try { t.Volume.SetMute(false, ref ctx); n++; } catch (COMException) { }
                    }
            Log.Info($"Restored mutes left over from the previous run: {n} apps.");
        }
        catch (Exception ex) { Log.Info($"Could not read previous mute list: {ex.Message}"); }
        try { File.Delete(path); } catch { }
    }

    private bool Counts(in SessionReading r, Settings s)
    {
        if (r.Self || r.System || r.Muted || r.Volume < 0.01f) return false;
        var a = s.Advanced;
        foreach (var ig in a.IgnoreApps)
            if (string.Equals(ig, r.App, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.DuckMode == "browsers")
        {
            foreach (var b in a.BrowserApps)
                if (string.Equals(b, r.App, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        return true;
    }

    private void Publish(bool ducked, string? who, ref bool wasDucked, float peak = 0)
    {
        if (ducked != wasDucked)
        {
            Log.Info(ducked
                ? $"DUCKED: {FriendlyApp(who)} is playing sound ({AudioMath.LinToDb(peak):0} dB)"
                : "RESUMING: no other sound");
            wasDucked = ducked;
        }
        _state.DuckSource = who;
        _state.Ducked = ducked;
    }

    public static string FriendlyApp(string? exe) => exe?.ToLowerInvariant() switch
    {
        null => "Another app",
        "brave" => "Brave",
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "firefox" => "Firefox",
        "opera" or "opera_gx" => "Opera",
        "spotify" => "Spotify",
        "vlc" => "VLC",
        "discord" => "Discord",
        "telegram" => "Telegram",
        "ms-teams" or "teams" => "Teams",
        "zoom" => "Zoom",
        "steam" => "Steam",
        _ => exe.Length > 0 ? char.ToUpperInvariant(exe[0]) + exe[1..] : exe,
    };

    private void RefreshDevices(IMMDeviceEnumerator en, Dictionary<string, DeviceSessions> devices)
    {
        List<(string Id, string Name)> list;
        try { list = CoreAudioUtil.ListActiveRender(en); }
        catch (Exception ex) { Log.Info($"Could not list devices: {ex.Message}"); return; }

        foreach (var id in devices.Keys.ToList())
        {
            if (!list.Exists(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                Release(devices[id]);
                devices.Remove(id);
            }
        }
        foreach (var (id, name) in list)
        {
            if (devices.ContainsKey(id)) continue;
            IMMDevice? dev = null;
            try
            {
                en.GetDevice(id, out dev);
                var iid = AudioConst.IID_IAudioSessionManager2;
                dev.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out var o);
                IAudioEndpointVolume? ep = null;
                try { ep = CoreAudioUtil.ActivateEndpointVolume(dev); } catch { }
                devices[id] = new DeviceSessions { Manager = (IAudioSessionManager2)o, Endpoint = ep, Name = name };
            }
            catch (Exception ex) { Log.Info($"Could not open session manager: {ex.Message}"); }
            finally { CoreAudioUtil.Release(dev); }
        }

        _state.Devices = list.Select(d => new DeviceInfo(d.Id, d.Name)).ToArray();
        try
        {
            var def = CoreAudioUtil.TryGetDefaultRender(en);
            _state.DefaultDeviceId = def == null ? null : CoreAudioUtil.GetId(def);
            CoreAudioUtil.Release(def);
        }
        catch { }
    }

    /// <summary>
    /// Refreshes the device's session list: new sessions are added, and the COM objects of vanished ones are released at once.
    /// When Windows returns the same COM object for the same session, .NET hands back the same wrapper, so no new object is created at all.
    /// </summary>
    private void RefreshSessions(DeviceSessions d)
    {
        d.Manager.GetSessionEnumerator(out var sessions);
        var fresh = new List<Tracked>(d.Sessions.Count + 2);
        try
        {
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                object? o = null;
                try
                {
                    sessions.GetSession(i, out o);
                    var known = d.Sessions.Find(t => ReferenceEquals(t.Com, o));
                    if (known != null)
                    {
                        if (!fresh.Contains(known)) fresh.Add(known);
                        o = null;
                        continue;
                    }
                    var ctl = (IAudioSessionControl2)o;
                    ctl.GetProcessId(out uint pid);
                    bool system = ctl.IsSystemSoundsSession() == 0;
                    string app = system ? "System sounds" : _names.Get(pid);
                    string ident = "";
                    try { ctl.GetSessionIdentifier(out ident); } catch (COMException) { }
                    fresh.Add(new Tracked
                    {
                        Identifier = ident ?? "",
                        Com = o,
                        Ctl = ctl,
                        Meter = (IAudioMeterInformation)o,
                        Volume = (ISimpleAudioVolume)o,
                        Pid = pid,
                        System = system,
                        App = app,
                        Self = pid == _selfPid || app == _selfName,
                    });
                    o = null;
                }
                catch (COMException) { /* the session closed just then */ }
                catch (InvalidCastException) { }
                finally { CoreAudioUtil.Release(o); }
            }
        }
        finally { CoreAudioUtil.Release(sessions); }

        foreach (var old in d.Sessions)
            if (!fresh.Contains(old)) CoreAudioUtil.Release(old.Com);
        d.Sessions = fresh;
    }

    /// <summary>Reads the current peak value of the cached sessions. Does not allocate memory.</summary>
    private void Measure(DeviceSessions d, List<SessionReading> output, bool ignoreSystem)
    {
        bool debug = _state.Settings.Advanced.DebugLog;
        foreach (var t in d.Sessions)
        {
            try
            {
                t.Ctl.GetState(out int st);
                if (st != AudioConst.AudioSessionStateActive) continue;
                if (t.System && ignoreSystem && !debug) continue;
                t.Meter.GetPeakValue(out float peak);
                if (peak <= 0 && !debug) continue;
                t.Volume.GetMute(out bool muted);
                t.Volume.GetMasterVolume(out float v);
                output.Add(new SessionReading(t.Pid, t.App, peak, muted, v, t.System, t.Self));
            }
            catch (COMException ex) when (ex.HResult != AudioConst.AUDCLNT_E_DEVICE_INVALIDATED) { /* the session closed; dropped on the next refresh */ }
        }
    }
}

/// <summary>PID → exe name (no extension, lowercase). Cheap Win32 call + 60 s cache.</summary>
internal sealed class ProcessNames
{
    private readonly Dictionary<uint, (string Name, long Tick)> _cache = new();

    public string Get(uint pid)
    {
        long now = Environment.TickCount64;
        if (_cache.TryGetValue(pid, out var e) && now - e.Tick < 60_000) return e.Name;
        if (_cache.Count > 256) _cache.Clear();
        string name = Query(pid) ?? $"pid{pid}";
        _cache[pid] = (name, now);
        return name;
    }

    public static string? Query(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
            return Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant();
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
