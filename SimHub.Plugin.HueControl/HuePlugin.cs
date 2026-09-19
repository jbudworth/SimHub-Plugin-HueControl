using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Dictionary<string, bool> _onState = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> _briState = new Dictionary<string, int>();

        private readonly Stopwatch _pollStopwatch = new Stopwatch();

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

        private void RegisterActions()
        {
            foreach (var light in Settings.Lights)
            {
                if (!light.Enabled || string.IsNullOrWhiteSpace(light.Id)) continue;
                var lightId = light.Id;
                var name = SanitizeName(light.Name);

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
                foreach (var preset in Settings.ColorPresets)
                {
                    var hex = preset.Hex;
                    var presetName = SanitizeName(preset.Name);
                    PluginManager.AddAction($"SetColor.{name}.{presetName}", GetType(), (a, b) => Fire(() => Client.SetColorHexAsync(lightId, hex)));
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
            foreach (var light in Settings.Lights)
            {
                if (!light.Enabled) continue;
                var lightId = light.Id;
                var name = SanitizeName(light.Name);

                PluginManager.AttachDelegate($"{name}.On", GetType(), () => _onState.TryGetValue(lightId, out var s) && s);
                PluginManager.AttachDelegate($"{name}.BrightnessPercent", GetType(), () => _briState.TryGetValue(lightId, out var b) ? BriToPercent(b) : 0);
            }
        }

        private async Task RefreshStateAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.BridgeIp) || string.IsNullOrWhiteSpace(Settings.ApiKey))
                return;

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

        // ------------------------------------------------------------------
        // Misc helpers
        // ------------------------------------------------------------------

        /// <summary>Fire-and-forget wrapper for the async Hue calls, since SimHub actions are synchronous void.</summary>
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
