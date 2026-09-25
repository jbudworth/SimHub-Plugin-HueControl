using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;

namespace SimHub.Plugin.HueControl
{
    [PluginDescription("Control Philips Hue lights (on/off/toggle/brightness/colour) from SimHub Controls and Events")]
    [PluginAuthor("Claude.ai")]
    [PluginName("Philips Hue Control")]
    public class HuePlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public PluginManager PluginManager { get; set; }
        public HueSettings Settings { get; private set; }

        internal HueBridgeClient Client { get; private set; }

        // Cached last-known state per light id, used by Toggle and by exposed properties.
        // Written from thread-pool tasks (polling, actions) and read from SimHub's own
        // threads, so these must be thread-safe.
        private readonly ConcurrentDictionary<string, bool> _onState = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, int> _briState = new ConcurrentDictionary<string, int>();

        // Enabled lights with their collision-free registered names, built once in Init and
        // shared by RegisterActions/RegisterProperties so both use identical names.
        private readonly List<(string LightId, string Name)> _registeredLights = new List<(string LightId, string Name)>();

        private readonly Stopwatch _pollStopwatch = new Stopwatch();

        // 1 while a RefreshStateAsync pass is in flight, so slow bridge responses
        // can't cause overlapping polls to pile up.
        private int _polling;

        public ImageSource PictureIcon => HueIcon.Image;
        public string LeftMenuTitle => "Hue Control";

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            Log("Initializing");

            Settings = this.ReadCommonSettings<HueSettings>("HueSettings", () => new HueSettings());
            DedupeSettings();
            Client = new HueBridgeClient(Settings.BridgeIp, Settings.ApiKey);

            BuildRegisteredLightNames();
            RegisterActions();
            RegisterProperties();

            _pollStopwatch.Start();

            // Seed initial state from the bridge so Toggle works correctly right away.
            _ = RefreshStateAsync();
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("HueSettings", Settings);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Called on every telemetry tick - throttle our bridge polling.
            if (_pollStopwatch.ElapsedMilliseconds >= Math.Max(1000, Settings.PollIntervalMs))
            {
                _pollStopwatch.Restart();
                _ = RefreshStateAsync();
            }
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        // ------------------------------------------------------------------
        // Action registration (this is what shows up in Controls and Events)
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves each enabled light to a unique sanitized name. Two lights whose names
        /// sanitize to the same string (e.g. "Rig Left" and "RigLeft") would otherwise
        /// register duplicate action/property names, leaving one light silently unusable.
        /// </summary>
        private void BuildRegisteredLightNames()
        {
            _registeredLights.Clear();
            var used = new HashSet<string>();
            foreach (var light in Settings.Lights)
            {
                if (!light.Enabled || string.IsNullOrWhiteSpace(light.Id)) continue;
                var name = MakeUnique(SanitizeName(light.Name), used);
                if (name != SanitizeName(light.Name))
                    Log($"Light '{light.Name}' (id {light.Id}) clashes with another light's action name, registered as '{name}' instead.");
                _registeredLights.Add((light.Id, name));
            }
        }

        private static string MakeUnique(string name, HashSet<string> used)
        {
            var candidate = name;
            int i = 2;
            while (!used.Add(candidate))
                candidate = $"{name}_{i++}";
            return candidate;
        }

        private void RegisterActions()
        {
            // Preset names can collide after sanitizing too ("Warm White" vs "WarmWhite").
            var usedPresetNames = new HashSet<string>();
            var presets = new List<(string Name, string Hex)>();
            foreach (var preset in Settings.ColorPresets)
            {
                var presetName = MakeUnique(SanitizeName(preset.Name), usedPresetNames);
                presets.Add((presetName, preset.Hex));
            }

            foreach (var (lightId, name) in _registeredLights)
            {
                PluginManager.AddAction($"TurnOn.{name}", GetType(), (a, b) => Fire(() => Client.SetPowerAsync(lightId, true)));
                PluginManager.AddAction($"TurnOff.{name}", GetType(), (a, b) => Fire(() => Client.SetPowerAsync(lightId, false)));
                PluginManager.AddAction($"Toggle.{name}", GetType(), (a, b) =>
                {
                    bool currentlyOn = _onState.TryGetValue(lightId, out var s) && s;
                    Fire(() => Client.SetPowerAsync(lightId, !currentlyOn));
                    _onState[lightId] = !currentlyOn;
                });

                PluginManager.AddAction($"BrightnessUp.{name}", GetType(), (a, b) => StepBrightness(lightId, Settings.BrightnessStep));
                PluginManager.AddAction($"BrightnessDown.{name}", GetType(), (a, b) => StepBrightness(lightId, -Settings.BrightnessStep));

                // Discrete brightness presets, since we're not using an analog axis mapping here.
                foreach (var percent in new[] { 1, 25, 50, 75, 100 })
                {
                    var p = percent;
                    PluginManager.AddAction($"SetBrightness{p}.{name}", GetType(), (a, b) => Fire(() => Client.SetBrightnessPercentAsync(lightId, p)));
                }

                // One button-style action per configured colour preset, e.g. SetColor.RigLeft.Red
                foreach (var (presetName, hex) in presets)
                {
                    var h = hex;
                    PluginManager.AddAction($"SetColor.{name}.{presetName}", GetType(), (a, b) => Fire(() => Client.SetColorHexAsync(lightId, h)));
                }
            }
        }

