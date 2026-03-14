using System.Text.Json.Serialization;

namespace BulkImageGenerator.Models
{
    /// <summary>
    /// All typographic properties for a text-type placeholder.
    /// Colors are stored as #AARRGGBB hex strings for JSON portability.
    /// </summary>
    public sealed class TextStyle
    {
        /// <summary>Font family name. Must be installed on the system.</summary>
        [JsonPropertyName("fontFamily")]
        public string FontFamily { get; set; } = "Arial";

        /// <summary>Font size in points (pt), as it will appear on the output image.</summary>
        [JsonPropertyName("fontSize")]
        public float FontSize { get; set; } = 24f;

        /// <summary>Text color as #AARRGGBB hex string (e.g. "#FFFF0000" = opaque red).</summary>
        [JsonPropertyName("color")]
        public string Color { get; set; } = "#FF000000";

        /// <summary>Whether the font is bold.</summary>
        [JsonPropertyName("bold")]
        public bool Bold { get; set; } = false;

        /// <summary>Whether the font is italic.</summary>
        [JsonPropertyName("italic")]
        public bool Italic { get; set; } = false;

        /// <summary>Horizontal alignment: "left", "center", or "right".</summary>
        [JsonPropertyName("alignment")]
        public string Alignment { get; set; } = "left";

        /// <summary>Vertical alignment: "top", "middle", or "bottom".</summary>
        [JsonPropertyName("verticalAlignment")]
        public string VerticalAlignment { get; set; } = "top";

        /// <summary>
        /// Whether long text should wrap within the bounding box.
        /// If false, text is clipped to a single line.
        /// </summary>
        [JsonPropertyName("wordWrap")]
        public bool WordWrap { get; set; } = true;

        /// <summary>Line height multiplier (1.0 = normal, 1.5 = 150% line spacing).</summary>
        [JsonPropertyName("lineHeight")]
        public float LineHeight { get; set; } = 1.2f;
    }
}
