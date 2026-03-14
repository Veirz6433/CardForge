using System.Text.Json.Serialization;

namespace BulkImageGenerator.Models
{
    /// <summary>
    /// Represents the axis-aligned bounding box for a placeholder,
    /// expressed in pixels relative to the top-left of the template canvas.
    /// All values are in the coordinate space of the OUTPUT image (not the UI canvas).
    /// </summary>
    public sealed class Bounds
    {
        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("width")]
        public float Width { get; set; }

        [JsonPropertyName("height")]
        public float Height { get; set; }

        /// <summary>Convenience factory for inline construction.</summary>
        public static Bounds Create(float x, float y, float width, float height)
            => new() { X = x, Y = y, Width = width, Height = height };
    }
}
