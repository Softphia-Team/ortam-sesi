using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using Microsoft.Win32;
using OrtamSesi.Audio;
using OrtamSesi.Core;

namespace OrtamSesi.UI;

/// <summary>
/// Tray icon and menu. One main action: "Pause / Resume" (bold, at the top).
/// Sound and level are one click away; rarely used options live under "Settings".
/// Left- or right-clicking the icon opens the same menu.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    /// Preset frequencies for the tone generator (Hz).
    private static readonly double[] ToneFrequencies = { 40, 60, 80, 100, 136.1, 174, 200, 285, 432, 528 };

    private readonly AppHost _host;
    private AppState State => _host.State;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _invoker;
    private readonly FileSystemWatcher? _watcher;
    private readonly Icon _iconPlaying, _iconQuiet, _iconPaused, _iconWarn;
    private readonly System.Windows.Forms.Timer _reloadDebounce;

    private readonly ToolStripMenuItem _status, _toggle, _sound, _tone, _volume, _settings, _keepMuted, _device, _autostart;
    private string _lastStatus = "";
    private bool _exiting;
    private DateTime _ignoreWatcherUntil;

    public TrayApp(AppHost host)
    {
        _host = host;
        var settings = State.Settings;
        _invoker = new Control();
        _invoker.CreateControl();
        _ = _invoker.Handle;
        Program.Step("window");

        int sz = SystemInformation.SmallIconSize.Width;
        Icon Make(Color c, Color? dot) { using var b = TrayIcons.Draw(sz, c, dot); return TrayIcons.ToIcon(b); }
        _iconPlaying = Make(TrayIcons.Moon, null);
        _iconQuiet = Make(TrayIcons.MoonDim, null);
        _iconPaused = Make(TrayIcons.MoonGray, null);
        _iconWarn = Make(TrayIcons.MoonGray, TrayIcons.Warn);
        Program.Step("icons");

        var bold = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold);
        _menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        _status = new ToolStripMenuItem("Ortam Sesi") { Enabled = false };
        _toggle = new ToolStripMenuItem("Pause", null, (_, _) => TogglePlaying()) { Font = bold };

        // Sound list: groups with a header row
        _sound = new ToolStripMenuItem("Sound");
        _tone = new ToolStripMenuItem("Tone frequency");
        string? group = null;
        foreach (var snd in _host.Library.Sounds)
        {
            if (snd.Group != group)
            {
                if (group != null) _sound.DropDownItems.Add(new ToolStripSeparator());
                group = snd.Group;
                _sound.DropDownItems.Add(new ToolStripMenuItem(group) { Enabled = false, Font = bold });
            }
            var id = snd.Id;
            _sound.DropDownItems.Add(new ToolStripMenuItem(snd.Name, null, (_, _) => Change(s => s.Sound = id)) { Tag = id });
            if (snd.Generator == "tone") _sound.DropDownItems.Add(_tone);
        }
        foreach (var hz in ToneFrequencies)
        {
            double f = hz;
            _tone.DropDownItems.Add(new ToolStripMenuItem($"{f.ToString("0.#", CultureInfo.InvariantCulture)} Hz", null,
                (_, _) => Change(s => { s.ToneHz = f; s.Sound = "tone"; })) { Tag = f });
        }

        _volume = new ToolStripMenuItem("Level");
        for (int i = 0; i < Settings.VolumeSteps.Length; i++)
        {
            int v = i + 1;
            _volume.DropDownItems.Add(new ToolStripMenuItem(Settings.VolumeSteps[i].Name, null, (_, _) => Change(s => s.Volume = v)) { Tag = v });
        }

        _keepMuted = new ToolStripMenuItem("Keep playing when the PC is muted", null,
            (_, _) => Change(s => s.KeepPlayingWhenMuted = !s.KeepPlayingWhenMuted));
        _device = new ToolStripMenuItem("Speaker");
        _device.DropDownOpening += (_, _) => BuildDeviceMenu();
        _device.DropDownItems.Add("…");
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());

        _settings = new ToolStripMenuItem("Settings");
        _settings.DropDownItems.AddRange(new ToolStripItem[]
        {
            _keepMuted, _autostart, _device,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open settings file", null, (_, _) => OpenFile(_host.Paths.SettingsFile)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenFile(_host.Paths.LogFile)),
            new ToolStripMenuItem("Sound credits and licenses", null, (_, _) => OpenFile(Path.Combine(_host.Library.Directory, "CREDITS.md"))),
        });

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _status, new ToolStripSeparator(), _toggle, _sound, _volume, new ToolStripSeparator(), _settings,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        });
        _menu.Opening += (_, _) => RefreshMenu();
        Program.Step("menu");

        _icon = new NotifyIcon { Icon = _iconPlaying, Text = "Ortam Sesi", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
        Program.Step("tray");

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshIcon();
        _timer.Start();
        RefreshIcon();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _host.OnShowRequested = () => Post(() =>
            _icon.ShowBalloonTip(5000, "Ortam Sesi is already running", "Click the moon icon next to the clock.", ToolTipIcon.Info));
        _host.OnExitRequested = () => Post(ExitApp);

        // Reload if the settings file is edited by hand
        _reloadDebounce = new System.Windows.Forms.Timer { Interval = 600 };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadSettingsFromDisk(); };
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_host.Paths.SettingsFile)!, Path.GetFileName(_host.Paths.SettingsFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) => Post(() => { if (DateTime.Now > _ignoreWatcherUntil) { _reloadDebounce.Stop(); _reloadDebounce.Start(); } });
            _watcher.Changed += h;
            _watcher.Created += h;
            _watcher.Renamed += (_, _) => h(this, null!);
        }
        catch { _watcher = null; }

        Program.Step("watcher");
        Log.Info($"Started ({Program.Uptime.ElapsedMilliseconds} ms). Sound: {settings.Sound}, level {settings.Volume}, keep playing when muted: {settings.KeepPlayingWhenMuted}");
        if (Program.Uptime.ElapsedMilliseconds > 3000) Log.Info($"Slow startup steps: {Program.StepsText}");

        if (!settings.WelcomeShown && !_host.Paths.IsTestInstance)
        {
            Change(s => s.WelcomeShown = true);
            _icon.ShowBalloonTip(8000, "Ortam Sesi is running",
                "It goes quiet by itself when another sound plays and comes back afterwards. Click the moon icon to pick a sound.",
                ToolTipIcon.Info);
        }
    }

    private void Post(Action a)
    {
        try { if (!_invoker.IsDisposed) _invoker.BeginInvoke(a); } catch { }
    }

    private void Change(Action<Settings> edit)
    {
        var s = State.Settings.Clone();
        edit(s);
        s.Normalize();
        State.Settings = s;
        _ignoreWatcherUntil = DateTime.Now.AddSeconds(1.5);
        _host.Store.Save(s);
        _host.Engine.Wake();
        _host.Monitor.Wake();
        RefreshIcon();
    }

    private void ReloadSettingsFromDisk()
    {
        State.Settings = _host.Store.Load();
        _host.Engine.Wake();
        _host.Monitor.Wake();
        Log.Info("Settings file reloaded.");
        RefreshIcon();
    }

    private void TogglePlaying()
    {
        bool next = !State.Settings.Playing;
        Change(s => s.Playing = next);
        Log.Info(next ? "User: resume" : "User: pause");
    }

    private void ShowMenu()
    {
        try
        {
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
        }
        catch { }
    }

    private string StatusText(out Icon icon)
    {
        var s = State.Settings;
        string name = State.SoundName ?? _host.Library.Get(s.Sound).Name;
        if (!s.Playing) { icon = _iconPaused; return "Paused"; }
        if (State.OutputProblem is string p) { icon = _iconWarn; return p; }
        if (State.MuteTakeover) { icon = _iconPlaying; return $"PC muted, only {name} is playing"; }
        if (State.Ducked) { icon = _iconQuiet; return $"Quiet: {ActivityMonitor.FriendlyApp(State.DuckSource)} is playing sound"; }
        icon = _iconPlaying;
        return $"Playing: {name}";
    }

    private void RefreshIcon()
    {
        string st = StatusText(out var icon);
        if (st == _lastStatus && _icon.Icon == icon) return;
        _lastStatus = st;
        _icon.Icon = icon;
        string text = "Ortam Sesi — " + st;
        _icon.Text = text.Length > 127 ? text[..127] : text;
        _status.Text = st;
    }

    private void RefreshMenu()
    {
        var s = State.Settings;
        RefreshIcon();
        _toggle.Text = s.Playing ? "Pause" : "Resume";
        string current = _host.Library.Get(s.Sound).Id;
        foreach (ToolStripItem i in _sound.DropDownItems)
            if (i is ToolStripMenuItem m && m.Tag is string id) m.Checked = id == current;
        foreach (ToolStripMenuItem i in _tone.DropDownItems) i.Checked = current == "tone" && Math.Abs((double)i.Tag! - s.ToneHz) < 0.05;
        _tone.Text = $"Tone frequency ({s.ToneHz.ToString("0.#", CultureInfo.InvariantCulture)} Hz)";
        foreach (ToolStripMenuItem i in _volume.DropDownItems) i.Checked = (int)i.Tag! == s.Volume;
        _keepMuted.Checked = s.KeepPlayingWhenMuted;
        _autostart.Checked = Autostart.IsEnabled();
        _autostart.Enabled = !_host.Paths.IsTestInstance;
        _device.Text = s.DeviceId == null ? "Speaker: Windows default" : $"Speaker: {s.DeviceName}";
    }

    private void BuildDeviceMenu()
    {
        var s = State.Settings;
        _device.DropDownItems.Clear();
        string? defName = State.Devices.FirstOrDefault(d => string.Equals(d.Id, State.DefaultDeviceId, StringComparison.OrdinalIgnoreCase))?.Name;
        _device.DropDownItems.Add(new ToolStripMenuItem(
            defName == null ? "Windows default" : $"Windows default (now: {defName})", null,
            (_, _) => Change(x => { x.DeviceId = null; x.DeviceName = null; }))
        { Checked = s.DeviceId == null });
        _device.DropDownItems.Add(new ToolStripSeparator());
        foreach (var d in State.Devices)
        {
            var dev = d;
            _device.DropDownItems.Add(new ToolStripMenuItem($"Only: {d.Name}", null,
                (_, _) => Change(x => { x.DeviceId = dev.Id; x.DeviceName = dev.Name; }))
            { Checked = string.Equals(s.DeviceId, d.Id, StringComparison.OrdinalIgnoreCase) });
        }
        if (s.DeviceId != null && !State.Devices.Any(d => string.Equals(d.Id, s.DeviceId, StringComparison.OrdinalIgnoreCase)))
            _device.DropDownItems.Add(new ToolStripMenuItem($"Only: {s.DeviceName} (not connected)") { Checked = true, Enabled = false });
    }

    private static void ToggleAutostart()
    {
        bool on = !Autostart.IsEnabled();
        try { Autostart.Set(on); }
        catch (Exception ex) { Log.Info($"Could not change autostart: {ex.Message}"); }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Info("PC resumed from sleep.");
            _host.Engine.RequestReopen("resume from sleep");
            _host.Monitor.Wake();
        }
        else if (e.Mode == PowerModes.Suspend) Log.Info("PC going to sleep.");
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => ExitApp();

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Closing.");
        _timer.Stop();
        _host.Shutdown();
        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            _host.OnShowRequested = null;
            _host.OnExitRequested = null;
            _watcher?.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _timer.Dispose();
            _reloadDebounce.Dispose();
            _invoker.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "OrtamSesi";

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) is string v && v.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, Command);
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
