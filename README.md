# Ortam Sesi

A calm background sound for Windows. When no other app is making sound, it plays a very quiet (at most **1%**)
ambient sound: ocean waves, rain, forest, fireplace… When a video, music or a game starts, it goes quiet by
itself and fades back in when that stops. Made to be left on while you sleep.

*Ortam sesi* is Turkish for "ambient sound".

## Features

- **20 sounds:** ocean waves, rain, rain on a tent, storm, stream, waterfall, underwater, birds, pine forest,
  wind, summer night (crickets), fireplace, airplane cabin, train, boat, café, city, and white / pink / brown
  noise. Plus a **tone generator** with selectable frequency.
- **At most 1%:** the ambient sound plays at 1% of a normal loud sound (music, video). When you turn the speaker
  up or down it changes along with everything else, but the ratio stays the same. The app never raises its own
  level.
- **Smart ducking:** it measures each app's real audio output. A browser that is open but silent, a paused
  video or a muted tab does not count. Short notification sounds (WhatsApp, Telegram…) do not interrupt it.
  Fades are smooth.
- **Keeps playing when the PC is muted:** many Bluetooth speakers switch themselves off when Windows mutes
  them. Ortam Sesi notices the mute, keeps the speaker on and mutes the other apps instead. Press mute again
  and everything goes back to normal.
- **Made for sleep:** sudden loud moments (crashing waves, drips, crackles) are smoothed out. Recordings loop
  without clicks or gaps.
- **Recovers by itself:** if the Bluetooth speaker disconnects, it waits and continues when the speaker is
  back. It also keeps working after sleep and when the output device changes.
- **Light:** ~0.1% CPU, ~30 MB RAM. No admin rights needed.

## Install

1. Download the zip of the latest version from [Releases](../../releases) and extract it.
2. Double-click **`Install.cmd`**.

That's it. The app runs as a **moon icon** next to the clock and starts automatically with Windows.
The only requirement is the free [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0);
if it is missing, the installer opens the download page.

## Use

Click the moon icon:

| Menu | What it does |
|---|---|
| **Pause / Resume** | Stops or starts the ambient sound |
| **Sound** | Picks one of the 20 sounds (and the frequency of the tone generator) |
| **Level** | Max (1%) · Quiet · Quieter · Very quiet · Whisper |
| **Settings** | Keep playing when the PC is muted · Start with Windows · Speaker |
| **Exit** | Fades the sound out and closes the app |

**Uninstall:** double-click `Uninstall.cmd`, or use Windows **Settings → Apps → Ortam Sesi → Uninstall**.
The program, its settings and the autostart entry are removed completely.

## How it works

- **Ducking:** Windows keeps a separate audio session with its own peak meter for every app (the green bars in
  the Volume Mixer). The app reads these every 150 ms. If another app stays above -50 dB for ~2.5 s, the ambient
  sound fades out in 1.5 s. It comes back 5 s after the other sound stops (10 s after long listening) and fades
  in over 6 s. The app's own sound is never counted as "another sound".
- **1% level:** all sounds are matched to the same perceived loudness, and the app fixes its own session volume
  so that what goes to the speaker is -60 dBFS (A-weighted). A normal loud sound is about -20 dBFS, so this is 1%
  of its amplitude. The Windows volume is not used for this: over Bluetooth, Windows does not attenuate the
  signal but tells the speaker which level to use. The speaker sets the volume, and the ambient sound always
  stays at 1% of everything else.
- **Sounds:** the OGG recordings are streamed ([NVorbis](https://github.com/NVorbis/NVorbis)). The loop point is
  hidden with an equal-power crossfade of 0.5–3 s, and the sample rate is converted when needed (windowed sinc).
  A look-ahead transient smoother keeps sudden events from waking you up.
- **Mute takeover:** on Bluetooth speakers the Windows mute is applied in hardware, so no app can play through
  it. When the app sees the device being muted, it mutes the other app sessions first and then unmutes the
  device. If the app is closed unexpectedly, it unmutes those apps the next time it starts.

## Build from source

Requires the .NET 8 SDK (Windows).

```powershell
dotnet publish src/OrtamSesi -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
.\Install.cmd
```

### Tests

| Command | What it checks |
|---|---|
| `OrtamSesi.exe --selftest` | No audio device needed. Plays one full loop of every sound (level, bursts, dropouts, clicks at the loop point), the 1% math, fades, 10 ducking scenarios and CPU load |
| `tests\real-test.ps1` | With the real speaker: audio in Chrome, a muted tab, a short notification, another app (very quiet test sounds) |
| `tests\mute-test.ps1` | With the real speaker: the mute takeover and going back to normal |
| `tests\resource-test.ps1` | Memory/handle leaks and CPU load |
| `tests\device-test.ps1` | Switching speakers while running, a disconnected speaker, pausing |
| `OrtamSesi.exe --diag --seconds 10` | Lists the devices and the live audio level of every app |

### Adding a sound

1. Put the Ogg Vorbis file into `sounds/` (its license must allow free redistribution: CC0, public domain or CC BY).
2. Add a line to `sounds/sounds.json`. If the recording fades in or out at the start or end, skip that part with
   `loopStart` / `loopEnd` (seconds).
3. Run `OrtamSesi.exe --measure-sounds --sounds sounds`; it measures the loudness and writes `gainDb`.
4. Add the author, license and source to `sounds/CREDITS.md`, then run `--selftest`.

## License

- **Code:** [MIT](LICENSE).
- **Sounds:** every recording keeps its own license (CC0, public domain, CC BY). Authors and sources are listed in
  [sounds/CREDITS.md](sounds/CREDITS.md). Some recordings are the loop-ready versions made by the
  [Blanket](https://github.com/rafaelmardojai/blanket) project; the others come from artists on
  [Freesound](https://freesound.org) and [SoundBible](https://soundbible.com).
