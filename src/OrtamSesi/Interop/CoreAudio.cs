using System.Runtime.InteropServices;

namespace OrtamSesi.Interop;

// Minimal COM definitions for Windows Core Audio (MMDevice + WASAPI + session management).
// The vtable order must match the Windows SDK headers (mmdeviceapi.h, audioclient.h, audiopolicy.h,
// endpointvolume.h) exactly; unused trailing methods are omitted.

internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

internal static class AudioConst
{
    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const uint CLSCTX_ALL = 0x17;
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const int AudioSessionStateActive = 1;
    public const uint STGM_READ = 0;

    public const int E_NOTFOUND = unchecked((int)0x80070490);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly Guid IID_IAudioSessionControl = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
    public static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    /// Event context for recognizing volume/mute changes made by this app.
    public static readonly Guid OurEventContext = new("6F1C2B7A-0D3E-4C55-9A61-2E7B8C4D9F10");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    public static readonly PropertyKey PKEY_Device_FriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
    public PropertyKey(Guid formatId, int propertyId) { FormatId = formatId; PropertyId = propertyId; }
}

// PROPVARIANT: 24 bytes on x64, 16 bytes on x86.
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort r1, r2, r3;
    public IntPtr data;
    public IntPtr data2;
}

internal static class NativeAudio
{
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pv);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    void OpenPropertyStore(uint access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out uint state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    void GetBufferSize(out uint bufferFrames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    void GetMixFormat(out IntPtr format);
    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr eventHandle);
    void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    void GetBuffer(uint framesRequested, out IntPtr data);
    void ReleaseBuffer(uint framesWritten, uint flags);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager (unused, only for vtable order)
    void GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr sessionControl);
    void GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr audioVolume);
    // IAudioSessionManager2
    void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    void GetGroupingParam(out Guid groupingId);
    void SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    void RegisterAudioSessionNotification(IntPtr client);
    void UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    void GetPeakValue(out float peak);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid eventContext);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>The device's master volume and mute state (Windows volume slider).</summary>
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr notify);
    void UnregisterControlChangeNotify(IntPtr notify);
    void GetChannelCount(out uint count);
    void SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    void GetMasterVolumeLevel(out float levelDb);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    void GetChannelVolumeLevel(uint channel, out float levelDb);
    void GetChannelVolumeLevelScalar(uint channel, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

internal static class CoreAudioUtil
{
    public static IAudioEndpointVolume ActivateEndpointVolume(IMMDevice device)
    {
        var iid = AudioConst.IID_IAudioEndpointVolume;
        device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out var o);
        return (IAudioEndpointVolume)o;
    }

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            try { Marshal.FinalReleaseComObject(com); } catch { /* already released */ }
        }
    }

    public static string GetId(IMMDevice device)
    {
        device.GetId(out var id);
        return id;
    }

    public static string GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(AudioConst.STGM_READ, out store);
            var key = AudioConst.PKEY_Device_FriendlyName;
            store.GetValue(ref key, out var pv);
            try
            {
                return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.data) ?? "?" : "?";
            }
            finally { NativeAudio.PropVariantClear(ref pv); }
        }
        catch { return "?"; }
        finally { Release(store); }
    }

    /// <summary>Default output device; null if there is none.</summary>
    public static IMMDevice? TryGetDefaultRender(IMMDeviceEnumerator enumerator)
    {
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var dev);
            return dev;
        }
        catch (COMException ex) when (ex.HResult == AudioConst.E_NOTFOUND) { return null; }
    }

    public static List<(string Id, string Name)> ListActiveRender(IMMDeviceEnumerator enumerator)
    {
        var list = new List<(string, string)>();
        enumerator.EnumAudioEndpoints(EDataFlow.Render, AudioConst.DEVICE_STATE_ACTIVE, out var col);
        try
        {
            col.GetCount(out var n);
            for (uint i = 0; i < n; i++)
            {
                col.Item(i, out var dev);
                try { list.Add((GetId(dev), GetFriendlyName(dev))); }
                finally { Release(dev); }
            }
        }
        finally { Release(col); }
        return list;
    }
}

/// <summary>Reports device changes only by incrementing a counter; no COM calls are made inside the callback.</summary>
[ComVisible(true)]
internal sealed class DeviceChangeNotifier : IMMNotificationClient
{
    private static int _version;
    public static int Version => Volatile.Read(ref _version);
    public static event Action? Changed;

    private static void Bump()
    {
        Interlocked.Increment(ref _version);
        try { Changed?.Invoke(); } catch { }
    }

    public void OnDeviceStateChanged(string deviceId, uint newState) => Bump();
    public void OnDeviceAdded(string deviceId) => Bump();
    public void OnDeviceRemoved(string deviceId) => Bump();
    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {
        if (flow == EDataFlow.Render) Bump();
    }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
}
