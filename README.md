# Device Volume Mixer – Loupedeck plugin for Windows

Control the volume of **one specific audio device** (e.g. only the speakers) from a dial on the Loupedeck CT,
regardless of which device is the Windows default.

## Actions

| Action | Put it on | What it does |
|---|---|---|
| **Device Volume** | dial / big wheel | Turn = volume of the selected device, press = mute/unmute. Shows name and volume % on the display |
| **Device Mute** | button | Mute/unmute the selected device, turns red when muted |
| **Set Default Device** | button | Makes the selected device the Windows default, green = current default |
| **App Volume** | dial | Volume of a single app (Spotify, Discord, browser …), press = mute/unmute |

In the Loupedeck action list, open **Device Volume Mixer** → e.g. **Device Volume** and drag the device you want
(e.g. *Out · Speakers (Realtek USB2.0 Audio)*) onto a dial. Every device and app is its own entry.
Besides individual devices there are *Default device (follows Windows)* and *Default microphone*.
Microphones (recording devices) work the same way as speakers.

Volume changes 2 % per tick (`StepPercent` in `src/Audio/AudioService.cs`).

## Install

1. Uninstall or disable the old volume plugin that keeps crashing.
2. Install the .NET 10 SDK if you don't have it: `winget install Microsoft.DotNet.SDK.10`
3. Run in this folder (in PowerShell):
   ```
   Set-ExecutionPolicy -Scope Process Bypass
   .\build.ps1
   ```
4. In Loupedeck: enable **Device Volume Mixer** under plugins (*Hide and show plugins*).
   If it doesn't show up, restart *Logi Plugin Service* in the Loupedeck settings.
5. Open **Device Volume Mixer → Device Volume** and drag e.g. *Out · Speakers* onto a dial.

To get an installer file: `.\build.ps1 -Package` creates `DeviceVolumeMixer.lplug4`.

Requires Logi Plugin Service 6.4+ (Loupedeck software or Logi Options+), which runs on .NET 10.

## Tips

- Device names come from Windows. If Windows is in Swedish they show as e.g. "Högtalare";
  rename the device in Windows sound settings (e.g. to "Desk Speakers") to change what the CT shows.
- An app appears in the list only after it has played sound since it started.
- A disconnected device shows "–" and works again as soon as it's plugged back in.

## Why this one shouldn't crash

- All calls to the Windows audio API (COM) run on **one dedicated thread**. Loupedeck calls the plugin from
  its own threads; there the plugin only reads cached values and never waits for Windows.
- Devices that disappear (USB headset, sleeping Bluetooth, remote desktop) make COM calls fail.
  Every failure is caught, the device handle is released and recreated on the next poll.
- No external libraries (e.g. NAudio) that can conflict with other plugins.

Log file if something goes wrong: `%LOCALAPPDATA%\Logi\LogiPluginService\PluginData\DeviceVolumeMixer\debug.log`.

## Files

```
src/Audio/CoreAudioInterop.cs   Windows Core Audio interfaces
src/Audio/AudioService.cs       audio thread, cache and all logic
src/Actions/*.cs                the four Loupedeck actions
src/package/metadata/           LoupedeckPackage.yaml + icon
```
