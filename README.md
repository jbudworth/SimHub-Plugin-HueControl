# SimHub Philips Hue Plugin

Lets you control Philips Hue lights directly from SimHub's **Controls and Events** screen: turn a light on, off, toggle it, step or set its brightness, and switch it to a preset colour. It also exposes a couple of live properties (on/off, brightness) that can be used in dashboards.

This talks straight to your Hue Bridge over your local network (the CLIP v1 REST API), so there is no cloud dependency once it is paired.

Logging goes through `SimHub.Logging.Current.Info(...)`, backed by `SimHub.Logging.dll` and `log4net.dll` (on current SimHub builds, logging was split out of `SimHub.Plugins.dll` into its own assembly, so both DLLs need to be referenced and their `HintPath`s pointed at your install folder).

## What you get

For every light you enable in the settings screen (named, say, `RigLeft`), these actions appear under Controls and Events:

- `TurnOn.RigLeft`
- `TurnOff.RigLeft`
- `Toggle.RigLeft`
- `BrightnessUp.RigLeft` / `BrightnessDown.RigLeft` (step size configurable, good for repeat-fire buttons)
- `SetBrightness1.RigLeft`, `SetBrightness25.RigLeft`, `SetBrightness50.RigLeft`, `SetBrightness75.RigLeft`, `SetBrightness100.RigLeft`
- `SetColor.RigLeft.Red`, `SetColor.RigLeft.Green`, ... one per colour preset you define

And two read-only properties per light: `RigLeft.On`, `RigLeft.BrightnessPercent`.

Note: an earlier version of this plugin also tried to add continuous analog mappings (bind a slider/rotary axis straight to brightness or an RGB channel) via `AddInputMapping`. That call's exact overload varies between SimHub SDK versions, and guessing it wrong caused build errors, so it has been dropped in favour of the discrete brightness/colour buttons above, which are guaranteed to compile. If you want analog control back, open `C:\Program Files (x86)\SimHub\PluginSdk\User.PluginSdkDemo` in your own SimHub install, find how it calls `AddInputMapping`, and send me that signature so it can be wired back in properly.

## Project layout

```
SimHub.Plugin.HueControl.slnx        solution file, open this in Visual Studio
SimHub.Plugin.HueControl/
  SimHub.Plugin.HueControl.csproj
  HuePlugin.cs             main plugin, action registration
  HueBridgeClient.cs       Hue Bridge HTTP client (discovery, pairing, on/off/brightness/colour)
  HueModels.cs             settings + data models
  SettingsControl.xaml(.cs) settings screen (bridge pairing, light list, colour presets)
  Properties/AssemblyInfo.cs
```

## Build steps

1. Install Visual Studio 2022 (Community is fine) with the ".NET desktop development" workload.
2. Open `SimHub.Plugin.HueControl.slnx` (double-click, or `File > Open > Project/Solution` in Visual Studio). This is the solution file; it references the single project below.
3. Edit the `<Reference>` `HintPath` entries near the bottom of the `.csproj` so they point at your actual SimHub folder (default `C:\Program Files (x86)\SimHub`). You need:
   - `SimHub.Plugins.dll`
   - `GameReaderCommon.dll`
   - `SimHub.Logging.dll`
   - `log4net.dll`
4. Check `PlatformTarget` in the `.csproj`: SimHub usually runs as a 32-bit process, so `x86` (already set) is normally correct. If your SimHub install is 64-bit, change it to `x64`.
5. Build (`Ctrl+Shift+B`). NuGet will pull in `Newtonsoft.Json` automatically.
6. Copy the build output - `SimHub.Plugin.HueControl.dll` and `Newtonsoft.Json.dll` from `bin\x86\Debug\net48\` (or `Release`) - into your SimHub install folder.
7. Restart SimHub. Go to **Settings > Plugins**, tick "Philips Hue Control" if it isn't already enabled, restart again if prompted.

## First-time setup in SimHub

1. Open the plugin's settings page (left menu, "Hue Control").
2. Click **Discover** to auto-find your bridge's IP (needs internet access for Philips' discovery service), or type it in manually - you can find it in the Hue app under Settings > My Bridge, or in your router's connected-devices list.
3. Press the physical link button on top of the Hue bridge, then within ~30 seconds click **Pair**. This fetches an API key.
4. Click **Save settings**.
5. Click **Load lights from bridge**. Every light on your bridge appears in the table.
6. Tick **Enabled** for the lights you want to control, and adjust the "Action name" if you want cleaner names in Controls and Events (letters/numbers/underscore only, no spaces).
7. Edit the colour preset list if you want different presets to the defaults (Red, Green, Blue, Yellow, Warm White, Purple).
8. Click **Save settings**, then **restart SimHub** - actions are registered at startup, so newly enabled lights or new presets only show up after a restart.

## Wiring it up in Controls and Events

1. Go to **Controls and Events** in SimHub.
2. Pick the trigger you want (keyboard key, wheel button, arcade panel button, etc.).
3. In the action list, search for your light's name, e.g. type `RigLeft` to see all its actions.
4. Assign `Toggle.RigLeft` to a button for a simple on/off switch, or `SetColor.RigLeft.Red` to a button that snaps it to red, etc.

## Exporting and importing configuration

The top of the settings screen has **Export** and **Import** buttons. This covers the plugin's settings other than the API key (see below): bridge IP, the enabled lights list with their action names, colour presets, brightness step, and poll interval.

- **Export** writes the current settings (including any unsaved edits on screen) to a `.json` file you choose. The bridge IP, enabled lights list with action names, colour presets, brightness step, and poll interval are included; the **API key is deliberately left out**, so the file is safe to back up or share without exposing a bridge credential.
- **Import** reads a `.json` file back in, overwrites the current settings with it (except the API key, which is left untouched), saves, and refreshes the screen. You'll need to restart SimHub afterwards for any changed light/action list to show up in Controls and Events.

This is meant for moving your setup to another SimHub install, or just keeping a backup. One thing worth knowing:

- Since the API key isn't exported, after importing you'll need to click **Discover** (or enter the IP manually) and **Pair** again, even on the same network, before lights respond.

## Notes and possible extensions

- Brightness and colour are sent as separate Hue Bridge fields (`bri` and `hue`/`sat`), so setting a colour preset doesn't change brightness, and vice versa, matching how the Hue app behaves.
- State is polled from the bridge every few seconds (configurable) so `Toggle` and the exposed properties stay accurate even if a light is also being controlled from the Hue app or a physical switch. If you want tighter sync, lower "Poll interval (ms)" in settings, at the cost of a few more bridge requests per second.
- This uses the Hue Bridge's local CLIP v1 API (`hue`/`sat`/`bri`), which is the simplest reliable option for bridges. If you specifically need CT (colour temperature) control or entertainment-area streaming, that would need extending `HueBridgeClient` with a `ct` field or the v2 API, happy to add that if you want it.
- If you have more than a handful of lights, the settings grid may get a bit long. Nothing stops you from just enabling the ones you actually plan to bind buttons to.

## License

MIT, see [LICENSE](LICENSE).
