namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;

    // Required by the SDK. This plugin is not linked to any application.
    public class DeviceVolumeMixerApplication : ClientApplication
    {
        protected override String GetProcessName() => String.Empty;

        protected override String GetBundleName() => String.Empty;
    }
}
