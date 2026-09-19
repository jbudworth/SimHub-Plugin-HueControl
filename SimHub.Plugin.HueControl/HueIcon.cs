using System;
using System.Windows.Media;

namespace SimHub.Plugin.HueControl
{
    /// <summary>
    /// SimHub sidebar icon for this plugin: the "hue:bridge-v2" icon from
    /// https://github.com/arallsopp/hass-hue-icons (CC BY-NC-SA 4.0, arallsopp).
    /// Rendered as a WPF vector Geometry rather than a bitmap, since the source
    /// icon is a plain 24x24 SVG path and this avoids pulling in an SVG rendering
    /// library just for one icon.
    /// </summary>
    internal static class HueIcon
    {
        // Path data copied verbatim from docs/svgs/bridge-v2.svg in the hass-hue-icons repo.
        private const string PathData =
            "M17.7,4.4c-0.53,0-0.95-0.42-0.95-0.95S17.18,2.5,17.7,2.5s0.95,0.42,0.95,0.95S18.23,4.4,17.7,4.4z " +
            "M12,8.2c2.1,0,3.8,1.7,3.8,3.8s-1.7,3.8-3.8,3.8S8.2,14.1,8.2,12S9.9,8.2,12,8.2z " +
            "M12,17.7c-3.15,0-5.7-2.56-5.7-5.7S8.85,6.3,12,6.3s5.7,2.56,5.7,5.7S15.15,17.7,12,17.7 " +
            "M5.35,3.45c0-0.53,0.42-0.95,0.95-0.95s0.95,0.42,0.95,0.95S6.82,4.4,6.3,4.4S5.35,3.97,5.35,3.45 " +
            "M12,2.5c0.53,0,0.95,0.42,0.95,0.95S12.53,4.4,12,4.4s-0.95-0.42-0.95-0.95S11.47,2.5,12,2.5 " +
            "M19.6,0.59H4.4c-2.09,0-3.8,1.71-3.8,3.8V19.6c0,2.09,1.71,3.8,3.8,3.8h15.2c2.09,0,3.8-1.71,3.8-3.8V4.4" +
            "C23.41,2.31,21.69,0.59,19.6,0.59";

        // Source SVG used fill="#44739e" (Home Assistant's default icon blue-grey).
        private static readonly Color FillColor = Color.FromRgb(0x44, 0x73, 0x9e);

        private static ImageSource _cached;

        public static ImageSource Image
        {
            get
            {
                if (_cached != null) return _cached;

                try
                {
                    var geometry = Geometry.Parse(PathData);
                    if (geometry is PathGeometry pathGeometry)
                    {
                        // SVG's default fill-rule is nonzero; WPF's Geometry.Parse defaults to
                        // EvenOdd, so set this explicitly to render the ring/dot cut-outs correctly.
                        pathGeometry.FillRule = FillRule.Nonzero;
                    }

                    var drawing = new GeometryDrawing(new SolidColorBrush(FillColor), null, geometry);
                    var image = new DrawingImage(drawing);
                    image.Freeze();
                    _cached = image;
                }
                catch (Exception ex)
                {
                    HuePlugin.Log($"Failed to build sidebar icon: {ex.Message}");
                    _cached = null;
                }

                return _cached;
            }
        }
    }
}