        private void StepBrightness(string lightId, int deltaPercent)
        {
            int current = _briState.TryGetValue(lightId, out var b) ? BriToPercent(b) : 50;
            int next = Math.Max(0, Math.Min(100, current + deltaPercent));
            Fire(() => Client.SetBrightnessPercentAsync(lightId, next));
        }

        private static int BriToPercent(int bri) => (int)Math.Round((bri - 1) / 253.0 * 100.0);

        // ------------------------------------------------------------------
        // Exposed read-only properties (visible in SimHub's property list / dash templates)
        // ------------------------------------------------------------------

        private void RegisterProperties()
        {
            foreach (var (lightId, name) in _registeredLights)
            {
                PluginManager.AttachDelegate($"{name}.On", GetType(), () => _onState.TryGetValue(lightId, out var s) && s);
                PluginManager.AttachDelegate($"{name}.BrightnessPercent", GetType(), () => _briState.TryGetValue(lightId, out var b) ? BriToPercent(b) : 0);
            }
        }

        private async Task RefreshStateAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.BridgeIp) || string.IsNullOrWhiteSpace(Settings.ApiKey))
                return;

            // Skip this pass if the previous one is still running (e.g. bridge offline and
            // every light is waiting out the HTTP timeout), rather than piling up overlapping
            // polls that spam the log and can finish out of order.
            if (Interlocked.Exchange(ref _polling, 1) == 1)
                return;

            try
            {
                foreach (var light in Settings.Lights)
                {
                    if (!light.Enabled || string.IsNullOrWhiteSpace(light.Id)) continue;
                    try
                    {
                        var state = await Client.GetLightAsync(light.Id);
                        _onState[light.Id] = state.On;
                        _briState[light.Id] = state.Brightness;
                    }
                    catch (Exception ex)
                    {
                        Log($"Poll failed for light {light.Id}: {ex.Message}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        // ------------------------------------------------------------------
        // Misc helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// One-time cleanup for settings files that already picked up duplicate lights/colour
        /// presets from the list-append bug (see the JsonProperty attributes in HueModels.cs).
        /// Keeps first occurrence order, drops later duplicates, and re-saves if anything changed.
        /// </summary>
        private void DedupeSettings()
        {
            bool changed = false;

            var seenLightIds = new HashSet<string>();
            var dedupedLights = new List<HueLightConfig>();
            foreach (var light in Settings.Lights)
            {
                if (seenLightIds.Add(light.Id))
                    dedupedLights.Add(light);
                else
                    changed = true;
            }

            var seenPresets = new HashSet<string>();
            var dedupedPresets = new List<HueColorPreset>();
            foreach (var preset in Settings.ColorPresets)
            {
                var key = $"{preset.Name}|{preset.Hex}";
                if (seenPresets.Add(key))
                    dedupedPresets.Add(preset);
                else
                    changed = true;
            }

            if (changed)
            {
                Settings.Lights = dedupedLights;
                Settings.ColorPresets = dedupedPresets;
                Log("Removed duplicate lights/colour presets left over from a previous version, saving cleaned-up settings.");
                SaveSettings();
            }
        }

        /// <summary>Fire-and-forget wrapper for the async Hue calls, since SimHub actions are synchronous void.</summary>
        internal static void Fire(Func<Task> action)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    Log($"Action failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Logs to SimHub's own log file via SimHub.Logging.dll (SimHub.Logging.Current),
        /// which ships as a separate assembly from SimHub.Plugins.dll on current SimHub builds.
        /// </summary>
        internal static void Log(string message)
        {
            SimHub.Logging.Current.Info($"[HuePlugin] {message}");
        }

        internal static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Light";
            var chars = Array.FindAll(name.ToCharArray(), c => char.IsLetterOrDigit(c) || c == '_');
            var clean = new string(chars);
            return clean.Length == 0 ? "Light" : clean;
        }

        internal void SaveSettings()
        {
            this.SaveCommonSettings("HueSettings", Settings);
        }
    }
}
