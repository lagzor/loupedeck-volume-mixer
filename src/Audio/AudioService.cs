namespace Loupedeck.DeviceVolumeMixerPlugin.Audio
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;

    internal sealed record DeviceInfo(String Id, String FriendlyName, String ShortName, Boolean IsCapture);

    internal sealed record AppInfo(String Key, String Name);

    internal readonly record struct VolumeState(Boolean Available, Int32 Volume, Boolean Muted, String Name);

    // Public face of the audio engine. Safe to call from any thread, never throws, never blocks on Windows audio.
    //
    // Why this design (and why the existing plugin probably crashes):
    //  * ALL Core Audio (COM) calls happen on one dedicated background thread. The Loupedeck service
    //    calls actions from its own threads; touching COM objects from there races with device changes.
    //  * Devices that disappear (USB headset unplugged, Bluetooth sleeping, RDP) make COM calls fail.
    //    Every failure is caught, the cached handle is dropped and re-created on the next poll.
    //  * The Loupedeck side only ever reads cached snapshots, so drawing a dial never waits for audio.
    internal static class AudioService
    {
        public const String DefaultRender = "@default-render";
        public const String DefaultCapture = "@default-capture";
        public const String SystemSounds = "@system";

        // Percent per dial tick.
        public const Int32 StepPercent = 2;

        private static readonly Object Gate = new Object();
        private static AudioWorker _worker;
        private static volatile Boolean _notificationsEnabled;

        // Raised on the audio thread with the keys whose state changed.
        // Device keys are device parameters (device id or @default-*), app keys are prefixed with "app:".
        public static event Action<IReadOnlyCollection<String>> StateChanged;

        public static void Start()
        {
            lock (Gate)
            {
                if (_worker == null)
                {
                    _notificationsEnabled = true;
                    _worker = new AudioWorker(QueueStateChanged);
                    _worker.Start();
                }
            }
        }

        // Blocks until the first device/app enumeration is done (or the timeout passes).
        public static Boolean WaitUntilReady(TimeSpan timeout)
        {
            var worker = _worker;
            return worker != null && worker.WaitUntilReady(timeout);
        }

        // Called by actions when Loupedeck asks for their parameter lists. Starts the audio thread if
        // Plugin.Load has not run yet and waits briefly, so the list is not empty right after startup.
        public static void EnsureReady()
        {
            Start();
            WaitUntilReady(TimeSpan.FromSeconds(2));
        }

        public static void Stop()
        {
            // Stop talking to Loupedeck first, so nothing calls into the service while it unloads us.
            _notificationsEnabled = false;
            StateChanged = null;

            AudioWorker worker;
            lock (Gate)
            {
                worker = _worker;
                _worker = null;
            }

            worker?.Dispose();
        }

        public static IReadOnlyList<DeviceInfo> Devices => _worker?.Devices ?? Array.Empty<DeviceInfo>();

        public static IReadOnlyList<AppInfo> Apps => _worker?.Apps ?? Array.Empty<AppInfo>();

        public static String DefaultRenderId => _worker?.DefaultRenderId;

        public static String DefaultCaptureId => _worker?.DefaultCaptureId;

        public static VolumeState GetDeviceState(String deviceParam)
            => _worker?.GetDeviceState(deviceParam) ?? new VolumeState(false, 0, false, "Starting…");

        public static void AdjustDevice(String deviceParam, Int32 ticks) => _worker?.AdjustDevice(deviceParam, ticks);

        public static void ToggleDeviceMute(String deviceParam) => _worker?.ToggleDeviceMute(deviceParam);

        public static void SetDefaultDevice(String deviceId) => _worker?.SetDefaultDevice(deviceId);

        public static VolumeState GetAppState(String appKey)
            => _worker?.GetAppState(appKey) ?? new VolumeState(false, 0, false, "Starting…");

        public static void AdjustApp(String appKey, Int32 ticks) => _worker?.AdjustApp(appKey, ticks);

        public static void ToggleAppMute(String appKey) => _worker?.ToggleAppMute(appKey);

        public static String AppStateKey(String appKey) => "app:" + appKey;

        public static String DeviceName(String deviceParam, Boolean shortName) => DeviceName(deviceParam, Devices, shortName);

        public static String DeviceName(String deviceParam, IReadOnlyList<DeviceInfo> devices, Boolean shortName)
        {
            switch (deviceParam)
            {
                case DefaultRender:
                    return "Default Out";
                case DefaultCapture:
                    return "Default In";
            }

            var device = devices.FirstOrDefault(d => d.Id == deviceParam);
            return device == null ? "Disconnected" : shortName ? device.ShortName : device.FriendlyName;
        }

        // The audio thread must never call into Loupedeck itself (it could block on the service and then
        // the thread can't stop on unload). Notifications are handed to the thread pool instead.
        private static void QueueStateChanged(IReadOnlyCollection<String> keys)
        {
            if (!_notificationsEnabled)
            {
                return;
            }

            ThreadPool.UnsafeQueueUserWorkItem(_ => RaiseStateChanged(keys), null);
        }

        private static void RaiseStateChanged(IReadOnlyCollection<String> keys)
        {
            if (!_notificationsEnabled)
            {
                return;
            }

            var handlers = StateChanged;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<IReadOnlyCollection<String>>)handler)(keys);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "StateChanged handler failed");
                }
            }
        }
    }

    internal sealed class AudioWorker : IDisposable
    {
        private const Int32 FastPollMs = 250;     // watched device volumes
        private const Int32 MediumPollMs = 1000;  // watched app volumes + default devices
        private const Int32 SlowPollMs = 3000;    // device list + app list

        private static Guid _eventContext = Guid.NewGuid();

        private readonly Action<IReadOnlyCollection<String>> _raiseChanged;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        // Shared with other threads (read-only snapshots / concurrent collections).
        private readonly ConcurrentDictionary<String, VolumeState> _deviceStates = new ConcurrentDictionary<String, VolumeState>();
        private readonly ConcurrentDictionary<String, VolumeState> _appStates = new ConcurrentDictionary<String, VolumeState>();
        private readonly ConcurrentDictionary<String, Byte> _watchedDevices = new ConcurrentDictionary<String, Byte>();
        private readonly ConcurrentDictionary<String, Byte> _watchedApps = new ConcurrentDictionary<String, Byte>();
        private readonly ConcurrentDictionary<String, String> _knownAppNames = new ConcurrentDictionary<String, String>();

        private volatile IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
        private volatile IReadOnlyList<AppInfo> _apps = Array.Empty<AppInfo>();
        private volatile String _defaultRenderId;
        private volatile String _defaultCaptureId;
        private volatile Boolean _stopping;
        private volatile Boolean _refreshListsRequested = true;
        private volatile Boolean _pollNowRequested;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly ConcurrentDictionary<String, Byte> _loggedParams = new ConcurrentDictionary<String, Byte>();

        // Owned by the audio thread only.
        private IMMDeviceEnumerator _enumerator;
        private readonly Dictionary<String, EndpointHandle> _endpoints = new Dictionary<String, EndpointHandle>();
        private readonly Dictionary<Int32, String> _processNames = new Dictionary<Int32, String>();
        private readonly HashSet<String> _changedKeys = new HashSet<String>();
        private String _lastLoggedError;
        private DateTime _lastLoggedErrorTime;

        public AudioWorker(Action<IReadOnlyCollection<String>> raiseChanged)
        {
            this._raiseChanged = raiseChanged;
            this._thread = new Thread(this.Run)
            {
                IsBackground = true,
                Name = "DeviceVolumeMixer audio",
            };
            this._thread.SetApartmentState(ApartmentState.MTA);
        }

        public IReadOnlyList<DeviceInfo> Devices => this._devices;

        public IReadOnlyList<AppInfo> Apps => this._apps;

        public String DefaultRenderId => this._defaultRenderId;

        public String DefaultCaptureId => this._defaultCaptureId;

        public void Start() => this._thread.Start();

        public Boolean WaitUntilReady(TimeSpan timeout)
        {
            try
            {
                return this._ready.Wait(timeout);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            this._stopping = true;
            try
            {
                this._queue.CompleteAdding();
            }
            catch
            {
            }

            if (!this._thread.Join(TimeSpan.FromSeconds(3)))
            {
                PluginLog.Warning("Audio thread did not stop within 3 seconds");
            }
        }

        // ---------------------------------------------------------------- public API (any thread)

        public VolumeState GetDeviceState(String deviceParam)
        {
            if (String.IsNullOrEmpty(deviceParam))
            {
                return new VolumeState(false, 0, false, "Select device");
            }

            if (!LooksLikeDevice(deviceParam))
            {
                // E.g. an action saved by an older version of this plugin. It has to be re-added.
                if (this._loggedParams.TryAdd("bad:" + deviceParam, 0))
                {
                    PluginLog.Warning($"Unknown device parameter '{deviceParam}' - re-add this action from the action list");
                }

                return new VolumeState(false, 0, false, "Re-add");
            }

            if (this._watchedDevices.TryAdd(deviceParam, 0))
            {
                this._pollNowRequested = true;
                PluginLog.Info($"Device requested: '{deviceParam}' (known device: {this._devices.Any(d => d.Id == deviceParam) || deviceParam.StartsWith("@", StringComparison.Ordinal)})");
            }

            return this._deviceStates.TryGetValue(deviceParam, out var state)
                ? state
                : new VolumeState(false, 0, false, AudioService.DeviceName(deviceParam, this._devices, true));
        }

        public VolumeState GetAppState(String appKey)
        {
            if (String.IsNullOrEmpty(appKey))
            {
                return new VolumeState(false, 0, false, "Select app");
            }

            if (this._watchedApps.TryAdd(appKey, 0))
            {
                this._pollNowRequested = true;
            }

            return this._appStates.TryGetValue(appKey, out var state)
                ? state
                : new VolumeState(false, 0, false, this.AppDisplayName(appKey));
        }

        public void AdjustDevice(String deviceParam, Int32 ticks)
        {
            if (String.IsNullOrEmpty(deviceParam) || ticks == 0 || !LooksLikeDevice(deviceParam))
            {
                return;
            }

            this._watchedDevices.TryAdd(deviceParam, 0);

            // Show the new value right away; the audio thread corrects it a moment later if needed.
            if (this._deviceStates.TryGetValue(deviceParam, out var cached) && cached.Available)
            {
                var predicted = Math.Clamp(cached.Volume + (ticks * AudioService.StepPercent), 0, 100);
                this._deviceStates[deviceParam] = cached with { Volume = predicted, Muted = cached.Muted && ticks < 0 };
            }

            this.Post(() =>
            {
                this.WithEndpoint(deviceParam, h =>
                {
                    h.Volume.GetMasterVolumeLevelScalar(out var current);
                    h.Volume.GetMute(out var muted);
                    var next = Math.Clamp(ToPercent(current) + (ticks * AudioService.StepPercent), 0, 100);
                    h.Volume.SetMasterVolumeLevelScalar(next / 100f, ref _eventContext);

                    // Same as the Windows volume keys: turning up un-mutes.
                    if (muted && ticks > 0)
                    {
                        h.Volume.SetMute(false, ref _eventContext);
                    }
                });
                this.PollDevice(deviceParam);
            });
        }

        public void ToggleDeviceMute(String deviceParam)
        {
            if (String.IsNullOrEmpty(deviceParam) || !LooksLikeDevice(deviceParam))
            {
                return;
            }

            this._watchedDevices.TryAdd(deviceParam, 0);
            this.Post(() =>
            {
                this.WithEndpoint(deviceParam, h =>
                {
                    h.Volume.GetMute(out var muted);
                    h.Volume.SetMute(!muted, ref _eventContext);
                });
                this.PollDevice(deviceParam);
            });
        }

        public void SetDefaultDevice(String deviceId)
        {
            if (String.IsNullOrEmpty(deviceId) || deviceId.StartsWith("@", StringComparison.Ordinal))
            {
                return;
            }

            this.Post(() =>
            {
                IPolicyConfig policy = null;
                try
                {
                    policy = CoreAudio.Create<IPolicyConfig>(CoreAudio.ClsidPolicyConfigClient);
                    var hr1 = policy.SetDefaultEndpoint(deviceId, ERole.Console);
                    var hr2 = policy.SetDefaultEndpoint(deviceId, ERole.Multimedia);
                    if (hr1 != 0 || hr2 != 0)
                    {
                        PluginLog.Warning($"SetDefaultEndpoint failed: 0x{hr1:X8} / 0x{hr2:X8}");
                    }
                }
                finally
                {
                    CoreAudio.Release(policy);
                }

                this.PollDefaults();
                foreach (var key in new[] { AudioService.DefaultRender, AudioService.DefaultCapture })
                {
                    if (this._watchedDevices.ContainsKey(key))
                    {
                        this.PollDevice(key);
                    }
                }
            });
        }

        public void AdjustApp(String appKey, Int32 ticks)
        {
            if (String.IsNullOrEmpty(appKey) || ticks == 0)
            {
                return;
            }

            this._watchedApps.TryAdd(appKey, 0);

            if (this._appStates.TryGetValue(appKey, out var cached) && cached.Available)
            {
                var predicted = Math.Clamp(cached.Volume + (ticks * AudioService.StepPercent), 0, 100);
                this._appStates[appKey] = cached with { Volume = predicted, Muted = cached.Muted && ticks < 0 };
            }

            this.Post(() =>
            {
                this.VisitSessions((key, name, volume) =>
                {
                    if (key != appKey)
                    {
                        return;
                    }

                    volume.GetMasterVolume(out var current);
                    volume.GetMute(out var muted);
                    var next = Math.Clamp(ToPercent(current) + (ticks * AudioService.StepPercent), 0, 100);
                    volume.SetMasterVolume(next / 100f, ref _eventContext);
                    if (muted && ticks > 0)
                    {
                        volume.SetMute(false, ref _eventContext);
                    }
                });
                this.PollApps(onlyKey: appKey);
            });
        }

        public void ToggleAppMute(String appKey)
        {
            if (String.IsNullOrEmpty(appKey))
            {
                return;
            }

            this._watchedApps.TryAdd(appKey, 0);
            this.Post(() =>
            {
                // Mute everything if anything is audible, otherwise unmute everything.
                var anyUnmuted = false;
                this.VisitSessions((key, name, volume) =>
                {
                    if (key == appKey)
                    {
                        volume.GetMute(out var muted);
                        anyUnmuted |= !muted;
                    }
                });

                this.VisitSessions((key, name, volume) =>
                {
                    if (key == appKey)
                    {
                        volume.SetMute(anyUnmuted, ref _eventContext);
                    }
                });
                this.PollApps(onlyKey: appKey);
            });
        }

        // ---------------------------------------------------------------- audio thread

        private void Post(Action work)
        {
            if (this._stopping)
            {
                return;
            }

            try
            {
                this._queue.Add(work);
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
            }
        }

        // An unhandled exception on a background thread kills the whole Logi Plugin Service process,
        // so nothing is allowed to escape from this method.
        private void Run()
        {
            try
            {
                this.RunLoop();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Audio thread stopped unexpectedly");
            }
            finally
            {
                try
                {
                    this.ReleaseAll();
                }
                catch
                {
                }
            }
        }

        private void RunLoop()
        {
            try
            {
                this._enumerator = CoreAudio.Create<IMMDeviceEnumerator>(CoreAudio.ClsidMMDeviceEnumerator);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Could not create MMDeviceEnumerator, audio control disabled");
                this._ready.Set();
                return;
            }

            PluginLog.Info("Audio thread started");

            var clock = Stopwatch.StartNew();
            Int64 nextFast = 0, nextMedium = 0, nextSlow = 0;

            while (!this._stopping)
            {
                try
                {
                    if (this._queue.TryTake(out var work, 50))
                    {
                        this.SafeRun(work);

                        // Drain whatever piled up (fast dial turns) before polling.
                        while (!this._stopping && this._queue.TryTake(out work))
                        {
                            this.SafeRun(work);
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    break; // CompleteAdding was called.
                }

                if (this._stopping)
                {
                    break;
                }

                var now = clock.ElapsedMilliseconds;

                if (this._refreshListsRequested || now >= nextSlow)
                {
                    this._refreshListsRequested = false;
                    this.SafeRun(this.RefreshLists);
                    nextSlow = now + SlowPollMs;
                    nextMedium = 0;

                    if (!this._ready.IsSet)
                    {
                        // Poll everything once before declaring ready, so the first values are real.
                        this.SafeRun(this.PollDefaults);
                        this.SafeRun(this.PollWatchedDevices);
                        this._ready.Set();
                        PluginLog.Info("Devices: " + String.Join(" | ", this._devices.Select(d => $"{(d.IsCapture ? "In" : "Out")} '{d.FriendlyName}' = {d.Id}")));
                    }
                }

                if (this._pollNowRequested)
                {
                    this._pollNowRequested = false;
                    nextFast = 0;
                    nextMedium = 0;
                }

                if (now >= nextMedium)
                {
                    this.SafeRun(this.PollDefaults);
                    this.SafeRun(() => this.PollApps(onlyKey: null));
                    nextMedium = now + MediumPollMs;
                }

                if (now >= nextFast)
                {
                    this.SafeRun(this.PollWatchedDevices);
                    nextFast = now + FastPollMs;
                }

                this.SafeRun(this.FlushChanges);
            }
        }

        private void SafeRun(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                this.LogThrottled(ex);
            }
        }

        private void LogThrottled(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            var now = DateTime.UtcNow;
            if (message != this._lastLoggedError || (now - this._lastLoggedErrorTime) > TimeSpan.FromMinutes(1))
            {
                this._lastLoggedError = message;
                this._lastLoggedErrorTime = now;
                PluginLog.Warning(ex, "Audio operation failed (handled)");
            }
        }

        private void FlushChanges()
        {
            if (this._changedKeys.Count == 0)
            {
                return;
            }

            var keys = this._changedKeys.ToArray();
            this._changedKeys.Clear();
            this._raiseChanged?.Invoke(keys);
        }

        private void SetDeviceState(String key, VolumeState state)
        {
            var hadOld = this._deviceStates.TryGetValue(key, out var old);
            if (!hadOld || old.Available != state.Available)
            {
                PluginLog.Info($"Device '{key}' ({state.Name}): available={state.Available}, volume={state.Volume}, muted={state.Muted}");
            }

            if (!hadOld || old != state)
            {
                this._deviceStates[key] = state;
                this._changedKeys.Add(key);
            }
        }

        private void SetAppState(String key, VolumeState state)
        {
            if (!this._appStates.TryGetValue(key, out var old) || old != state)
            {
                this._appStates[key] = state;
                this._changedKeys.Add(AudioService.AppStateKey(key));
            }
        }

        private static Boolean LooksLikeDevice(String deviceParam)
            => deviceParam.StartsWith("{", StringComparison.Ordinal) || deviceParam.StartsWith("@", StringComparison.Ordinal);

        private static Int32 ToPercent(Single scalar) => Math.Clamp((Int32)Math.Round(scalar * 100f), 0, 100);

        // ---------------------------------------------------------------- devices

        private String ResolveDeviceId(String deviceParam)
        {
            switch (deviceParam)
            {
                case AudioService.DefaultRender:
                    return this.GetDefaultId(EDataFlow.Render);
                case AudioService.DefaultCapture:
                    return this.GetDefaultId(EDataFlow.Capture);
                default:
                    return deviceParam;
            }
        }

        private String GetDefaultId(EDataFlow flow)
        {
            IMMDevice device = null;
            try
            {
                this._enumerator.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out device);
                device.GetId(out var id);
                return id;
            }
            catch (COMException)
            {
                return null; // No device of this kind at all.
            }
            finally
            {
                CoreAudio.Release(device);
            }
        }

        private EndpointHandle GetEndpoint(String deviceId)
        {
            if (this._endpoints.TryGetValue(deviceId, out var handle))
            {
                return handle;
            }

            IMMDevice device = null;
            try
            {
                this._enumerator.GetDevice(deviceId, out device);
                device.GetState(out var state);
                if (state != CoreAudio.DeviceStateActive)
                {
                    CoreAudio.Release(device);
                    return null;
                }

                var iid = CoreAudio.IidAudioEndpointVolume;
                device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var volume);
                handle = new EndpointHandle(device, (IAudioEndpointVolume)volume);
                this._endpoints[deviceId] = handle;
                return handle;
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                CoreAudio.Release(device);
                if (this._loggedParams.TryAdd("open:" + deviceId, 0))
                {
                    PluginLog.Warning(ex, $"Could not open device '{deviceId}'");
                }

                return null; // Unplugged or never existed.
            }
        }

        private void WithEndpoint(String deviceParam, Action<EndpointHandle> action)
        {
            var id = this.ResolveDeviceId(deviceParam);
            if (id == null)
            {
                return;
            }

            var handle = this.GetEndpoint(id);
            if (handle == null)
            {
                return;
            }

            try
            {
                action(handle);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                this.InvalidateEndpoint(id);
            }
        }

        private void PollWatchedDevices()
        {
            foreach (var key in this._watchedDevices.Keys)
            {
                this.PollDevice(key);
            }
        }

        private void PollDevice(String deviceParam)
        {
            var devices = this._devices;
            var name = AudioService.DeviceName(deviceParam, devices, true);
            var id = this.ResolveDeviceId(deviceParam);

            if (deviceParam.StartsWith("@", StringComparison.Ordinal) && id != null)
            {
                var device = devices.FirstOrDefault(d => d.Id == id);
                if (device != null)
                {
                    name = $"{name}: {device.ShortName}";
                }
            }

            var state = new VolumeState(false, 0, false, name);
            if (id != null)
            {
                var handle = this.GetEndpoint(id);
                if (handle != null)
                {
                    try
                    {
                        handle.Volume.GetMasterVolumeLevelScalar(out var level);
                        handle.Volume.GetMute(out var muted);
                        state = new VolumeState(true, ToPercent(level), muted, name);
                    }
                    catch (Exception ex) when (IsComFailure(ex))
                    {
                        this.InvalidateEndpoint(id);
                    }
                }
            }

            this.SetDeviceState(deviceParam, state);
        }

        private void PollDefaults()
        {
            var render = this.GetDefaultId(EDataFlow.Render);
            var capture = this.GetDefaultId(EDataFlow.Capture);
            if (render != this._defaultRenderId || capture != this._defaultCaptureId)
            {
                this._defaultRenderId = render;
                this._defaultCaptureId = capture;
                this._changedKeys.Add("defaults");
            }
        }

        private void RefreshLists()
        {
            var devices = new List<DeviceInfo>();
            this.EnumerateDevices(EDataFlow.Render, devices);
            this.EnumerateDevices(EDataFlow.Capture, devices);

            // Use the short name ("Speakers") unless two devices share it, then the full name.
            var result = devices
                .Select(d => devices.Count(o => o.IsCapture == d.IsCapture && String.Equals(o.ShortName, d.ShortName, StringComparison.OrdinalIgnoreCase)) > 1
                    ? d with { ShortName = d.FriendlyName }
                    : d)
                .ToList();

            if (!result.SequenceEqual(this._devices))
            {
                this._devices = result;
                this._changedKeys.Add("devices");
                foreach (var key in this._watchedDevices.Keys)
                {
                    this._changedKeys.Add(key);
                }
            }

            // Drop cached handles for devices that are gone.
            foreach (var id in this._endpoints.Keys.ToList())
            {
                if (!result.Any(d => d.Id == id))
                {
                    this.InvalidateEndpoint(id);
                }
            }

            this._processNames.Clear();
            var apps = new Dictionary<String, String>();
            this.VisitSessions((key, name, volume) => apps[key] = name);
            foreach (var app in apps)
            {
                this._knownAppNames[app.Key] = app.Value;
            }

            var appList = apps
                .Select(a => new AppInfo(a.Key, a.Value))
                .OrderBy(a => a.Key == AudioService.SystemSounds ? 0 : 1)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (!appList.SequenceEqual(this._apps))
            {
                this._apps = appList;
                this._changedKeys.Add("apps");
            }
        }

        private void EnumerateDevices(EDataFlow flow, List<DeviceInfo> target)
        {
            IMMDeviceCollection collection = null;
            try
            {
                this._enumerator.EnumAudioEndpoints(flow, CoreAudio.DeviceStateActive, out collection);
                collection.GetCount(out var count);
                for (var i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    IPropertyStore store = null;
                    try
                    {
                        collection.Item(i, out device);
                        device.GetId(out var id);
                        device.OpenPropertyStore(CoreAudio.StgmRead, out store);
                        var friendly = CoreAudio.ReadString(store, CoreAudio.PKeyDeviceFriendlyName) ?? id;
                        var shortName = CoreAudio.ReadString(store, CoreAudio.PKeyDeviceDeviceDesc) ?? friendly;
                        target.Add(new DeviceInfo(id, friendly, shortName, flow == EDataFlow.Capture));
                    }
                    catch (Exception ex) when (IsComFailure(ex))
                    {
                        // Device vanished while enumerating; skip it.
                    }
                    finally
                    {
                        CoreAudio.Release(store);
                        CoreAudio.Release(device);
                    }
                }
            }
            finally
            {
                CoreAudio.Release(collection);
            }
        }

        // ---------------------------------------------------------------- applications

        private String AppDisplayName(String appKey)
            => this._knownAppNames.TryGetValue(appKey, out var name) ? name
             : appKey == AudioService.SystemSounds ? "System Sounds"
             : appKey;

        private void PollApps(String onlyKey)
        {
            if (this._watchedApps.IsEmpty)
            {
                return;
            }

            var found = new Dictionary<String, (Int32 Volume, Boolean AllMuted)>();
            this.VisitSessions((key, name, volume) =>
            {
                if (!this._watchedApps.ContainsKey(key) || (onlyKey != null && key != onlyKey))
                {
                    return;
                }

                volume.GetMasterVolume(out var level);
                volume.GetMute(out var muted);
                var percent = ToPercent(level);
                found[key] = found.TryGetValue(key, out var existing)
                    ? (Math.Max(existing.Volume, percent), existing.AllMuted && muted)
                    : (percent, muted);
            });

            foreach (var key in this._watchedApps.Keys)
            {
                if (onlyKey != null && key != onlyKey)
                {
                    continue;
                }

                var name = this.AppDisplayName(key);
                this.SetAppState(key, found.TryGetValue(key, out var s)
                    ? new VolumeState(true, s.Volume, s.AllMuted, name)
                    : new VolumeState(false, 0, false, name));
            }
        }

        // Calls visitor(appKey, displayName, volume) for every live session on every active output device.
        // The ISimpleAudioVolume is only valid during the callback.
        private void VisitSessions(Action<String, String, ISimpleAudioVolume> visitor)
        {
            foreach (var device in this._devices.Where(d => !d.IsCapture))
            {
                var manager = this.GetSessionManager(device.Id);
                if (manager == null)
                {
                    continue;
                }

                IAudioSessionEnumerator sessions = null;
                try
                {
                    manager.GetSessionEnumerator(out sessions);
                    sessions.GetCount(out var count);
                    for (var i = 0; i < count; i++)
                    {
                        IAudioSessionControl control = null;
                        try
                        {
                            sessions.GetSession(i, out control);
                            control.GetState(out var state);
                            if (state == CoreAudio.SessionStateExpired)
                            {
                                continue;
                            }

                            if (control is not IAudioSessionControl2 control2 || control is not ISimpleAudioVolume volume)
                            {
                                continue;
                            }

                            String key, name;
                            if (control2.IsSystemSoundsSession() == 0)
                            {
                                key = AudioService.SystemSounds;
                                name = "System Sounds";
                            }
                            else
                            {
                                control2.GetProcessId(out var pid);
                                name = this.GetProcessName(pid);
                                if (name == null)
                                {
                                    continue;
                                }

                                key = name.ToLowerInvariant();
                            }

                            visitor(key, name, volume);
                        }
                        catch (Exception ex) when (IsComFailure(ex))
                        {
                            // Session ended while we looked at it.
                        }
                        finally
                        {
                            CoreAudio.Release(control);
                        }
                    }
                }
                catch (Exception ex) when (IsComFailure(ex))
                {
                    this.InvalidateEndpoint(device.Id);
                }
                finally
                {
                    CoreAudio.Release(sessions);
                }
            }
        }

        private IAudioSessionManager2 GetSessionManager(String deviceId)
        {
            var handle = this.GetEndpoint(deviceId);
            if (handle == null)
            {
                return null;
            }

            if (handle.Sessions != null)
            {
                return handle.Sessions;
            }

            try
            {
                var iid = CoreAudio.IidAudioSessionManager2;
                handle.Device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var manager);
                handle.Sessions = (IAudioSessionManager2)manager;

                // Windows only reliably reports sessions created later if someone listens for them.
                // Get the enumerator once first (documented requirement), then register.
                handle.Sessions.GetSessionEnumerator(out var first);
                CoreAudio.Release(first);

                // Optional: if registration fails we just lose the instant refresh (the 3 s poll still works).
                try
                {
                    var notifier = new SessionCreatedNotifier(() => this._refreshListsRequested = true);
                    if (handle.Sessions.RegisterSessionNotification(notifier) == 0)
                    {
                        handle.Notifier = notifier;
                    }
                }
                catch (Exception ex)
                {
                    this.LogThrottled(ex);
                }

                return handle.Sessions;
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                this.InvalidateEndpoint(deviceId);
                return null;
            }
        }

        private String GetProcessName(Int32 pid)
        {
            if (pid <= 0)
            {
                return null;
            }

            if (this._processNames.TryGetValue(pid, out var cached))
            {
                return cached;
            }

            String name = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
            }
            catch
            {
                // Process exited or is not accessible.
            }

            this._processNames[pid] = name;
            return name;
        }

        // ---------------------------------------------------------------- cleanup

        private void InvalidateEndpoint(String deviceId)
        {
            if (this._endpoints.Remove(deviceId, out var handle))
            {
                handle.Dispose();
            }
        }

        private void ReleaseAll()
        {
            foreach (var handle in this._endpoints.Values)
            {
                handle.Dispose();
            }

            this._endpoints.Clear();
            CoreAudio.Release(this._enumerator);
            this._enumerator = null;
        }

        private static Boolean IsComFailure(Exception ex)
            => ex is COMException || ex is InvalidCastException || ex is InvalidComObjectException
            || ex is ArgumentException || ex is UnauthorizedAccessException;

        private sealed class EndpointHandle : IDisposable
        {
            public EndpointHandle(IMMDevice device, IAudioEndpointVolume volume)
            {
                this.Device = device;
                this.Volume = volume;
            }

            public IMMDevice Device { get; }

            public IAudioEndpointVolume Volume { get; }

            public IAudioSessionManager2 Sessions { get; set; }

            public SessionCreatedNotifier Notifier { get; set; }

            public void Dispose()
            {
                if (this.Sessions != null && this.Notifier != null)
                {
                    try
                    {
                        this.Sessions.UnregisterSessionNotification(this.Notifier);
                    }
                    catch
                    {
                    }
                }

                CoreAudio.Release(this.Sessions);
                CoreAudio.Release(this.Volume);
                CoreAudio.Release(this.Device);
                this.Sessions = null;
                this.Notifier = null;
            }
        }
    }
}
