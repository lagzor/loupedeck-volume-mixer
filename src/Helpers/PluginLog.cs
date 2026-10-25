namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;

    // Writes to the plugin log in Logi Plugin Service
    // (%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\DeviceVolumeMixer.log).
    internal static class PluginLog
    {
        private static PluginLogFile _pluginLogFile;

        public static void Init(PluginLogFile pluginLogFile)
        {
            pluginLogFile.CheckNullArgument(nameof(pluginLogFile));
            _pluginLogFile = pluginLogFile;
        }

        // Everything is also written to our own debug.log (see DebugLog), because the
        // service's plugin log is often switched off.
        public static void Verbose(String text) => _pluginLogFile?.Verbose(text);

        public static void Info(String text)
        {
            DebugLog.Write("INFO  " + text);
            _pluginLogFile?.Info(text);
        }

        public static void Warning(String text)
        {
            DebugLog.Write("WARN  " + text);
            _pluginLogFile?.Warning(text);
        }

        public static void Warning(Exception ex, String text)
        {
            DebugLog.Write(ex, "WARN  " + text);
            _pluginLogFile?.Warning(ex, text);
        }

        public static void Error(String text)
        {
            DebugLog.Write("ERROR " + text);
            _pluginLogFile?.Error(text);
        }

        public static void Error(Exception ex, String text)
        {
            DebugLog.Write(ex, "ERROR " + text);
            _pluginLogFile?.Error(ex, text);
        }
    }
}
