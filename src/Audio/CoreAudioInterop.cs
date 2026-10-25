namespace Loupedeck.DeviceVolumeMixerPlugin.Audio
{
    using System;
    using System.Runtime.InteropServices;

    // Minimal, hand-written Windows Core Audio interop.
    // The method ORDER in every interface must match the native vtable exactly.
    // Methods we never call are declared with placeholder signatures so the order stays right.

    internal enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    internal enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    internal static class CoreAudio
    {
        public const Int32 DeviceStateActive = 0x1;
        public const Int32 ClsCtxAll = 0x17;
        public const Int32 StgmRead = 0x0;
        public const Int32 SessionStateExpired = 2;
        public const UInt16 VtLpwstr = 31;

        public static readonly Guid ClsidMMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid ClsidPolicyConfigClient = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

        public static readonly Guid IidAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        public static readonly Guid IidAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        // PKEY_Device_FriendlyName, e.g. "Speakers (Realtek(R) Audio)"
        public static readonly PropertyKey PKeyDeviceFriendlyName = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

        // PKEY_Device_DeviceDesc, e.g. "Speakers" (this is the name the user can change in Windows sound settings)
        public static readonly PropertyKey PKeyDeviceDeviceDesc = new PropertyKey(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);

        [DllImport("ole32.dll")]
        public static extern Int32 PropVariantClear(ref PropVariant pvar);

        public static T Create<T>(Guid clsid) where T : class
        {
            var type = Type.GetTypeFromCLSID(clsid, true);
            return (T)Activator.CreateInstance(type);
        }

        public static void Release(Object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                try
                {
                    Marshal.ReleaseComObject(comObject);
                }
                catch
                {
                    // Never let cleanup take anything down.
                }
            }
        }

        public static String ReadString(IPropertyStore store, PropertyKey key)
        {
            var pv = default(PropVariant);
            try
            {
                store.GetValue(ref key, out pv);
                return pv.VarType == VtLpwstr && pv.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pv.Pointer) : null;
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public Int32 PropertyId;

        public PropertyKey(Guid formatId, Int32 propertyId)
        {
            this.FormatId = formatId;
            this.PropertyId = propertyId;
        }
    }

    // Large enough for PROPVARIANT on both x86 (16 bytes) and x64 (24 bytes).
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)]
        public UInt16 VarType;

        [FieldOffset(8)]
        public IntPtr Pointer;

        [FieldOffset(16)]
        public IntPtr Reserved;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, Int32 stateMask, out IMMDeviceCollection devices);

        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] String id, out IMMDevice device);

        [PreserveSig]
        Int32 RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        Int32 UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        void GetCount(out Int32 count);

        void Item(Int32 index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        void Activate(ref Guid iid, Int32 clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out Object instance);

        void OpenPropertyStore(Int32 access, out IPropertyStore store);

        void GetId([MarshalAs(UnmanagedType.LPWStr)] out String id);

        void GetState(out Int32 state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        void GetCount(out Int32 count);

        void GetAt(Int32 index, out PropertyKey key);

        void GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        Int32 SetValue(IntPtr key, IntPtr value);

        [PreserveSig]
        Int32 Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig]
        Int32 RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        Int32 UnregisterControlChangeNotify(IntPtr notify);

        void GetChannelCount(out Int32 count);

        void SetMasterVolumeLevel(Single levelDb, ref Guid eventContext);

        void SetMasterVolumeLevelScalar(Single level, ref Guid eventContext);

        void GetMasterVolumeLevel(out Single levelDb);

        void GetMasterVolumeLevelScalar(out Single level);

        void SetChannelVolumeLevel(Int32 channel, Single levelDb, ref Guid eventContext);

        void SetChannelVolumeLevelScalar(Int32 channel, Single level, ref Guid eventContext);

        void GetChannelVolumeLevel(Int32 channel, out Single levelDb);

        void GetChannelVolumeLevelScalar(Int32 channel, out Single level);

        void SetMute([MarshalAs(UnmanagedType.Bool)] Boolean mute, ref Guid eventContext);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out Boolean mute);

        // Remaining methods (GetVolumeStepInfo, VolumeStepUp, ...) are not used and intentionally omitted.
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // --- IAudioSessionManager ---
        [PreserveSig]
        Int32 GetAudioSessionControl(IntPtr sessionGuid, Int32 streamFlags, out IntPtr sessionControl);

        [PreserveSig]
        Int32 GetSimpleAudioVolume(IntPtr sessionGuid, Int32 streamFlags, out IntPtr audioVolume);

        // --- IAudioSessionManager2 ---
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

        [PreserveSig]
        Int32 RegisterSessionNotification(IAudioSessionNotification notification);

        [PreserveSig]
        Int32 UnregisterSessionNotification(IAudioSessionNotification notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        void GetCount(out Int32 count);

        void GetSession(Int32 index, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl
    {
        void GetState(out Int32 state);

        [PreserveSig]
        Int32 GetDisplayName(out IntPtr name);

        [PreserveSig]
        Int32 SetDisplayName(IntPtr name, IntPtr eventContext);

        [PreserveSig]
        Int32 GetIconPath(out IntPtr path);

        [PreserveSig]
        Int32 SetIconPath(IntPtr path, IntPtr eventContext);

        [PreserveSig]
        Int32 GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        Int32 SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);

        [PreserveSig]
        Int32 RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        Int32 UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // --- IAudioSessionControl ---
        void GetState(out Int32 state);

        [PreserveSig]
        Int32 GetDisplayName(out IntPtr name);

        [PreserveSig]
        Int32 SetDisplayName(IntPtr name, IntPtr eventContext);

        [PreserveSig]
        Int32 GetIconPath(out IntPtr path);

        [PreserveSig]
        Int32 SetIconPath(IntPtr path, IntPtr eventContext);

        [PreserveSig]
        Int32 GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        Int32 SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);

        [PreserveSig]
        Int32 RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        Int32 UnregisterAudioSessionNotification(IntPtr client);

        // --- IAudioSessionControl2 ---
        [PreserveSig]
        Int32 GetSessionIdentifier(out IntPtr id);

        [PreserveSig]
        Int32 GetSessionInstanceIdentifier(out IntPtr id);

        // May return the success code AUDCLNT_S_NO_SINGLE_PROCESS, which is fine.
        void GetProcessId(out Int32 processId);

        // S_OK (0) = this is the system sounds session, S_FALSE (1) = it is not.
        [PreserveSig]
        Int32 IsSystemSoundsSession();

        [PreserveSig]
        Int32 SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] Boolean optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        void SetMasterVolume(Single level, ref Guid eventContext);

        void GetMasterVolume(out Single level);

        void SetMute([MarshalAs(UnmanagedType.Bool)] Boolean mute, ref Guid eventContext);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out Boolean mute);
    }

    [ComImport]
    [Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    public interface IAudioSessionNotification
    {
        [PreserveSig]
        Int32 OnSessionCreated(IAudioSessionControl newSession);
    }

    // Undocumented but stable since Windows 7; used by every "switch default audio device" tool.
    // Only the method count before SetDefaultEndpoint matters, so the others use placeholders.
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        Int32 GetMixFormat(IntPtr a, IntPtr b);

        [PreserveSig]
        Int32 GetDeviceFormat(IntPtr a, Int32 b, IntPtr c);

        [PreserveSig]
        Int32 ResetDeviceFormat(IntPtr a);

        [PreserveSig]
        Int32 SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);

        [PreserveSig]
        Int32 GetProcessingPeriod(IntPtr a, Int32 b, IntPtr c, IntPtr d);

        [PreserveSig]
        Int32 SetProcessingPeriod(IntPtr a, IntPtr b);

        [PreserveSig]
        Int32 GetShareMode(IntPtr a, IntPtr b);

        [PreserveSig]
        Int32 SetShareMode(IntPtr a, IntPtr b);

        [PreserveSig]
        Int32 GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);

        [PreserveSig]
        Int32 SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);

        [PreserveSig]
        Int32 SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] String deviceId, ERole role);

        [PreserveSig]
        Int32 SetEndpointVisibility(IntPtr a, Int32 b);
    }

    // Receives "a new audio session was created" callbacks (e.g. Spotify starts playing)
    // so the app list refreshes right away. Must never throw and must never call back into Core Audio.
    // Public + ComVisible because Windows calls it through a COM callable wrapper.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class SessionCreatedNotifier : IAudioSessionNotification
    {
        private readonly Action _onCreated;

        internal SessionCreatedNotifier(Action onCreated) => this._onCreated = onCreated;

        public Int32 OnSessionCreated(IAudioSessionControl newSession)
        {
            try
            {
                this._onCreated?.Invoke();
            }
            catch
            {
            }
            finally
            {
                CoreAudio.Release(newSession);
            }

            return 0;
        }
    }
}
