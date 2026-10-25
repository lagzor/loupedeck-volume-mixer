namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.DeviceVolumeMixerPlugin.Audio;

    // Button: make a device the Windows default (e.g. switch between speakers and headset). Green = current default.
    public class DefaultDeviceCommand : PluginDynamicCommand
    {
        public DefaultDeviceCommand()
            : base()
        {
            // Parameterless base constructor = an action WITH parameters (HasParameter = true).
            // Each device/app is then registered as its own entry in SyncParameters; the parameter is the real device id / app name.
            this.DisplayName = "Set Default Device";
            this.Description = "Makes the selected audio device the Windows default.";
            this.GroupName = "Set Default Device";
        }

        protected override Boolean OnLoad()
        {
            AudioService.StateChanged += this.OnAudioStateChanged;
            AudioService.EnsureReady();
            this.SyncParameters();
            return true;
        }

        private readonly HashSet<String> _addedParameters = new HashSet<String>();

        private Boolean SyncParameters()
        {
            try
            {
                return Ui.SyncParameters((name, displayName, group) => this.AddParameter(name, displayName, group), this._addedParameters, Ui.DeviceParameters(includeDefaults: false, groupName: "Set Default Device"));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not register parameters");
                return false;
            }
        }

        protected override Boolean OnUnload()
        {
            AudioService.StateChanged -= this.OnAudioStateChanged;
            return true;
        }


        protected override void RunCommand(String actionParameter)
            => Ui.Try(() => AudioService.SetDefaultDevice(actionParameter));

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
            => AudioService.DeviceName(actionParameter, shortName: true);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                var connected = AudioService.Devices.Any(d => d.Id == actionParameter);
                var isDefault = actionParameter == AudioService.DefaultRenderId || actionParameter == AudioService.DefaultCaptureId;
                var name = AudioService.DeviceName(actionParameter, shortName: true);
                var background = !connected ? Ui.Offline : isDefault ? Ui.Active : Ui.Normal;
                return Ui.Tile(imageSize, isDefault ? $"{name}\n● Default" : name, background);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not draw default-device button");
                return null;
            }
        }

        private void OnAudioStateChanged(IReadOnlyCollection<String> keys)
        {
            if (Ui.ListChanged(keys) && this.SyncParameters())
            {
                Ui.Try(this.ParametersChanged);
            }

            if (keys.Contains("defaults") || keys.Contains("devices"))
            {
                Ui.Try(() => this.ActionImageChanged());
            }
        }
    }
}
