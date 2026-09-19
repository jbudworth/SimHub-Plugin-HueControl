using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimHub.Plugin.HueControl
{
    /// <summary>
    /// Thin wrapper around the Philips Hue Bridge local (CLIP v1) REST API.
    /// No external Hue SDK dependency is used, just plain HTTP + JSON, so this
    /// has no extra install requirements beyond Newtonsoft.Json.
    /// </summary>
    public class HueBridgeClient
    {
        private readonly HttpClient _http;

        public string BridgeIp { get; set; }
        public string ApiKey { get; set; }

        public HueBridgeClient(string bridgeIp, string apiKey)
        {
            BridgeIp = bridgeIp;
            ApiKey = apiKey;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        }

        private string BaseUrl => $"http://{BridgeIp}/api/{ApiKey}";

        // ---------- Discovery & pairing ----------

        /// <summary>Uses Philips' cloud discovery endpoint to find bridges on the local network.
        /// Requires internet access; if it fails, ask the user to enter the bridge IP manually
        /// (visible in their router's DHCP client list, or the Hue app under Settings > My Bridge).</summary>
        public static async Task<List<(string Id, string Ip)>> DiscoverBridgesAsync()
        {
            var result = new List<(string Id, string Ip)>();
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var resp = await http.GetStringAsync("https://discovery.meethue.com/");
                var arr = JArray.Parse(resp);
                foreach (var item in arr)
                {
                    result.Add((item["id"]?.ToString(), item["internalipaddress"]?.ToString()));
                }
            }
            return result;
        }

        /// <summary>
        /// Requests an API key ("username") from the bridge. The user must physically press
        /// the link button on the bridge within ~30 seconds before calling this.
        /// </summary>
        public static async Task<string> PairAsync(string bridgeIp)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var body = new JObject { ["devicetype"] = "simhub#hue-plugin" };
                var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                var resp = await http.PostAsync($"http://{bridgeIp}/api", content);
                var text = await resp.Content.ReadAsStringAsync();
                var arr = JArray.Parse(text);
                var first = arr[0];

                if (first["error"] != null)
                {
                    var desc = first["error"]?["description"]?.ToString() ?? "unknown error";
                    throw new InvalidOperationException($"Pairing failed: {desc}. Press the link button on the bridge and try again.");
                }

                return first["success"]?["username"]?.ToString();
            }
        }

        // ---------- Reading state ----------

        public async Task<List<HueDiscoveredLight>> GetLightsAsync()
        {
            var resp = await _http.GetStringAsync($"{BaseUrl}/lights");
            var obj = JObject.Parse(resp);
            var lights = new List<HueDiscoveredLight>();

            foreach (var prop in obj.Properties())
            {
                var state = prop.Value["state"];
                lights.Add(new HueDiscoveredLight
                {
                    Id = prop.Name,
                    Name = prop.Value["name"]?.ToString(),
                    On = state?["on"]?.ToObject<bool>() ?? false,
                    Brightness = state?["bri"]?.ToObject<int>() ?? 0
                });
            }

            return lights;
        }

        public async Task<HueDiscoveredLight> GetLightAsync(string lightId)
        {
            var resp = await _http.GetStringAsync($"{BaseUrl}/lights/{lightId}");
            var obj = JObject.Parse(resp);
            var state = obj["state"];
            return new HueDiscoveredLight
            {
                Id = lightId,
                Name = obj["name"]?.ToString(),
                On = state?["on"]?.ToObject<bool>() ?? false,
                Brightness = state?["bri"]?.ToObject<int>() ?? 0
            };
        }

        // ---------- Writing state ----------

        private async Task SetStateAsync(string lightId, JObject body)
        {
            var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}/lights/{lightId}/state") { Content = content };
            var resp = await _http.SendAsync(request);
            resp.EnsureSuccessStatusCode();
        }

        public Task SetPowerAsync(string lightId, bool on) =>
            SetStateAsync(lightId, new JObject { ["on"] = on });

        /// <summary>Brightness as a 0-100 percentage, converted internally to Hue's 1-254 range.</summary>
        public Task SetBrightnessPercentAsync(string lightId, int percent)
        {
            percent = Clamp(percent, 0, 100);
            if (percent <= 0)
            {
                return SetStateAsync(lightId, new JObject { ["on"] = false });
            }

            int bri = (int)Math.Round(1 + (percent / 100.0) * 253);
            return SetStateAsync(lightId, new JObject { ["on"] = true, ["bri"] = bri });
        }

        public Task SetColorHexAsync(string lightId, string hex)
        {
            var (r, g, b) = HexToRgb(hex);
            return SetColorRgbAsync(lightId, r, g, b);
        }

        public Task SetColorRgbAsync(string lightId, byte r, byte g, byte b)
        {
            var (hue, sat) = RgbToHueSat(r, g, b);
            return SetStateAsync(lightId, new JObject
            {
                ["on"] = true,
                ["hue"] = hue,
                ["sat"] = sat
            });
        }

        // ---------- Helpers ----------

        public static (byte R, byte G, byte B) HexToRgb(string hex)
        {
            hex = hex.TrimStart('#');
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return (r, g, b);
        }

        /// <summary>Converts 0-255 RGB into the Hue bridge's hue (0-65535) and sat (0-254) fields.</summary>
        public static (ushort Hue, byte Sat) RgbToHueSat(byte r, byte g, byte b)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            double hueDeg = 0;
            if (delta > 0.00001)
            {
                if (max == rd) hueDeg = 60 * (((gd - bd) / delta) % 6);
                else if (max == gd) hueDeg = 60 * (((bd - rd) / delta) + 2);
                else hueDeg = 60 * (((rd - gd) / delta) + 4);
            }
            if (hueDeg < 0) hueDeg += 360;

            double sat = max <= 0 ? 0 : delta / max;

            ushort hueVal = (ushort)Math.Round(hueDeg / 360.0 * 65535.0);
            byte satVal = (byte)Math.Round(sat * 254.0);
            return (hueVal, satVal);
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
    }
}
