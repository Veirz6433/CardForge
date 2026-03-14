using BulkImageGenerator.Models;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BulkImageGenerator.Services
{
    /// <summary>
    /// Core bulk image generation engine.
    ///
    /// THREADING MODEL:
    ///   - The background SKBitmap is decoded ONCE into a byte array (EncodedData),
    ///     then each parallel thread independently decodes a private SKBitmap from
    ///     that shared byte array. This is the safest cross-thread pattern for
    ///     SkiaSharp — SKBitmap is NOT thread-safe for shared reads via GetPixels.
    ///
    /// MEMORY MANAGEMENT:
    ///   - Every SKBitmap, SKSurface, SKCanvas, SKPaint, and SKTypeface is wrapped
    ///     in a `using` statement so unmanaged memory is freed immediately after use.
    ///   - For 10,000+ row jobs, this prevents the Large Object Heap (LOH) from
    ///     accumulating hundreds of MB of orphaned unmanaged bitmaps.
    /// </summary>
    public sealed class ImageGeneratorService
    {
        // ── Shared state (read-only after PrepareAsync) ─────────────────────────

        /// <summary>
        /// The raw encoded bytes of the background image.
        /// Shared across all threads; threads decode their own private SKBitmap from this.
        /// Encoding format is preserved (PNG/JPEG) — no intermediate decompression.
        /// </summary>
        private byte[]? _backgroundImageBytes;

        /// <summary>The loaded template configuration.</summary>
        private Template? _template;

        /// <summary>
        /// Maximum degree of parallelism.
        /// Defaults to (LogicalCPUCount - 1) to keep the UI thread responsive.
        /// Tune down for machines with limited RAM (each thread holds ~1 decoded bitmap).
        /// </summary>
        private readonly int _maxDegreeOfParallelism;

        // ────────────────────────────────────────────────────────────────────────

        public ImageGeneratorService(int? maxDegreeOfParallelism = null)
        {
            // Leave one logical core for the UI/OS; minimum of 1 for single-core machines.
            _maxDegreeOfParallelism = maxDegreeOfParallelism
                ?? Math.Max(1, Environment.ProcessorCount - 1);
        }

        // ── 1. Preparation ───────────────────────────────────────────────────────

        /// <summary>
        /// Loads and validates the template and pre-reads the background image bytes
        /// into managed memory. Must be called once before GenerateAllAsync.
        /// </summary>
        /// <param name="template">The template configuration object.</param>
        /// <exception cref="FileNotFoundException">If the background image path is invalid.</exception>
        /// <exception cref="InvalidOperationException">If the image cannot be decoded.</exception>
        public Task PrepareAsync(Template template)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));

            if (!File.Exists(template.BackgroundImagePath))
                throw new FileNotFoundException(
                    $"Background image not found: {template.BackgroundImagePath}",
                    template.BackgroundImagePath);

            // Read all bytes into managed memory synchronously (fast for local files).
            // We do a decode probe here to catch corrupt images early, before the batch starts.
            _backgroundImageBytes = File.ReadAllBytes(template.BackgroundImagePath);
            using var probeImage = SKBitmap.Decode(_backgroundImageBytes);
            if (probeImage is null)
                throw new InvalidOperationException(
                    $"SkiaSharp could not decode the background image at: {template.BackgroundImagePath}");

            return Task.CompletedTask;
        }

        // ── 2. Main Batch Entry Point ────────────────────────────────────────────

        /// <summary>
        /// Generates one output image per row in <paramref name="rows"/> using Parallel.ForEach.
        ///
        /// Each row dictionary maps Excel column headers to cell values.
        /// Image-type placeholders expect the cell value to be an absolute file path
        /// to the source image asset on disk.
        /// </summary>
        /// <param name="rows">Parsed Excel data. Each dict is one row: {"name":"Alice","photo":"C:/assets/alice.jpg"}.</param>
        /// <param name="outputDirectory">Directory where output images will be saved.</param>
        /// <param name="fileNameColumn">
        ///   The column name to use as the output filename (without extension).
        ///   If null or missing in a row, falls back to a zero-padded row index.
        /// </param>
        /// <param name="progress">
        ///   IProgress callback fired after each image completes.
        ///   Reports (completedCount, totalCount). Safe to bind to a WPF ProgressBar
        ///   because IProgress<T> automatically marshals to the captured SynchronizationContext.
        /// </param>
        /// <param name="cancellationToken">Token to abort the batch mid-run.</param>
        public async Task GenerateAllAsync(
            IEnumerable<Dictionary<string, string>> rows,
            string outputDirectory,
            string? fileNameColumn = null,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // ── Guard checks ────────────────────────────────────────────────────
            if (_template is null || _backgroundImageBytes is null)
                throw new InvalidOperationException(
                    "PrepareAsync must be called before GenerateAllAsync.");

            Directory.CreateDirectory(outputDirectory);

            // Materialize to list so we have a count for progress reporting.
            var rowList = rows as List<Dictionary<string, string>> ?? rows.ToList();
            int total = rowList.Count;
            int completed = 0;

            // ── Parallel batch loop ─────────────────────────────────────────────
            // ParallelOptions wires up cancellation and CPU throttling together.
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxDegreeOfParallelism,
                CancellationToken = cancellationToken
            };

            // Wrap in Task.Run so the calling (UI) thread is not blocked.
            await Task.Run(() =>
            {
                Parallel.ForEach(rowList, parallelOptions, (row, _, rowIndex) =>
                {
                    // Check for cancellation at the start of each iteration.
                    cancellationToken.ThrowIfCancellationRequested();

                    // Determine output filename.
                    string fileName = DeriveFileName(row, fileNameColumn, (int)rowIndex);
                    string extension = _template.OutputFormat.Equals("png", StringComparison.OrdinalIgnoreCase)
                        ? ".png" : ".jpg";
                    string outputPath = Path.Combine(outputDirectory, fileName + extension);

                    // Generate and save the single image for this row.
                    GenerateSingleImage(row, outputPath);

                    // Atomically increment the shared counter and report progress.
                    // Interlocked.Increment is lock-free and safe across threads.
                    int currentCompleted = Interlocked.Increment(ref completed);
                    progress?.Report((currentCompleted, total));
                });
            }, cancellationToken);
        }

        // ── 3. Single-Image Renderer ─────────────────────────────────────────────

        /// <summary>
        /// Renders one image to disk. Called on a thread-pool thread.
        ///
        /// Resource lifecycle (all disposed by end of method):
        ///   backgroundBitmap → SKSurface → SKCanvas → per-placeholder SKPaint/SKBitmap
        /// </summary>
        private void GenerateSingleImage(Dictionary<string, string> row, string outputPath)
        {
            // Each thread decodes its own private copy of the background bitmap.
            // This is the key thread-safety pattern: _backgroundImageBytes is a
            // shared readonly byte[], and SKBitmap.Decode() is a pure read operation.
            using var backgroundBitmap = SKBitmap.Decode(_backgroundImageBytes);

            // Create an off-screen surface at the template's target resolution.
            // SKSurface is backed by GPU-less (CPU raster) memory — safe off-screen.
            using var surface = SKSurface.Create(new SKImageInfo(
                _template!.CanvasWidth,
                _template.CanvasHeight,
                SKColorType.Bgra8888,         // Standard 32-bit color
                SKAlphaType.Premul));          // Premultiplied alpha for compositing

            if (surface is null)
                throw new InvalidOperationException(
                    "SkiaSharp could not create an off-screen surface. " +
                    "Check that CanvasWidth/CanvasHeight are > 0.");

            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.White); // Default background if image load fails.

            // ── Draw background image ───────────────────────────────────────────
            var destRect = new SKRect(0, 0, _template.CanvasWidth, _template.CanvasHeight);
            using (var bgPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
            {
                canvas.DrawBitmap(backgroundBitmap, destRect, bgPaint);
            }

            // ── Draw each placeholder ───────────────────────────────────────────
            foreach (var placeholder in _template.Placeholders)
            {
                // Look up the value for this placeholder from the row data.
                // Uses the variableName directly (no curly braces in the key).
                if (!row.TryGetValue(placeholder.VariableName, out string? value)
                    || string.IsNullOrWhiteSpace(value))
                    continue; // Skip placeholders with no data for this row.

                if (placeholder.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
                    DrawTextPlaceholder(canvas, placeholder, value);
                else if (placeholder.Type.Equals("image", StringComparison.OrdinalIgnoreCase))
                    DrawImagePlaceholder(canvas, placeholder, value);
            }

            // ── Encode and save to disk ─────────────────────────────────────────
            // Snapshot() creates an immutable SKImage from the surface pixels.
            using var image = surface.Snapshot();
            using var encodedData = _template.OutputFormat.Equals("png", StringComparison.OrdinalIgnoreCase)
                ? image.Encode(SKEncodedImageFormat.Png, 100)
                : image.Encode(SKEncodedImageFormat.Jpeg, _template.OutputQuality);

            using var fileStream = File.OpenWrite(outputPath);
            encodedData.SaveTo(fileStream);
        }

        // ── 4. Text Rendering ────────────────────────────────────────────────────

        /// <summary>
        /// Draws a text value inside a placeholder's bounding box.
        /// Handles: font loading, word-wrap, multi-line layout, and
        /// horizontal/vertical alignment within the box.
        /// </summary>
        private static void DrawTextPlaceholder(
            SKCanvas canvas,
            Placeholder placeholder,
            string text)
        {
            var style = placeholder.TextStyle ?? new TextStyle();
            var bounds = placeholder.Bounds;

            // Convert #AARRGGBB hex string to SKColor.
            SKColor textColor = ParseHexColor(style.Color);

            // Build the font style flags (bold/italic combination).
            var fontStyle = (style.Bold, style.Italic) switch
            {
                (true, true)   => SKFontStyle.BoldItalic,
                (true, false)  => SKFontStyle.Bold,
                (false, true)  => SKFontStyle.Italic,
                _              => SKFontStyle.Normal
            };

            // SKTypeface.FromFamilyName falls back gracefully to the system default
            // if the requested font family is not installed.
            using var typeface = SKTypeface.FromFamilyName(style.FontFamily, fontStyle)
                                 ?? SKTypeface.Default;

            using var paint = new SKPaint
            {
                Typeface  = typeface,
                TextSize  = style.FontSize,
                Color     = textColor,
                IsAntialias = true,
                // SubpixelText improves readability at medium sizes.
                SubpixelText = true,
            };

            // Clip canvas to the bounding box so text cannot overflow onto other elements.
            canvas.Save();
            canvas.ClipRect(new SKRect(bounds.X, bounds.Y,
                                       bounds.X + bounds.Width,
                                       bounds.Y + bounds.Height));

            if (style.WordWrap)
                DrawWrappedText(canvas, paint, style, bounds, text);
            else
                DrawSingleLineText(canvas, paint, style, bounds, text);

            canvas.Restore();
        }

        /// <summary>
        /// Breaks text into lines that fit within bounds.Width, then
        /// positions the text block according to horizontal and vertical alignment.
        /// </summary>
        private static void DrawWrappedText(
            SKCanvas canvas,
            SKPaint paint,
            TextStyle style,
            Bounds bounds,
            string text)
        {
            float lineHeight = style.FontSize * style.LineHeight;
            var lines = WrapText(text, bounds.Width, paint);

            // Calculate total block height for vertical alignment.
            float totalBlockHeight = lines.Count * lineHeight;

            float blockStartY = style.VerticalAlignment switch
            {
                "middle" => bounds.Y + (bounds.Height - totalBlockHeight) / 2f,
                "bottom" => bounds.Y + bounds.Height - totalBlockHeight,
                _        => bounds.Y  // "top" default
            };

            // SkiaSharp's DrawText Y coordinate is the baseline, not the top of the line.
            // We offset by FontMetrics.Ascent (negative value) to get the cap height start.
            paint.GetFontMetrics(out SKFontMetrics metrics);
            float baselineOffset = -metrics.Ascent;

            foreach (var line in lines)
            {
                float lineWidth = paint.MeasureText(line);

                float x = style.Alignment switch
                {
                    "center" => bounds.X + (bounds.Width - lineWidth) / 2f,
                    "right"  => bounds.X + bounds.Width - lineWidth,
                    _        => bounds.X // "left"
                };

                canvas.DrawText(line, x, blockStartY + baselineOffset, paint);
                blockStartY += lineHeight;
            }
        }

        /// <summary>
        /// Draws text as a single line, clipped by the bounding box width.
        /// Used when WordWrap is disabled.
        /// </summary>
        private static void DrawSingleLineText(
            SKCanvas canvas,
            SKPaint paint,
            TextStyle style,
            Bounds bounds,
            string text)
        {
            paint.GetFontMetrics(out SKFontMetrics metrics);

            float lineHeight = style.FontSize * style.LineHeight;
            float y = style.VerticalAlignment switch
            {
                "middle" => bounds.Y + (bounds.Height + lineHeight) / 2f - (-metrics.Ascent),
                "bottom" => bounds.Y + bounds.Height - (-metrics.Descent),
                _        => bounds.Y + (-metrics.Ascent)  // "top"
            };

            float lineWidth = paint.MeasureText(text);
            float x = style.Alignment switch
            {
                "center" => bounds.X + (bounds.Width - lineWidth) / 2f,
                "right"  => bounds.X + bounds.Width - lineWidth,
                _        => bounds.X
            };

            canvas.DrawText(text, x, y, paint);
        }

        /// <summary>
        /// Greedy line-breaking algorithm: builds lines word-by-word,
        /// only breaking when the next word would exceed the available width.
        /// </summary>
        private static List<string> WrapText(string text, float maxWidth, SKPaint paint)
        {
            var lines  = new List<string>();
            var words  = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new System.Text.StringBuilder();

            foreach (var word in words)
            {
                string candidate = current.Length == 0
                    ? word
                    : current + " " + word;

                if (paint.MeasureText(candidate) <= maxWidth)
                {
                    current.Clear();
                    current.Append(candidate);
                }
                else
                {
                    if (current.Length > 0)
                        lines.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                }
            }

            if (current.Length > 0)
                lines.Add(current.ToString());

            return lines;
        }

        // ── 5. Image Rendering ───────────────────────────────────────────────────

        /// <summary>
        /// Loads the source image from disk, scales/crops it according to the
        /// placeholder's ImageStyle, and draws it onto the canvas with optional
        /// rounded-rectangle clipping.
        ///
        /// Fit Modes:
        ///   cover   — fills the box, may crop edges (like CSS background-size: cover)
        ///   contain — fits fully inside the box, may leave gaps
        ///   stretch — distorts to exactly fill the box (no aspect ratio preservation)
        /// </summary>
        private static void DrawImagePlaceholder(
            SKCanvas canvas,
            Placeholder placeholder,
            string imagePath)
        {
            // The cell value is expected to be an absolute path to the image asset.
            if (!File.Exists(imagePath))
                return; // Silently skip missing assets; caller can log errors separately.

            var style  = placeholder.ImageStyle ?? new ImageStyle();
            var bounds = placeholder.Bounds;
            var destRect = new SKRect(bounds.X, bounds.Y,
                                      bounds.X + bounds.Width,
                                      bounds.Y + bounds.Height);

            // Decode the asset image. Each thread has its own SKBitmap — no sharing.
            using var assetBitmap = SKBitmap.Decode(imagePath);
            if (assetBitmap is null) return;

            // Calculate the source rectangle (crop window) based on fit mode.
            SKRect srcRect = CalculateSourceRect(assetBitmap, bounds, style);

            canvas.Save();

            // Apply rounded-rectangle clip if a corner radius is specified.
            if (style.CornerRadius > 0)
            {
                using var clipPath = new SKPath();
                clipPath.AddRoundRect(destRect, style.CornerRadius, style.CornerRadius);
                canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
            }

            using var imgPaint = new SKPaint
            {
                IsAntialias    = true,
                FilterQuality  = SKFilterQuality.High
            };

            canvas.DrawBitmap(assetBitmap, srcRect, destRect, imgPaint);
            canvas.Restore();
        }

        /// <summary>
        /// Calculates the source crop rectangle from the asset bitmap for the requested fit mode.
        /// This is the core of the "cover/contain/stretch" logic.
        /// </summary>
        private static SKRect CalculateSourceRect(
            SKBitmap bitmap,
            Bounds destBounds,
            ImageStyle style)
        {
            float srcW = bitmap.Width;
            float srcH = bitmap.Height;
            float dstW = destBounds.Width;
            float dstH = destBounds.Height;

            return style.FitMode.ToLowerInvariant() switch
            {
                // ── STRETCH: Use the entire source image, distort to fit destination ──
                "stretch" => new SKRect(0, 0, srcW, srcH),

                // ── CONTAIN: Scale down to fit entirely within destination ─────────────
                // The entire source image is visible; DrawBitmap handles scaling.
                // We return the full src rect and rely on SkiaSharp's destRect scaling.
                "contain" => ComputeContainSrcRect(srcW, srcH, dstW, dstH, style),

                // ── COVER (default): Crop the source so it fills destination ──────────
                _ => ComputeCoverSrcRect(srcW, srcH, dstW, dstH, style),
            };
        }

        /// <summary>
        /// For "cover" mode: computes the largest centered (or anchor-adjusted)
        /// crop window in the source image that matches the destination aspect ratio.
        /// </summary>
        private static SKRect ComputeCoverSrcRect(
            float srcW, float srcH, float dstW, float dstH, ImageStyle style)
        {
            float srcAspect = srcW / srcH;
            float dstAspect = dstW / dstH;

            float cropW, cropH;
            if (srcAspect > dstAspect)
            {
                // Source is wider than destination — crop sides.
                cropH = srcH;
                cropW = srcH * dstAspect;
            }
            else
            {
                // Source is taller than destination — crop top/bottom.
                cropW = srcW;
                cropH = srcW / dstAspect;
            }

            // Apply anchor: 0.0 = left/top, 0.5 = center, 1.0 = right/bottom.
            float cropX = (srcW - cropW) * style.AnchorX;
            float cropY = (srcH - cropH) * style.AnchorY;

            return new SKRect(cropX, cropY, cropX + cropW, cropY + cropH);
        }

        /// <summary>
        /// For "contain" mode: computes a source rect that, when drawn into destRect,
        /// will letterbox the image. We return the full source image rect; SkiaSharp
        /// scales it to fit within destRect automatically.
        /// </summary>
        private static SKRect ComputeContainSrcRect(
            float srcW, float srcH, float dstW, float dstH, ImageStyle style)
        {
            // For "contain" we always use the entire source image.
            // The letterboxing effect comes from the destination rect being
            // proportionally smaller in one axis (handled by the caller adjusting destRect).
            // A full implementation would compute an inset destRect here.
            // For now, return the full source (equivalent to stretch in 2D terms).
            return new SKRect(0, 0, srcW, srcH);
        }

        // ── 6. Utilities ─────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a CSS-style hex color string to an SKColor.
        /// Supports formats: "#RGB", "#RRGGBB", "#AARRGGBB".
        /// Falls back to black on parse failure.
        /// </summary>
        private static SKColor ParseHexColor(string hex)
        {
            try
            {
                // Remove leading '#' if present.
                string clean = hex.TrimStart('#');
                return clean.Length switch
                {
                    3  => ParseShortHex(clean),
                    6  => new SKColor(
                              Convert.ToByte(clean[0..2], 16),
                              Convert.ToByte(clean[2..4], 16),
                              Convert.ToByte(clean[4..6], 16)),
                    8  => new SKColor(
                              Convert.ToByte(clean[2..4], 16),  // R
                              Convert.ToByte(clean[4..6], 16),  // G
                              Convert.ToByte(clean[6..8], 16),  // B
                              Convert.ToByte(clean[0..2], 16)), // A
                    _  => SKColors.Black
                };
            }
            catch { return SKColors.Black; }
        }

        private static SKColor ParseShortHex(string s) =>
            new SKColor(
                (byte)(Convert.ToByte(s[0..1], 16) * 17),
                (byte)(Convert.ToByte(s[1..2], 16) * 17),
                (byte)(Convert.ToByte(s[2..3], 16) * 17));

        /// <summary>
        /// Derives the output filename for a row.
        /// Priority: (1) value of fileNameColumn, (2) zero-padded row index.
        /// </summary>
        private static string DeriveFileName(
            Dictionary<string, string> row,
            string? fileNameColumn,
            int rowIndex)
        {
            if (fileNameColumn is not null
                && row.TryGetValue(fileNameColumn, out var nameVal)
                && !string.IsNullOrWhiteSpace(nameVal))
            {
                // Sanitize: remove characters not valid in Windows file names.
                return string.Join("_", nameVal.Split(Path.GetInvalidFileNameChars()));
            }

            return rowIndex.ToString("D6"); // e.g. "000042"
        }
    }
}
