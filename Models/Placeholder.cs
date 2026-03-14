using System.Text.Json.Serialization;

namespace BulkImageGenerator.Models
{
    /// <summary>
    /// Discriminated union type for a single variable placeholder drawn on the template.
    /// A placeholder maps to exactly one column in the Excel file via its VariableName.
    /// </summary>
    public sealed class Placeholder
    {
        /// <summary>
        /// Unique identifier for this placeholder (used internally for selection tracking).
        /// Generated as a new GUID when placeholder is created in the editor.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// The variable name that maps to an Excel column header.
        /// Convention: stored WITHOUT curly braces (e.g. "name", not "{name}").
        /// </summary>
        [JsonPropertyName("variableName")]
        public string VariableName { get; set; } = string.Empty;

        /// <summary>Discriminator: "text" or "image".</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        /// <summary>Position and size on the output canvas (pixels).</summary>
        [JsonPropertyName("bounds")]
        public Bounds Bounds { get; set; } = new();

        /// <summary>
        /// Text rendering settings. Populated when Type == "text".
        /// Null when Type == "image" to keep JSON clean.
        /// </summary>
        [JsonPropertyName("textStyle")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextStyle? TextStyle { get; set; }

        /// <summary>
        /// Image rendering settings. Populated when Type == "image".
        /// Null when Type == "text".
        /// </summary>
        [JsonPropertyName("imageStyle")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ImageStyle? ImageStyle { get; set; }
    }
}
