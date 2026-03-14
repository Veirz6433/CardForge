using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BulkImageGenerator.Models
{
    /// <summary>
    /// Root template configuration object. Serializes to/from a .bigt (JSON) file.
    /// Describes the background image dimensions, all placeholders, and output settings.
    ///
    /// Example JSON on disk:
    /// {
    ///   "name": "Employee Badge",
    ///   "backgroundImagePath": "C:/Assets/badge_bg.png",
    ///   "canvasWidth": 800,
    ///   "canvasHeight": 600,
    ///   "outputFormat": "jpeg",
    ///   "outputQuality": 92,
    ///   "placeholders": [
    ///     {
    ///       "id": "a1b2c3",
    ///       "variableName": "name",
    ///       "type": "text",
    ///       "bounds": { "x": 100, "y": 200, "width": 300, "height": 50 },
    ///       "textStyle": { "fontFamily": "Arial", "fontSize": 28, "color": "#FF000000", ... }
    ///     },
    ///     {
    ///       "id": "d4e5f6",
    ///       "variableName": "photo",
    ///       "type": "image",
    ///       "bounds": { "x": 30, "y": 30, "width": 120, "height": 120 },
    ///       "imageStyle": { "fitMode": "cover", "anchorX": 0.5, "anchorY": 0.5, "cornerRadius": 60 }
    ///     }
    ///   ]
    /// }
    /// </summary>
    public sealed class Template
    {
        /// <summary>Human-readable template name shown in the UI title bar.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Untitled Template";

        /// <summary>
        /// Absolute path to the background image.
        /// Tip: store relative paths ("./bg.png") for portable template files.
        /// </summary>
        [JsonPropertyName("backgroundImagePath")]
        public string BackgroundImagePath { get; set; } = string.Empty;

        /// <summary>Output image width in pixels. Must match background image width for best results.</summary>
        [JsonPropertyName("canvasWidth")]
        public int CanvasWidth { get; set; } = 1200;

        /// <summary>Output image height in pixels.</summary>
        [JsonPropertyName("canvasHeight")]
        public int CanvasHeight { get; set; } = 630;

        /// <summary>Output format: "jpeg" or "png".</summary>
        [JsonPropertyName("outputFormat")]
        public string OutputFormat { get; set; } = "jpeg";

        /// <summary>
        /// JPEG quality (1–100). Only used when OutputFormat == "jpeg".
        /// 92 is a good balance of quality vs. file size for print/web.
        /// </summary>
        [JsonPropertyName("outputQuality")]
        public int OutputQuality { get; set; } = 92;

        /// <summary>All variable placeholders defined for this template.</summary>
        [JsonPropertyName("placeholders")]
        public List<Placeholder> Placeholders { get; set; } = new();
    }
}
