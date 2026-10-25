namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.DeviceVolumeMixerPlugin.Audio;

    // Button: mute/unmute one chosen device. Turns red when muted.
    public class DeviceMuteCommand : PluginDynamicCommand
    {
        public DeviceMuteCommand()
            : base()
        {
            // Parameterless base constructor = an action WITH parameters (HasParameter = true).
            // Each device/app is then registered as its own entry in SyncParameters; the parameter is the real device id / app name.
            this.DisplayName = "Device Mute";
            this.Description = "Mutes or unmutes the selected audio device.";
            this.GroupName = "Device Mute";
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
                return Ui.SyncParameters((name, displayName, group) => this.AddParameter(name, displayName, group), this._addedParameters, Ui.DeviceParameters(includeDefaults: true, groupName: "Device Mute"));
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
            => Ui.Try(() => AudioService.ToggleDeviceMute(actionParameter));

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
            => AudioService.GetDeviceState(actionParameter).Name;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                var state = AudioService.GetDeviceState(actionParameter);
                var background = !state.Available ? Ui.Offline : state.Muted ? Ui.Muted : Ui.Normal;
                return Ui.Tile(imageSize, $"{state.Name}\n{Ui.FormatValue(state)}", background);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not draw mute button");
                return null;
            }
        }

        private void OnAudioStateChanged(IReadOnlyCollection<String> keys)
        {
            if (Ui.ListChanged(keys) && this.SyncParameters())
            {
                Ui.Try(this.ParametersChanged);
            }

            foreach (var key in Ui.DeviceKeys(keys))
            {
                Ui.Try(() => this.ActionImageChanged(key));
            }
        }
    }
}
