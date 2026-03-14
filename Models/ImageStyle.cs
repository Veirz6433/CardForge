using System.Text.Json.Serialization;

namespace BulkImageGenerator.Models
{
    /// <summary>
    /// Controls how a source image asset is scaled and cropped to fill its bounding box.
    /// Mirrors the CSS background-size / object-fit mental model for familiarity.
    /// </summary>
    public sealed class ImageStyle
    {
        /// <summary>
        /// Fit mode for the image inside the bounding box:
        ///   "cover"   — scale proportionally, crop excess (fills the box completely).
        ///   "contain" — scale proportionally, letterbox (image fully visible, may have gaps).
        ///   "stretch" — distort image to exactly fill the box (no crop, no letterbox).
        /// </summary>
        [JsonPropertyName("fitMode")]
        public string FitMode { get; set; } = "cover";

        /// <summary>
        /// Horizontal anchor when cropping in "cover" mode.
        /// 0.0 = left, 0.5 = center, 1.0 = right.
        /// </summary>
        [JsonPropertyName("anchorX")]
        public float AnchorX { get; set; } = 0.5f;

        /// <summary>
        /// Vertical anchor when cropping in "cover" mode.
        /// 0.0 = top, 0.5 = center, 1.0 = bottom.
        /// </summary>
        [JsonPropertyName("anchorY")]
        public float AnchorY { get; set; } = 0.5f;

        /// <summary>
        /// Corner radius in pixels for rounded-rectangle clipping.
        /// 0 = no rounding (sharp corners).
        /// </summary>
        [JsonPropertyName("cornerRadius")]
        public float CornerRadius { get; set; } = 0f;
    }
}
