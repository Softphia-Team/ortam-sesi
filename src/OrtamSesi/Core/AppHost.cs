using OrtamSesi.Audio;

namespace OrtamSesi.Core;

/// <summary>
/// The app's UI-independent core: audio engine, monitor and inter-process signals.
/// Started BEFORE the UI; even if the tray icon (Explorer / GDI+) is delayed or hangs,
/// audio keeps playing, muting keeps working and the "exit" signal (uninstaller) is still handled.
/// </summary>
internal sealed class AppHost : IDisposable
{
    public AppPaths Paths { get; }
    public AppState State { get; }
    public SettingsStore Store { get; }
    public AudioEngine Engine { get; }
    public ActivityMonitor Monitor { get; }
    public SoundLibrary Library { get; }

    private readonly EventWaitHandle _showSignal, _exitSignal;
    private readonly RegisteredWaitHandle _showWait, _exitWait;
    private int _shutdown;

    /// <summary>Wired up once the UI is ready.</summary>
    public Action? OnShowRequested { get; set; }
    public Action? OnExitRequested { get; set; }

    public AppHost(AppPaths paths, Settings settings)
    {
        Paths = paths;
        Store = new SettingsStore(paths.SettingsFile);
        State = new AppState(settings);
        Library = SoundLibrary.Load();
        Log.Info($"Sound list: {Library.Sounds.Count} sounds ({Library.Directory})");
        Engine = new AudioEngine(State, Library);
        Monitor = new ActivityMonitor(State, paths.DataDir);

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, paths.InstanceName + ".Show");
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, paths.InstanceName + ".Exit");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => OnShowRequested?.Invoke(), null, -1, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) =>
        {
            var ui = OnExitRequested;
            if (ui != null) ui();
            else
            {
                // If the UI isn't up yet, shut down the core and end the process
                Log.Info("Exit requested (UI not ready).");
                Shutdown();
                Environment.Exit(0);
            }
        }, null, -1, false);
    }

    public void Start()
    {
        Engine.Start();
        Monitor.Start();
    }

    /// <summary>Fades the sound out gently and stops the threads (safe to call more than once).</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        Engine.Shutdown(TimeSpan.FromSeconds(2.5));
        Monitor.Stop();
    }

    public void Dispose()
    {
        _showWait.Unregister(null);
        _exitWait.Unregister(null);
        _showSignal.Dispose();
        _exitSignal.Dispose();
    }
}
