namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;
    using System.IO;

    // The plugin's own log file, written even when logging is turned off in Logi Plugin Service.
    //   %LOCALAPPDATA%\Logi\LogiPluginService\PluginData\DeviceVolumeMixer\debug.log
    // Kept small (rotates at 512 KB). Never throws.
    internal static class DebugLog
    {
        private const Int64 MaxBytes = 512 * 1024;
        private static readonly Object Gate = new Object();
        private static String _path;
        private static Boolean _initialized;

        public static void Init(String dataDirectory)
        {
            _initialized = true;
            try
            {
                var dir = !String.IsNullOrEmpty(dataDirectory)
                    ? dataDirectory
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Logi", "LogiPluginService", "PluginData", "DeviceVolumeMixer");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "debug.log");
            }
            catch
            {
                _path = null;
            }
        }

        public static void Write(String text)
        {
            if (!_initialized)
            {
                Init(null); // Something logged before Plugin.Load: use the default folder.
            }

            var path = _path;
            if (path == null)
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Copy(path, path + ".old", overwrite: true);
                        File.Delete(path);
                    }

                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {text}{Environment.NewLine}");
                }
            }
            catch
            {
            }
        }

        public static void Write(Exception ex, String text) => Write($"{text}: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
    }
}
