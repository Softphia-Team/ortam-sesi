using System.Runtime.InteropServices;
using OrtamSesi.Core;
using OrtamSesi.UI;

namespace OrtamSesi;

internal static class Program
{
    public static readonly System.Diagnostics.Stopwatch Uptime = System.Diagnostics.Stopwatch.StartNew();
    private static readonly List<string> StartupSteps = new();

    /// <summary>Times startup steps; if startup is slow, the log shows which step held it up.</summary>
    public static void Step(string name) => StartupSteps.Add($"{name}@{Uptime.ElapsedMilliseconds}ms");
    public static string StepsText => string.Join(", ", StartupSteps);

    [STAThread]
    private static int Main(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        // Command-line tools (for development, tests and install scripts)
        if (Has("--selftest")) return Cli.Run(Arg("--out"), w => Tools.SelfTest.Run(w, Arg("--sounds")));
        if (Has("--measure-sounds")) return Cli.Run(Arg("--out"), w => Tools.SelfTest.MeasureSounds(w, Arg("--sounds")));
        if (Has("--peak-scan")) return Cli.Run(Arg("--out"), w => Tools.SelfTest.PeakScan(w, Arg("--peak-scan")!));
        if (Has("--mute-state")) return Cli.Run(Arg("--out"), Tools.Diag.MuteState);
        if (Has("--set-mute")) return Cli.Run(Arg("--out"), w => Tools.Diag.SetMute(w, Arg("--set-mute") == "on"));
        if (Has("--level-profile")) return Cli.Run(Arg("--out"), w => Tools.SelfTest.LevelProfile(w, Arg("--level-profile")!));
        if (Has("--diag")) return Cli.Run(Arg("--out"), w => Tools.Diag.Run(w, double.TryParse(Arg("--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var sec) ? sec : 5, Arg("--csv"), int.TryParse(Arg("--interval"), out var iv) ? iv : 100));
        if (Has("--make-icon")) { TrayIcons.WriteIco(Arg("--make-icon") ?? "app.ico"); return 0; }

        var paths = new AppPaths(Arg("--settings"));
        if (Has("--exit"))
        {
            try { using var ev = EventWaitHandle.OpenExisting(paths.InstanceName + ".Exit"); ev.Set(); return 0; }
            catch { return 1; }
        }

        using var mutex = new Mutex(true, "Local\\" + paths.InstanceName + ".Instance", out bool first);
        if (!first)
        {
            try { using var ev = EventWaitHandle.OpenExisting(paths.InstanceName + ".Show"); ev.Set(); } catch { }
            return 0;
        }

        Step("mutex");
        Log.FilePath = paths.LogFile;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info($"Unexpected error: {e.ExceptionObject}");
        var settings = new SettingsStore(paths.SettingsFile).Load();
        if (!File.Exists(paths.SettingsFile)) new SettingsStore(paths.SettingsFile).Save(settings);
        Step("settings");

        // Audio first: playback and ducking start right away, however late the UI comes up
        using var host = new AppHost(paths, settings);
        host.Start();
        Step("audio");

        Application.ThreadException += (_, e) => Log.Info($"UI error: {e.Exception}");
        ApplicationConfiguration.Initialize();
        Step("winforms");
        Application.Run(new TrayApp(host));
        host.Shutdown();
        return 0;
    }
}

/// <summary>Output from a WinExe to the command line: to the --out file if given, otherwise to the parent console.</summary>
internal static class Cli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static int Run(string? outFile, Func<TextWriter, int> body)
    {
        TextWriter w;
        if (outFile != null) w = new StreamWriter(outFile, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        else
        {
            AttachConsole(-1);
            w = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        }
        try { return body(w); }
        catch (Exception ex) { w.WriteLine($"ERROR: {ex}"); return 2; }
        finally { w.Dispose(); }
    }
}
