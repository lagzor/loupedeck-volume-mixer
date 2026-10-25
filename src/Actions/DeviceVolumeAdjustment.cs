namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.DeviceVolumeMixerPlugin.Audio;

    // Dial: turn = volume of ONE chosen device (e.g. only the speakers), press = mute/unmute that device.
    public class DeviceVolumeAdjustment : PluginDynamicAdjustment
    {
        public DeviceVolumeAdjustment()
            : base(hasReset: true)
        {
            // Parameterless base constructor = an action WITH parameters (HasParameter = true).
            // Each device/app is then registered as its own entry in SyncParameters; the parameter is the real device id / app name.
            this.DisplayName = "Device Volume";
            this.Description = "Turn to change the volume of the selected audio device. Press to mute/unmute.";
            this.GroupName = "Device Volume";
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
                return Ui.SyncParameters((name, displayName, group) => this.AddParameter(name, displayName, group), this._addedParameters, Ui.DeviceParameters(includeDefaults: true, groupName: "Device Volume"));
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


        protected override void ApplyAdjustment(String actionParameter, Int32 diff)
            => Ui.Try(() =>
            {
                AudioService.AdjustDevice(actionParameter, diff);
                this.Refresh(actionParameter);
            });

        // Pressing the dial.
        protected override void RunCommand(String actionParameter)
            => Ui.Try(() => AudioService.ToggleDeviceMute(actionParameter));

        // The dial image (GetAdjustmentImage) already shows name, percentage and bar. Returning no value
        // and an empty name stops Loupedeck from drawing a second "54%" / "Speakers" text under it.
        protected override String GetAdjustmentValue(String actionParameter) => null;

        protected override BitmapImage GetAdjustmentImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                return Ui.VolumeImage(imageSize, AudioService.GetDeviceState(actionParameter), "Not found");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Could not draw dial image");
                return null;
            }
        }

        protected override String GetAdjustmentDisplayName(String actionParameter, PluginImageSize imageSize)
            => String.Empty;

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
            => AudioService.GetDeviceState(actionParameter).Name;

        private void Refresh(String actionParameter)
        {
            this.AdjustmentValueChanged(actionParameter);
            this.ActionImageChanged(actionParameter);
        }

        private void OnAudioStateChanged(IReadOnlyCollection<String> keys)
        {
            if (Ui.ListChanged(keys) && this.SyncParameters())
            {
                Ui.Try(this.ParametersChanged);
            }

            foreach (var key in Ui.DeviceKeys(keys))
            {
                Ui.Try(() => this.Refresh(key));
            }
        }
    }
}
