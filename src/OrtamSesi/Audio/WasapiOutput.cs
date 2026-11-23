using System.Runtime.InteropServices;
using OrtamSesi.Interop;

namespace OrtamSesi.Audio;

/// <summary>
/// Shared-mode WASAPI output. Uses the device's own mix format (no conversion),
/// 300 ms buffer + timer-driven filling: ~25 wake-ups per second, low CPU.
/// </summary>
internal sealed unsafe class WasapiOutput : IDisposable
{
    private enum SampleKind { Float32, Int16, Int24, Int32 }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public string FormatText { get; }

    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private ISimpleAudioVolume? _sessionVolume;
    private float _lastSessionVolume = -1;
    private readonly uint _bufferFrames;
    private readonly SampleKind _kind;
    private readonly int _blockAlign;
    private float[] _l = new float[4096], _r = new float[4096];

    private const long BufferHns = 3_000_000; // 300 ms

    private WasapiOutput(IMMDevice device, string id, string name)
    {
        _device = device;
        DeviceId = id;
        DeviceName = name;
        var iid = AudioConst.IID_IAudioClient;
        device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out var o);
        _client = (IAudioClient)o;

        _client.GetMixFormat(out var pFmt);
        try
        {
            ushort tag = (ushort)Marshal.ReadInt16(pFmt, 0);
            Channels = Marshal.ReadInt16(pFmt, 2);
            SampleRate = Marshal.ReadInt32(pFmt, 4);
            _blockAlign = Marshal.ReadInt16(pFmt, 12);
            int bits = Marshal.ReadInt16(pFmt, 14);
            bool isFloat = tag == 3, isPcm = tag == 1;
            if (tag == 0xFFFE && Marshal.ReadInt16(pFmt, 16) >= 22)
            {
                var sub = new Guid(new ReadOnlySpan<byte>((void*)(pFmt + 24), 16));
                isFloat = sub == AudioConst.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                isPcm = sub == AudioConst.KSDATAFORMAT_SUBTYPE_PCM;
            }
            _kind = (isFloat, isPcm, bits) switch
            {
                (true, _, 32) => SampleKind.Float32,
                (_, true, 16) => SampleKind.Int16,
                (_, true, 24) => SampleKind.Int24,
                (_, true, 32) => SampleKind.Int32,
                _ => throw new NotSupportedException($"Unsupported audio format (tag {tag}, {bits} bit)"),
            };
            FormatText = $"{SampleRate} Hz, {Channels} ch, {_kind}";
            // NOPERSIST: we adjust our session volume constantly, so Windows must not store it as an "app preference"
            _client.Initialize(AudioConst.AUDCLNT_SHAREMODE_SHARED, AudioConst.AUDCLNT_STREAMFLAGS_NOPERSIST, BufferHns, 0, pFmt, IntPtr.Zero);
        }
        finally { Marshal.FreeCoTaskMem(pFmt); }

        _client.GetBufferSize(out _bufferFrames);
        var iidR = AudioConst.IID_IAudioRenderClient;
        _client.GetService(ref iidR, out var r);
        _render = (IAudioRenderClient)r;

        // Show a readable name in the volume mixer
        try
        {
            var iidS = AudioConst.IID_IAudioSessionControl;
            _client.GetService(ref iidS, out var s);
            var ctx = Guid.Empty;
            ((IAudioSessionControl)s).SetDisplayName("Ortam Sesi", ref ctx);
            CoreAudioUtil.Release(s);
        }
        catch { }

        var iidV = AudioConst.IID_ISimpleAudioVolume;
        _client.GetService(ref iidV, out var sv);
        _sessionVolume = (ISimpleAudioVolume)sv;
        SetSessionVolume(0); // silent until the first setting arrives

        // Fill the buffer with silence and start
        _render.GetBuffer(_bufferFrames, out _);
        _render.ReleaseBuffer(_bufferFrames, AudioConst.AUDCLNT_BUFFERFLAGS_SILENT);
        _client.Start();
    }

    public static WasapiOutput Open(IMMDevice device)
    {
        string id = CoreAudioUtil.GetId(device);
        string name = CoreAudioUtil.GetFriendlyName(device);
        try { return new WasapiOutput(device, id, name); }
        catch { CoreAudioUtil.Release(device); throw; }
    }

    /// <summary>
    /// This app's session volume (0..1). Applied in the mixer: takes effect within a few milliseconds,
    /// without waiting for the buffered audio. Sent to Windows only when it changes.
    /// </summary>
    public void SetSessionVolume(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        if (_sessionVolume == null || Math.Abs(v - _lastSessionVolume) <= Math.Max(1e-6f, _lastSessionVolume * 0.01f)) return;
        var ctx = AudioConst.OurEventContext;
        _sessionVolume.SetMasterVolume(v, ref ctx);
        _lastSessionVolume = v;
    }

    /// <summary>Duration of buffered audio waiting to be played (s).</summary>
    public double QueuedSeconds
    {
        get
        {
            _client!.GetCurrentPadding(out var pad);
            return (double)pad / SampleRate;
        }
    }

    /// <summary>Fills the empty part of the buffer. Throws COMException if the device disappears.</summary>
    public int Pump(AmbientRenderer renderer, in RenderInputs inputs)
    {
        _client!.GetCurrentPadding(out var pad);
        int avail = (int)(_bufferFrames - pad);
        if (avail < SampleRate / 200) return 0; // < 5 ms: wait
        if (_l.Length < avail) { _l = new float[avail]; _r = new float[avail]; }
        var l = _l.AsSpan(0, avail);
        var r = _r.AsSpan(0, avail);
        renderer.Render(l, r, inputs);

        _render!.GetBuffer((uint)avail, out var p);
        bool silent = renderer.LastRenderSilent;
        if (!silent) Write((byte*)p, l, r, avail);
        _render.ReleaseBuffer((uint)avail, silent ? AudioConst.AUDCLNT_BUFFERFLAGS_SILENT : 0);
        return avail;
    }

    private void Write(byte* dst, Span<float> l, Span<float> r, int frames)
    {
        int ch = Channels;
        for (int i = 0; i < frames; i++)
        {
            byte* frame = dst + (long)i * _blockAlign;
            float a = l[i], b = r[i];
            for (int c = 0; c < ch; c++)
            {
                float v = ch == 1 ? (a + b) * 0.5f : c == 0 ? a : c == 1 ? b : 0f;
                switch (_kind)
                {
                    case SampleKind.Float32:
                        ((float*)frame)[c] = v;
                        break;
                    case SampleKind.Int16:
                        ((short*)frame)[c] = (short)Math.Clamp(v * 32767f, -32768f, 32767f);
                        break;
                    case SampleKind.Int32:
                        ((int*)frame)[c] = (int)Math.Clamp(v * 2147483647.0, -2147483648.0, 2147483647.0);
                        break;
                    case SampleKind.Int24:
                        int s = (int)Math.Clamp(v * 8388607f, -8388608f, 8388607f);
                        byte* q = frame + c * 3;
                        q[0] = (byte)s; q[1] = (byte)(s >> 8); q[2] = (byte)(s >> 16);
                        break;
                }
            }
        }
    }

    public void Dispose()
    {
        try { _client?.Stop(); } catch { }
        CoreAudioUtil.Release(_sessionVolume);
        _sessionVolume = null;
        CoreAudioUtil.Release(_render);
        CoreAudioUtil.Release(_client);
        CoreAudioUtil.Release(_device);
        _render = null; _client = null; _device = null;
    }
}
