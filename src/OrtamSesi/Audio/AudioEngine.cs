using System.Runtime.InteropServices;
using OrtamSesi.Core;
using OrtamSesi.Interop;

namespace OrtamSesi.Audio;

/// <summary>
/// Audio thread: picks/opens the output device, waits and reopens it if it disappears,
/// generates the ambient sound and writes it to the buffer. No error brings the app down; at worst
/// it switches to the "Waiting for speaker" state and retries every few seconds.
/// </summary>
internal sealed class AudioEngine
{
    private readonly AppState _state;
    private readonly SoundLibrary _library;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private volatile bool _stop, _exiting, _reopen;
    private volatile string? _reopenReason;

    public AudioEngine(AppState state, SoundLibrary library)
    {
        _state = state;
        _library = library;
        _thread = new Thread(Run) { IsBackground = true, Name = "OrtamSesi.Audio", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        // Measure the noise levels in advance (so the audio thread doesn't wait on first start)
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var k in new[] { "white", "pink", "brown" }) foreach (int fs in new[] { 44100, 48000 }) new NoiseSource(k, fs).Dispose();
        });
        _thread.Start();
    }

    /// <summary>
    /// Session volume: a fixed multiplier that brings the normalized sound (≈ -32 dBFS) down to the target level (at most
    /// -60 dBFS = 1% of a normal loud sound). The Windows volume is ignored: over Bluetooth Windows doesn't attenuate the
    /// signal, it sends a command to the speaker; the speaker can also be adjusted with its own buttons. So the program
    /// never raises its own volume under any condition — the ambient sound is always 1% of other sounds, scaled by however loud the speaker is set.
    /// </summary>
    public static float SessionVolumeFor(double targetLevelDb) =>
        (float)Math.Min(1.0, Math.Pow(10, (targetLevelDb - Loudness.ReferenceDb) / 20));

    public void Wake() => _wake.Set();

    public void RequestReopen(string reason)
    {
        _reopenReason = reason;
        _reopen = true;
        _wake.Set();
    }

    /// <summary>Fades the sound out smoothly and stops the thread.</summary>
    public void Shutdown(TimeSpan timeout)
    {
        _exiting = true;
        _wake.Set();
        if (!_stopped.Wait(timeout))
        {
            _stop = true;
            _wake.Set();
            _stopped.Wait(1000);
        }
    }

    private void Run()
    {
        IMMDeviceEnumerator? en = null;
        var notifier = new DeviceChangeNotifier();
        bool registered = false;
        WasapiOutput? output = null;
        AmbientRenderer? renderer = null;
        int seenVersion = -1, failVersion = -1;
        DateTime nextRetry = DateTime.MinValue, lastCheck = DateTime.MinValue;
        DateTime? silentSince = null;
        double backoff = 1;
        string? lastError = null;
        string? lastDeviceSetting = null;
        string? failDeviceSetting = null;

        void Close(string reason)
        {
            if (output == null) return;
            Log.Info($"Output closed ({reason}): {output.DeviceName}");
            output.Dispose();
            output = null;
            _state.OutputOpen = false;
            _state.Audible = false;
        }

        try
        {
            en = CoreAudioUtil.CreateEnumerator();
            try { en.RegisterEndpointNotificationCallback(notifier); registered = true; }
            catch (Exception ex) { Log.Info($"Cannot receive device notifications (will check every 5 s): {ex.Message}"); }
            DeviceChangeNotifier.Changed += Wake;

            while (!_stop)
            {
                var s = _state.Settings;
                var now = DateTime.Now;
                bool want = !_exiting && s.Playing && !_state.Ducked;
                bool keepAlive = !_exiting && s.Advanced.KeepSpeakerAwake;
                var inputs = new RenderInputs
                {
                    WantAmbient = want,
                    SoundId = s.Sound,
                    ToneHz = s.ToneHz,
                    KeepAlive = keepAlive,
                    FadeInSeconds = s.Advanced.FadeInSeconds,
                    FadeOutSeconds = _exiting ? 0.8 : s.Advanced.FadeOutSeconds,
                };
                bool needStream = want || keepAlive || (renderer != null && renderer.FadePosition > 0);

                // Shutdown: once the sound is fully faded out, wait for the buffer to drain and exit
                if (_exiting && (output == null || renderer == null || (renderer.FadePosition == 0 && renderer.CurrentGain == 0)))
                {
                    if (output != null)
                    {
                        var until = DateTime.Now.AddMilliseconds(500);
                        try { while (output.QueuedSeconds > 0.01 && DateTime.Now < until) Thread.Sleep(20); } catch { }
                    }
                    break;
                }

                // Is the open output still the right device?
                if (output != null)
                {
                    int v = DeviceChangeNotifier.Version;
                    bool deviceSettingChanged = !string.Equals(lastDeviceSetting, s.DeviceId, StringComparison.OrdinalIgnoreCase);
                    if (_reopen)
                    {
                        _reopen = false;
                        Close(_reopenReason ?? "restart");
                    }
                    else if (v != seenVersion || deviceSettingChanged || (now - lastCheck).TotalSeconds >= 5)
                    {
                        seenVersion = v;
                        lastDeviceSetting = s.DeviceId;
                        lastCheck = now;
                        string? desired = DesiredDeviceId(en, s);
                        if (!string.Equals(desired, output.DeviceId, StringComparison.OrdinalIgnoreCase))
                            Close(desired == null ? "device lost" : "output device changed");
                    }
                }

                if (output == null)
                {
                    _reopen = false;
                    if (!needStream)
                    {
                        _state.OutputProblem = null;
                        _wake.WaitOne(500);
                        continue;
                    }
                    // Wait if the retry delay hasn't passed — but retry right away if the devices changed or the user picked another device
                    if (now < nextRetry && DeviceChangeNotifier.Version == failVersion
                        && string.Equals(failDeviceSetting, s.DeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        _wake.WaitOne(250);
                        continue;
                    }
                    try
                    {
                        var dev = OpenDesiredDevice(en, s) ?? throw new DeviceMissingException(s.DeviceId != null
                            ? $"Waiting for speaker: {s.DeviceName ?? "?"}"
                            : "Waiting for speaker");
                        output = WasapiOutput.Open(dev);
                        if (renderer == null || renderer.SampleRate != output.SampleRate)
                        {
                            renderer?.Dispose();
                            renderer = new AmbientRenderer(output.SampleRate, _library, s.Sound);
                        }
                        output.SetSessionVolume(SessionVolumeFor(s.TargetLevelDb()));
                        renderer.ResetFade(); // fade in slowly from silence on every open
                        seenVersion = DeviceChangeNotifier.Version;
                        lastDeviceSetting = s.DeviceId;
                        lastCheck = now;
                        backoff = 1;
                        lastError = null;
                        silentSince = null;
                        _state.OutputOpen = true;
                        _state.OutputDeviceName = output.DeviceName;
                        _state.OutputProblem = null;
                        Log.Info($"Output opened: {output.DeviceName} ({output.FormatText})");
                    }
                    catch (Exception ex)
                    {
                        output = null;
                        string msg = ex is DeviceMissingException ? ex.Message : $"Could not open speaker ({Describe(ex)})";
                        if (msg != lastError) { Log.Info(msg); lastError = msg; }
                        _state.OutputProblem = ex is DeviceMissingException ? ex.Message : "Can't reach the speaker, retrying";
                        _state.OutputOpen = false;
                        failVersion = DeviceChangeNotifier.Version;
                        failDeviceSetting = s.DeviceId;
                        nextRetry = now.AddSeconds(backoff);
                        backoff = Math.Min(backoff * 2, 15);
                    }
                    continue;
                }

                try
                {
                    // Volume first (applies instantly if changed from the menu), then new samples
                    output.SetSessionVolume(SessionVolumeFor(s.TargetLevelDb()));
                    output.Pump(renderer!, inputs);
                    _state.Audible = renderer!.CurrentGain > 0;
                    _state.SoundName = _library.Get(renderer.SoundId).Name;
                }
                catch (Exception ex)
                {
                    Log.Info($"Playback error: {Describe(ex)}");
                    Close("error");
                    failVersion = DeviceChangeNotifier.Version;
                    failDeviceSetting = s.DeviceId;
                    nextRetry = now.AddSeconds(1);
                    continue;
                }

                // If nothing is going to play (paused + keep-awake off), close the stream
                // so the speaker and the PC can go to sleep normally.
                if (!needStream && renderer!.CurrentGain == 0)
                {
                    silentSince ??= now;
                    if ((now - silentSince.Value).TotalSeconds > 10) Close("no sound, stream closed");
                }
                else silentSince = null;

                _wake.WaitOne(30);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Audio thread unexpected error: {ex}");
        }
        finally
        {
            renderer?.Dispose();
            DeviceChangeNotifier.Changed -= Wake;
            try { output?.Dispose(); } catch { }
            if (registered) { try { en!.UnregisterEndpointNotificationCallback(notifier); } catch { } }
            CoreAudioUtil.Release(en);
            _state.OutputOpen = false;
            _stopped.Set();
        }
    }

    private static string? DesiredDeviceId(IMMDeviceEnumerator en, Settings s)
    {
        var dev = OpenDesiredDevice(en, s);
        if (dev == null) return null;
        try { return CoreAudioUtil.GetId(dev); }
        finally { CoreAudioUtil.Release(dev); }
    }

    private static IMMDevice? OpenDesiredDevice(IMMDeviceEnumerator en, Settings s)
    {
        if (s.DeviceId == null) return CoreAudioUtil.TryGetDefaultRender(en);
        try
        {
            en.GetDevice(s.DeviceId, out var dev);
            dev.GetState(out var st);
            if (st == AudioConst.DEVICE_STATE_ACTIVE) return dev;
            CoreAudioUtil.Release(dev);
        }
        catch (COMException) { }

        // If the speaker is re-paired, Windows may give it a new id: accept an active device with the same name
        if (!string.IsNullOrEmpty(s.DeviceName))
        {
            try
            {
                foreach (var (id, name) in CoreAudioUtil.ListActiveRender(en))
                {
                    if (!string.Equals(name, s.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                    en.GetDevice(id, out var dev);
                    return dev;
                }
            }
            catch (COMException) { }
        }
        return null;
    }

    private static string Describe(Exception ex) => ex is COMException c
        ? c.HResult switch
        {
            AudioConst.AUDCLNT_E_DEVICE_INVALIDATED => "device disconnected",
            AudioConst.AUDCLNT_E_SERVICE_NOT_RUNNING => "Windows audio service is not running",
            _ => $"0x{c.HResult:X8} {c.Message}",
        }
        : ex.Message;

    private sealed class DeviceMissingException(string message) : Exception(message);
}
