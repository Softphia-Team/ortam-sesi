namespace OrtamSesi.Core;

internal sealed record DeviceInfo(string Id, string Name);

/// <summary>
/// State shared between threads. Each field is updated by a single writer
/// (audio thread, monitor thread or UI); the others only read it.
/// </summary>
internal sealed class AppState
{
    private volatile Settings _settings;
    public AppState(Settings settings) { _settings = settings; }

    /// <summary>Immutable snapshot; to change it, clone it and reassign.</summary>
    public Settings Settings { get => _settings; set => _settings = value; }

    // Written by the monitor thread
    public volatile bool Ducked;
    public volatile string? DuckSource;
    public volatile IReadOnlyList<DeviceInfo> Devices = Array.Empty<DeviceInfo>();
    public volatile string? DefaultDeviceId;

    /// <summary>The PC was muted; the other apps were muted instead of the speaker, and the ambient sound keeps playing.</summary>
    public volatile bool MuteTakeover;

    // Written by the audio thread
    public volatile string? SoundName;
    public volatile bool OutputOpen;
    public volatile string? OutputDeviceName;
    /// <summary>Short explanation shown to the user if the output can't be opened (null = no problem).</summary>
    public volatile string? OutputProblem;
    public volatile bool Audible;
}

internal static class Log
{
    private static readonly object Gate = new();
    public static string? FilePath { get; set; }
    private const long MaxBytes = 1_000_000;

    public static void Info(string message)
    {
        var path = FilePath;
        if (path == null) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
            }
            catch { /* the app must keep working even if the log can't be written */ }
        }
    }
}

internal sealed class AppPaths
{
    public string DataDir { get; }
    public string SettingsFile { get; }
    public string LogFile { get; }
    /// <summary>Name used to allow only one running instance; test instances use a different name.</summary>
    public string InstanceName { get; }
    public bool IsTestInstance { get; }

    public AppPaths(string? settingsOverride)
    {
        if (settingsOverride != null)
        {
            SettingsFile = Path.GetFullPath(settingsOverride);
            DataDir = Path.GetDirectoryName(SettingsFile)!;
            IsTestInstance = true;
            InstanceName = "OrtamSesi.Test." + StableHash(SettingsFile.ToLowerInvariant()).ToString("X8");
        }
        else
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrtamSesi");
            SettingsFile = Path.Combine(DataDir, "settings.json");
            InstanceName = "OrtamSesi";
        }
        LogFile = Path.Combine(DataDir, "log.txt");
        Directory.CreateDirectory(DataDir);
    }

    // string.GetHashCode varies between processes; here we need FNV-1a, which gives the same result in every process.
    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
