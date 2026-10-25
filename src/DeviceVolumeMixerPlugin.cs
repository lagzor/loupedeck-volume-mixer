// Windows Core Audio only.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;

    using Loupedeck.DeviceVolumeMixerPlugin.Audio;

    // Universal plugin: always active, not tied to a foreground application.
    public class DeviceVolumeMixerPlugin : Plugin
    {
        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        public DeviceVolumeMixerPlugin()
        {
            PluginLog.Init(this.Log);
        }

        public override void Load()
        {
            try
            {
                PluginLog.Info($"Plugin loading, version {typeof(DeviceVolumeMixerPlugin).Assembly.GetName().Version}");

                // Normally already started by an action asking for its parameters; Start() is idempotent.
                AudioService.Start();
                AudioService.WaitUntilReady(TimeSpan.FromSeconds(3));
                PluginLog.Info($"Audio service ready: {AudioService.Devices.Count} devices, {AudioService.Apps.Count} apps");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Could not start audio service");
            }
        }

        public override void Unload()
        {
            try
            {
                AudioService.Stop();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Could not stop audio service");
            }
        }
    }
}
