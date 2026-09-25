using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace SimHub.Plugin.HueControl
{
    public partial class SettingsControl : UserControl
    {
        private readonly HuePlugin _plugin;

        // Row model used by the Lights grid (adds a read-only BridgeName column
        // on top of the persisted HueLightConfig).
        public class LightRow
        {
            public string Id { get; set; }
            public string BridgeName { get; set; }
            public string Name { get; set; }
            public bool Enabled { get; set; }
        }

        public ObservableCollection<LightRow> LightRows { get; } = new ObservableCollection<LightRow>();
        public ObservableCollection<HueColorPreset> PresetRows { get; } = new ObservableCollection<HueColorPreset>();

        public SettingsControl(HuePlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            LightsGrid.ItemsSource = LightRows;
            PresetsGrid.ItemsSource = PresetRows;

            LoadFromSettings(_plugin.Settings);
        }

        /// <summary>Populates every field on screen from a HueSettings instance.
        /// Used both on initial load and after a successful import.</summary>
        private void LoadFromSettings(HueSettings settings)
        {
            BridgeIpBox.Text = settings.BridgeIp;
            ApiKeyBox.Text = settings.ApiKey;
            BrightnessStepBox.Text = settings.BrightnessStep.ToString();
            PollIntervalBox.Text = settings.PollIntervalMs.ToString();

            LightRows.Clear();
            foreach (var l in settings.Lights ?? new System.Collections.Generic.List<HueLightConfig>())
                LightRows.Add(new LightRow { Id = l.Id, Name = l.Name, Enabled = l.Enabled, BridgeName = l.BridgeName });

            PresetRows.Clear();
            foreach (var p in settings.ColorPresets ?? new System.Collections.Generic.List<HueColorPreset>())
                PresetRows.Add(p);
        }

        private async void DiscoverButton_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Searching for bridges...";
            try
            {
                var bridges = await HueBridgeClient.DiscoverBridgesAsync();
                if (bridges.Count == 0)
                {
                    StatusText.Text = "No bridges found automatically. Enter the bridge IP manually (check your router, or the Hue app under Settings > My Bridge).";
                    return;
                }
                BridgeIpBox.Text = bridges[0].Ip;
                StatusText.Text = bridges.Count == 1
                    ? $"Found bridge at {bridges[0].Ip}."
                    : $"Found {bridges.Count} bridges, using the first one ({bridges[0].Ip}). Edit the field if that's the wrong one.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Discovery failed (needs internet access): " + ex.Message + ". Enter the bridge IP manually instead.";
            }
        }

        private async void PairButton_Click(object sender, RoutedEventArgs e)
        {
            var ip = BridgeIpBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                StatusText.Text = "Enter the bridge IP first.";
                return;
            }

            StatusText.Text = "Press the physical button on the Hue bridge now, then wait...";
            try
            {
                var key = await HueBridgeClient.PairAsync(ip);
                ApiKeyBox.Text = key;
                StatusText.Text = "Paired successfully. Click 'Save settings', then 'Load lights from bridge'.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Pairing failed: " + ex.Message;
            }
        }

        private async void LoadLightsButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyBasicFieldsToPlugin();
            _plugin.Client.BridgeIp = _plugin.Settings.BridgeIp;
            _plugin.Client.ApiKey = _plugin.Settings.ApiKey;

            StatusText.Text = "Loading lights...";
            try
            {
                var lights = await _plugin.Client.GetLightsAsync();
                foreach (var l in lights)
                {
                    var existing = LightRows.FirstOrDefault(r => r.Id == l.Id);
                    if (existing == null)
                    {
                        LightRows.Add(new LightRow
                        {
                            Id = l.Id,
                            BridgeName = l.Name,
                            Name = HuePlugin.SanitizeName(l.Name),
                            Enabled = false
                        });
                    }
                    else
                    {
                        existing.BridgeName = l.Name;
                    }
                }
                LightsGrid.Items.Refresh();
                StatusText.Text = $"Loaded {lights.Count} light(s) from the bridge. Tick 'Enabled' for the ones you want, then Save.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not load lights: " + ex.Message;
            }
        }

        private void ApplyBasicFieldsToPlugin()
        {
            _plugin.Settings.BridgeIp = BridgeIpBox.Text?.Trim();
            _plugin.Settings.ApiKey = ApiKeyBox.Text?.Trim();

            if (int.TryParse(BrightnessStepBox.Text, out var step)) _plugin.Settings.BrightnessStep = step;
            if (int.TryParse(PollIntervalBox.Text, out var poll)) _plugin.Settings.PollIntervalMs = poll;
        }

        /// <summary>Copies the Lights and Color Presets grids back into _plugin.Settings.
        /// Shared by Save and Export, so an export always reflects unsaved on-screen edits too.
        /// Returns the names of presets that were skipped because their hex colour is invalid.</summary>
        private System.Collections.Generic.List<string> SyncGridsToPlugin()
        {
            _plugin.Settings.Lights.Clear();
            foreach (var row in LightRows)
            {
                _plugin.Settings.Lights.Add(new HueLightConfig
                {
                    Id = row.Id,
                    Name = HuePlugin.SanitizeName(row.Name),
                    BridgeName = row.BridgeName,
                    Enabled = row.Enabled
                });
            }

            var invalidPresets = new System.Collections.Generic.List<string>();
            _plugin.Settings.ColorPresets.Clear();
            foreach (var p in PresetRows)
            {
                if (string.IsNullOrWhiteSpace(p.Name) && string.IsNullOrWhiteSpace(p.Hex))
                    continue;

                if (HueBridgeClient.TryNormalizeHex(p.Hex, out var normalized))
                {
                    p.Hex = normalized;
                    _plugin.Settings.ColorPresets.Add(p);
                }
                else
                {
                    invalidPresets.Add(string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name);
                }
            }
            PresetsGrid.Items.Refresh();
            return invalidPresets;
        }

        private static string InvalidPresetsWarning(System.Collections.Generic.List<string> invalidPresets) =>
            invalidPresets.Count == 0
                ? ""
                : $" Skipped preset(s) with invalid hex colour (expected #RRGGBB): {string.Join(", ", invalidPresets)}.";

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyBasicFieldsToPlugin();
            var invalidPresets = SyncGridsToPlugin();

            _plugin.SaveSettings();
            StatusText.Text = "Saved. Restart SimHub for new/changed light or preset actions to appear in Controls and Events."
                + InvalidPresetsWarning(invalidPresets);
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyBasicFieldsToPlugin();
            var invalidPresets = SyncGridsToPlugin();

            var dialog = new SaveFileDialog
            {
                Title = "Export Hue plugin configuration",
                Filter = "JSON configuration (*.json)|*.json",
                FileName = "SimHub.Plugin.HueControl.json"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var export = new HueSettingsExport
                {
                    BridgeIp = _plugin.Settings.BridgeIp,
                    Lights = _plugin.Settings.Lights,
                    ColorPresets = _plugin.Settings.ColorPresets,
                    BrightnessStep = _plugin.Settings.BrightnessStep,
                    PollIntervalMs = _plugin.Settings.PollIntervalMs
                };
                var json = JsonConvert.SerializeObject(export, Formatting.Indented);
                File.WriteAllText(dialog.FileName, json);
                StatusText.Text = $"Exported to {dialog.FileName}. The API key is not included (it's a credential); you'll need to re-Pair after importing this on another install."
                    + InvalidPresetsWarning(invalidPresets);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Export failed: " + ex.Message;
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import Hue plugin configuration",
                Filter = "JSON configuration (*.json)|*.json"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var imported = JsonConvert.DeserializeObject<HueSettingsExport>(json);
                if (imported == null)
                    throw new InvalidOperationException("File did not contain a recognisable configuration.");

                // Exported files never contain the API key (see HueSettingsExport), so the
                // existing key is left untouched here; the user re-Pairs afterwards.
                _plugin.Settings.BridgeIp = imported.BridgeIp;
                _plugin.Settings.BrightnessStep = imported.BrightnessStep;
                _plugin.Settings.PollIntervalMs = imported.PollIntervalMs;
                _plugin.Settings.Lights = imported.Lights ?? new System.Collections.Generic.List<HueLightConfig>();
                _plugin.Settings.ColorPresets = imported.ColorPresets ?? new System.Collections.Generic.List<HueColorPreset>();

                LoadFromSettings(_plugin.Settings);
                _plugin.SaveSettings();

                StatusText.Text = "Imported and saved. The API key was not part of the export, click 'Discover' (or enter the bridge IP manually) and 'Pair' to reconnect. Restart SimHub for the light/action list to take effect.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Import failed: " + ex.Message;
            }
        }
    }
}
