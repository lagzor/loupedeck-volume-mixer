namespace Loupedeck.DeviceVolumeMixerPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.DeviceVolumeMixerPlugin.Audio;

    internal static class Ui
    {
        public static readonly BitmapColor Normal = new BitmapColor(32, 32, 36);
        public static readonly BitmapColor Muted = new BitmapColor(170, 30, 30);
        public static readonly BitmapColor Active = new BitmapColor(20, 110, 60);
        public static readonly BitmapColor Offline = new BitmapColor(70, 70, 70);
        public static readonly BitmapColor Text = new BitmapColor(255, 255, 255);

        public static BitmapImage Tile(PluginImageSize size, String text, BitmapColor background)
        {
            using var builder = new BitmapBuilder(size);
            builder.Clear(background);
            builder.DrawText(text, Text);
            return builder.ToImage();
        }

        public static readonly BitmapColor BarTrack = new BitmapColor(70, 70, 78);
        public static readonly BitmapColor BarFill = new BitmapColor(46, 204, 113);
        public static readonly BitmapColor Dim = new BitmapColor(170, 170, 180);

        // Image shown next to a dial: device/app name, a big percentage and a volume bar.
        public static BitmapImage VolumeImage(PluginImageSize size, VolumeState state, String unavailableText)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            b.Clear(!state.Available ? Offline : state.Muted ? Muted : Normal);

            var nameHeight = h * 22 / 100;
            var barHeight = Math.Max(4, h * 9 / 100);
            var pad = Math.Max(3, w * 7 / 100);

            b.DrawText(state.Name ?? String.Empty, 0, 2, w, nameHeight, Dim, Math.Max(9, h * 14 / 100));

            var big = !state.Available ? unavailableText : state.Muted ? "MUTED" : $"{state.Volume}%";
            var bigSize = !state.Available || state.Muted ? Math.Max(10, h * 18 / 100) : Math.Max(14, h * 32 / 100);
            b.DrawText(big, 0, nameHeight, w, h - nameHeight - barHeight - pad, Text, bigSize);

            if (state.Available)
            {
                var barY = h - barHeight - pad;
                var barW = w - (2 * pad);
                b.FillRectangle(pad, barY, barW, barHeight, BarTrack);
                var fill = barW * state.Volume / 100;
                if (fill > 0)
                {
                    b.FillRectangle(pad, barY, fill, barHeight, state.Muted ? Text : BarFill);
                }
            }

            return b.ToImage();
        }

        // Value data for Loupedeck: text ("45%"/"Muted") plus the number 0-100 so the service can draw its own indicator.
        public static PluginAdjustmentValue AdjustmentValue(VolumeState state, String unavailableText)
            => state.Available
                ? new PluginAdjustmentValue(FormatValue(state), "{0}%", state.Volume, 0, 100, null)
                : new PluginAdjustmentValue(unavailableText);

        public static String FormatValue(VolumeState state)
            => !state.Available ? "–"
             : state.Muted ? "Muted"
             : $"{state.Volume}%";

        public static PluginActionParameter[] DeviceParameters(Boolean includeDefaults, String groupName)
        {
            var list = new List<PluginActionParameter>();
            if (includeDefaults)
            {
                list.Add(new PluginActionParameter(AudioService.DefaultRender, "Out · Default device (follows Windows)", groupName));
                list.Add(new PluginActionParameter(AudioService.DefaultCapture, "In · Default microphone (follows Windows)", groupName));
            }

            foreach (var device in AudioService.Devices.OrderBy(d => d.IsCapture).ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            {
                list.Add(new PluginActionParameter(
                    device.Id,
                    (device.IsCapture ? "In · " : "Out · ") + device.FriendlyName,
                    groupName));
            }

            return list.ToArray();
        }

        public static PluginActionParameter[] AppParameters(String groupName)
        {
            var list = new List<PluginActionParameter>
            {
                new PluginActionParameter(AudioService.SystemSounds, "System Sounds", groupName),
            };

            foreach (var app in AudioService.Apps.Where(a => a.Key != AudioService.SystemSounds))
            {
                list.Add(new PluginActionParameter(app.Key, app.Name, groupName));
            }

            return list.ToArray();
        }

        // Device keys are everything that isn't an app key or a list/default notification.
        public static IEnumerable<String> DeviceKeys(IEnumerable<String> keys)
            => keys.Where(k => !k.StartsWith("app:", StringComparison.Ordinal) && k != "devices" && k != "defaults" && k != "apps");

        public static Boolean ListChanged(IEnumerable<String> keys)
            => keys.Any(k => k == "devices" || k == "apps");

        public static IEnumerable<String> AppKeys(IEnumerable<String> keys)
            => keys.Where(k => k.StartsWith("app:", StringComparison.Ordinal)).Select(k => k.Substring(4));

        // Registers parameters with Loupedeck (AddParameter). Only ever adds: a device that is unplugged
        // stays in the list (shown as "Disconnected") so dials assigned to it keep working when it returns.
        // Returns true if something new was added.
        public static Boolean SyncParameters(Action<String, String, String> addParameter, HashSet<String> added, IEnumerable<PluginActionParameter> parameters)
        {
            var changed = false;
            lock (added)
            {
                foreach (var p in parameters)
                {
                    if (p?.Name == null || !added.Add(p.Name))
                    {
                        continue;
                    }

                    addParameter(p.Name, p.DisplayName, p.GroupName);
                    changed = true;
                }
            }

            return changed;
        }

        public static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "UI update failed (handled)");
            }
        }
    }
}
