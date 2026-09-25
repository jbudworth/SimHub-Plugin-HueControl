using System.Collections.Generic;
using Newtonsoft.Json;

namespace SimHub.Plugin.HueControl
{
    /// <summary>
    /// One Hue light the user has chosen to expose to SimHub Controls and Events.
    /// </summary>
    public class HueLightConfig
    {
        /// <summary>The numeric light id as reported by the bridge (e.g. "1", "2"...).</summary>
        public string Id { get; set; }

        /// <summary>Friendly name used to build action names, e.g. "RigLeft" -> "TurnOn.RigLeft".
        /// Keep it free of spaces/dots for cleaner action names.</summary>
        public string Name { get; set; }

        /// <summary>The light's own name as reported by the bridge, persisted so the settings
        /// screen can show which physical light a row maps to without re-querying the bridge.</summary>
        public string BridgeName { get; set; }

        public bool Enabled { get; set; } = true;
    }

    /// <summary>A named colour preset, editable in the plugin settings screen.</summary>
    public class HueColorPreset
    {
        public string Name { get; set; }

        /// <summary>Hex colour, e.g. "#FF0000".</summary>
        public string Hex { get; set; }

        public HueColorPreset() { }

        public HueColorPreset(string name, string hex)
        {
            Name = name;
            Hex = hex;
        }
    }

    /// <summary>Plugin settings, persisted by SimHub via ReadCommonSettings/SaveCommonSettings.</summary>
    public class HueSettings
    {
        public string BridgeIp { get; set; } = "";
        public string ApiKey { get; set; } = "";

        // ObjectCreationHandling.Replace is required here: these lists have default values
        // (below), and if the settings loader deserializes saved JSON onto an already-constructed
        // HueSettings instance (rather than creating a fresh one), Json.NET's default behaviour
        // for a non-null collection is to APPEND the saved items onto the existing default ones,
        // not replace them, doubling the list on every load. This forces a clean replace instead.
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<HueLightConfig> Lights { get; set; } = new List<HueLightConfig>();

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<HueColorPreset> ColorPresets { get; set; } = new List<HueColorPreset>
        {
            new HueColorPreset("Red", "#FF0000"),
            new HueColorPreset("Green", "#00FF00"),
            new HueColorPreset("Blue", "#0000FF"),
            new HueColorPreset("Yellow", "#FFFF00"),
            new HueColorPreset("Warm White", "#FFE29A"),
            new HueColorPreset("Purple", "#8A2BE2"),
        };

        /// <summary>Brightness step, in percent, applied by the BrightnessUp/Down actions.</summary>
        public int BrightnessStep { get; set; } = 10;

        /// <summary>How often (ms) to poll the bridge to resync on/off + brightness state.</summary>
        public int PollIntervalMs { get; set; } = 4000;
    }

    /// <summary>Export/import DTO for the settings screen's Export/Import buttons.
    /// Deliberately excludes ApiKey: it's a bridge credential and must never be written
    /// to a file the user might share/back up. After importing, the user re-pairs to get
    /// a fresh key for whichever bridge is reachable on their network.</summary>
    public class HueSettingsExport
    {
        public string BridgeIp { get; set; } = "";

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<HueLightConfig> Lights { get; set; } = new List<HueLightConfig>();

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<HueColorPreset> ColorPresets { get; set; } = new List<HueColorPreset>();

        public int BrightnessStep { get; set; } = 10;
        public int PollIntervalMs { get; set; } = 4000;
    }

    /// <summary>Light info as read back from the bridge when populating the settings screen.</summary>
    public class HueDiscoveredLight
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool On { get; set; }
        public int Brightness { get; set; }
    }
}
