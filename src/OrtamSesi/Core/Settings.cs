using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrtamSesi.Core;

/// <summary>User settings. Ones changed from the menu are at the top; rare/expert settings are under "Advanced".</summary>
internal sealed class Settings
{
    /// <summary>True unless the user chose "Pause".</summary>
    public bool Playing { get; set; } = true;
    /// <summary>Sound ID from sounds/sounds.json.</summary>
    public string Sound { get; set; } = "waves";
    /// <summary>1 = max (1%) … 5 = whisper.</summary>
    public int Volume { get; set; } = 1;
    /// <summary>Frequency (Hz) played while "Tone generator" is selected.</summary>
    public double ToneHz { get; set; } = 100;
    /// <summary>When the PC is muted, mute the other apps instead of the speaker so the ambient sound keeps playing.</summary>
    public bool KeepPlayingWhenMuted { get; set; } = true;
    /// <summary>null = Windows default output device.</summary>
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public bool WelcomeShown { get; set; }
    public AdvancedSettings Advanced { get; set; } = new();

    /// The 5 volume levels in the menu (loudest = 1%). dB relative to the "1%" level.
    public static readonly (string Name, double Db)[] VolumeSteps =
    {
        ("Max (1%)", 0), ("Quiet", -5), ("Quieter", -10), ("Very quiet", -16), ("Whisper", -24),
    };

    public void Normalize()
    {
        Volume = Math.Clamp(Volume, 1, VolumeSteps.Length);
        ToneHz = double.IsFinite(ToneHz) ? Math.Clamp(ToneHz, 20, 2000) : 100;
        if (string.IsNullOrWhiteSpace(Sound)) Sound = "waves";
        Advanced ??= new();
        Advanced.Normalize();
    }

    public Settings Clone() =>
        JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    /// <summary>
    /// Level of the ambient sound going to the speaker (A-weighted dBFS). At most -60 = 1% of the amplitude of a
    /// normally loud sound (≈ -20). The Windows/speaker volume affects it like any other sound; the ratio always stays the same.
    /// </summary>
    public double TargetLevelDb() => Advanced.MaxLevelDb + VolumeSteps[Math.Clamp(Volume, 1, VolumeSteps.Length) - 1].Db;
}

internal sealed class AdvancedSettings
{
    /// "1%" level: 1% of the amplitude of a normally loud sound (-40 dB). Normally loud music is taken as ≈ -20 dBFS (A);
    /// 1% = -60 dBFS (A). The code never goes above -40 (i.e. 10%).
    public double MaxLevelDb { get; set; } = -60;
    public const double HardMaxLevelDb = -40;

    /// all = audio from any app mutes it, browsers = only browsers, off = never mute.
    public string DuckMode { get; set; } = "all";
    /// Another app's audio starts counting as "sound present" when it is above this level (dBFS peak).
    public double DetectStartDb { get; set; } = -50;
    /// After muting, this lower threshold is used (so it doesn't come back on during quiet scenes in films).
    public double DetectHoldDb { get; set; } = -60;
    /// Other audio must last this long before muting (so notification sounds don't trigger it).
    public double AttackSeconds { get; set; } = 2.5;
    public double ReleaseSeconds { get; set; } = 5;
    public double LongReleaseSeconds { get; set; } = 10;
    public double LongPlaySeconds { get; set; } = 60;
    public double FadeOutSeconds { get; set; } = 1.5;
    public double FadeInSeconds { get; set; } = 6;
    /// While silent, an inaudibly quiet floor sound keeps a Bluetooth speaker from turning itself off.
    public bool KeepSpeakerAwake { get; set; } = true;
    public bool IgnoreSystemSounds { get; set; } = true;
    public string[] IgnoreApps { get; set; } = Array.Empty<string>();
    public string[] BrowserApps { get; set; } =
    {
        "brave", "chrome", "msedge", "firefox", "opera", "opera_gx", "vivaldi", "browser", "arc", "zen", "librewolf", "waterfox", "chromium",
    };
    public int PollMs { get; set; } = 150;
    public bool DebugLog { get; set; }

    public void Normalize()
    {
        MaxLevelDb = Math.Clamp(MaxLevelDb, -90, HardMaxLevelDb);
        if (DuckMode is not ("all" or "browsers" or "off")) DuckMode = "all";
        DetectStartDb = Math.Clamp(DetectStartDb, -90, -20);
        DetectHoldDb = Math.Clamp(DetectHoldDb, -95, DetectStartDb);
        AttackSeconds = Math.Clamp(AttackSeconds, 0.2, 10);
        ReleaseSeconds = Math.Clamp(ReleaseSeconds, 1, 120);
        LongReleaseSeconds = Math.Clamp(LongReleaseSeconds, ReleaseSeconds, 300);
        LongPlaySeconds = Math.Clamp(LongPlaySeconds, 5, 3600);
        FadeOutSeconds = Math.Clamp(FadeOutSeconds, 0.2, 10);
        FadeInSeconds = Math.Clamp(FadeInSeconds, 0.5, 60);
        IgnoreApps ??= Array.Empty<string>();
        BrowserApps ??= Array.Empty<string>();
        PollMs = Math.Clamp(PollMs, 5, 2000);
    }
}

internal sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }
    private readonly object _gate = new();

    public SettingsStore(string filePath) { FilePath = filePath; }

    public Settings Load()
    {
        lock (_gate)
        {
            Settings? s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
            }
            catch (Exception ex)
            {
                Log.Info($"Could not read settings file, using defaults: {ex.Message}");
                try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch { }
                s = null;
            }
            s ??= new Settings();
            s.Normalize();
            return s;
        }
    }

    public void Save(Settings s)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { Log.Info($"Could not save settings: {ex.Message}"); }
        }
    }
}
