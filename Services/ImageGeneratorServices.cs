using BulkImageGenerator.Models;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BulkImageGenerator.Services
{
    /// <summary>
    /// Core bulk image generation engine.
    ///
    /// THREADING MODEL:
    ///   - Background image bytes loaded ONCE into shared byte[].
    ///   - Asset images cached in ConcurrentDictionary — each unique file
    ///     read from disk exactly once, all threads reuse cached bytes.
    ///   - Each thread decodes its own private SKBitmap — SKBitmap is NOT thread-safe.
    ///
    /// MEMORY MANAGEMENT:
    ///   - Every SKBitmap, SKSurface, SKPaint, SKTypeface in a `using` block.
    ///   - RAM stays flat even at 10,000+ images.
    /// </summary>
    public sealed class ImageGeneratorService
    {
        // ── Shared state (read-only after PrepareAsync) ────────────────────────

        /// <summary>
        /// Raw encoded bytes of the background image.
        /// Shared across all threads. Each thread decodes its own SKBitmap from this.
        /// </summary>
        private byte[]? _backgroundImageBytes;

        /// <summary>The loaded template configuration.</summary>
        private Template? _template;

        /// <summary>
        /// Thread-safe cache of image asset bytes keyed by resolved file path.
        /// Each unique image file is read from disk exactly once.
        /// All parallel threads reuse the cached bytes — eliminates file lock conflicts.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte[]> _imageCache
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Max parallel threads. Defaults to (CPU count - 1) to keep UI responsive.
        /// Each active thread holds one decoded SKBitmap in memory (~width*height*4 bytes).
        /// </summary>
        private readonly int _maxDegreeOfParallelism;

        // ── Constructor ────────────────────────────────────────────────────────

        public ImageGeneratorService(int? maxDegreeOfParallelism = null)
        {
            _maxDegreeOfParallelism = maxDegreeOfParallelism
                ?? Math.Max(1, Environment.ProcessorCount - 1);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Step 1: Call ONCE before GenerateAllAsync.
        /// Loads background image into memory, validates it, clears the image cache.
        /// </summary>
        public Task PrepareAsync(Template template)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));

            // Clear cached assets from any previous generation run.
            _imageCache.Clear();

            if (!File.Exists(template.BackgroundImagePath))
                throw new FileNotFoundException(
                    $"Background image not found: {template.BackgroundImagePath}",
                    template.BackgroundImagePath);

            // Read all background bytes into managed memory once.
            _backgroundImageBytes = File.ReadAllBytes(template.BackgroundImagePath);

            // Probe-decode to catch corrupt files before the batch starts.
            using var probe = SKBitmap.Decode(_backgroundImageBytes);
            if (probe is null)
                throw new InvalidOperationException(
                    $"SkiaSharp could not decode background image: {template.BackgroundImagePath}\n" +
                    "Ensure it is a valid JPG or PNG file.");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Step 2: Runs the parallel bulk generation batch.
        ///
        /// Each row dictionary maps Excel column headers to cell values:
        ///   { "name": "Alice", "photo": "alice.jpg" }
        ///
        /// Image columns should contain either:
        ///   - A bare filename ("alice.jpg") — resolved via AssetsFolder in MainViewModel
        ///   - A full absolute path ("C:\assets\alice.jpg")
        ///
        /// Progress callbacks are marshaled to the UI thread automatically via
        /// IProgress — no Dispatcher.Invoke needed anywhere.
        /// </summary>
        public async Task GenerateAllAsync(
            IEnumerable<Dictionary<string, string>> rows,
            string outputDirectory,
            string? fileNameColumn = null,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_template is null || _backgroundImageBytes is null)
                throw new InvalidOperationException(
                    "Call PrepareAsync() before GenerateAllAsync().");

            Directory.CreateDirectory(outputDirectory);

            var rowList   = rows as List<Dictionary<string, string>> ?? rows.ToList();
            int total     = rowList.Count;
            int completed = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxDegreeOfParallelism,
                CancellationToken      = cancellationToken
            };

            // Task.Run prevents blocking the UI thread for the entire batch duration.
            await Task.Run(() =>
            {
                Parallel.ForEach(rowList, parallelOptions, (row, _, rowIndex) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string fileName  = DeriveFileName(row, fileNameColumn, (int)rowIndex);
                    string extension = _template.OutputFormat
                        .Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                    string outputPath = Path.Combine(outputDirectory, fileName + extension);

                    GenerateSingleImage(row, outputPath);

                    // Interlocked.Increment is lock-free — safe across all threads.
                    int current = Interlocked.Increment(ref completed);
                    progress?.Report((current, total));
                });

            }, cancellationToken);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PRIVATE: SINGLE IMAGE RENDERER
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders one complete image for a single Excel row and saves it to disk.
        /// Called concurrently on thread-pool threads.
        /// All SkiaSharp resources are fully disposed before this method returns.
        /// </summary>
        private void GenerateSingleImage(
            Dictionary<string, string> row,
            string outputPath)
        {
            // Each thread gets its own decoded background bitmap.
            // Decoding from a shared byte[] is a pure read operation — thread safe.
            using var backgroundBitmap = SKBitmap.Decode(_backgroundImageBytes);

            // CPU raster surface — no GPU required, fully thread safe.
            using var surface = SKSurface.Create(new SKImageInfo(
                _template!.CanvasWidth,
                _template.CanvasHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));

            if (surface is null)
                throw new InvalidOperationException(
                    "SkiaSharp could not create a rendering surface. " +
                    "Check that CanvasWidth and CanvasHeight are greater than zero.");

            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.White);

            // ── Draw background ────────────────────────────────────────────────
            using (var bgPaint = new SKPaint
            {
                IsAntialias   = true,
                FilterQuality = SKFilterQuality.High
            })
            {
                canvas.DrawBitmap(
                    backgroundBitmap,
                    new SKRect(0, 0, _template.CanvasWidth, _template.CanvasHeight),
                    bgPaint);
            }

            // ── Draw each placeholder ──────────────────────────────────────────
            foreach (var placeholder in _template.Placeholders)
            {
                // Skip placeholders with no matching data in this row.
                if (!row.TryGetValue(placeholder.VariableName, out string? value)
                    || string.IsNullOrWhiteSpace(value))
                    continue;

                if (placeholder.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
                    DrawTextPlaceholder(canvas, placeholder, value);

                else if (placeholder.Type.Equals("image", StringComparison.OrdinalIgnoreCase))
                    DrawImagePlaceholder(canvas, placeholder, value);
            }

            // ── Encode and save to disk ────────────────────────────────────────
            using var image = surface.Snapshot();
            using var encodedData = _template.OutputFormat
                .Equals("png", StringComparison.OrdinalIgnoreCase)
                    ? image.Encode(SKEncodedImageFormat.Png, 100)
                    : image.Encode(SKEncodedImageFormat.Jpeg, _template.OutputQuality);

            // FileShare.None is fine here — each output file has a unique name.
            using var fileStream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            encodedData.SaveTo(fileStream);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PRIVATE: TEXT RENDERING
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draws a text value inside a placeholder bounding box.
        /// Handles font loading, word wrap, multi-line layout,
        /// and horizontal + vertical alignment.
        /// </summary>
        private static void DrawTextPlaceholder(
            SKCanvas canvas,
            Placeholder placeholder,
            string text)
        {
            var style  = placeholder.TextStyle ?? new TextStyle();
            var bounds = placeholder.Bounds;

            SKColor textColor = ParseHexColor(style.Color);

            var fontStyle = (style.Bold, style.Italic) switch
            {
                (true,  true)  => SKFontStyle.BoldItalic,
                (true,  false) => SKFontStyle.Bold,
                (false, true)  => SKFontStyle.Italic,
                _              => SKFontStyle.Normal
            };

            // Falls back to system default font if family not installed.
            using var typeface = SKTypeface.FromFamilyName(style.FontFamily, fontStyle)
                                 ?? SKTypeface.Default;

            using var paint = new SKPaint
            {
                Typeface     = typeface,
                TextSize     = style.FontSize,
                Color        = textColor,
                IsAntialias  = true,
                SubpixelText = true,
            };

            // Clip canvas to bounding box — text cannot overflow onto other elements.
            canvas.Save();
            canvas.ClipRect(new SKRect(
                bounds.X,
                bounds.Y,
                bounds.X + bounds.Width,
                bounds.Y + bounds.Height));

            if (style.WordWrap)
                DrawWrappedText(canvas, paint, style, bounds, text);
            else
                DrawSingleLineText(canvas, paint, style, bounds, text);

            canvas.Restore();
        }

        /// <summary>
        /// Breaks text into word-wrapped lines and draws the full block
        /// with horizontal and vertical alignment applied.
        /// </summary>
        private static void DrawWrappedText(
            SKCanvas canvas,
            SKPaint paint,
            TextStyle style,
            Bounds bounds,
            string text)
        {
            float lineHeight       = style.FontSize * style.LineHeight;
            var   lines            = WrapText(text, bounds.Width, paint);
            float totalBlockHeight = lines.Count * lineHeight;

            float blockStartY = style.VerticalAlignment switch
            {
                "middle" => bounds.Y + (bounds.Height - totalBlockHeight) / 2f,
                "bottom" => bounds.Y +  bounds.Height - totalBlockHeight,
                _        => bounds.Y   // "top"
            };

            // SkiaSharp DrawText Y = baseline. Offset by -Ascent to get cap-height start.
            paint.GetFontMetrics(out SKFontMetrics metrics);
            float baselineOffset = -metrics.Ascent;

            foreach (var line in lines)
            {
                float lineWidth = paint.MeasureText(line);

                float x = style.Alignment switch
                {
                    "center" => bounds.X + (bounds.Width - lineWidth) / 2f,
                    "right"  => bounds.X +  bounds.Width - lineWidth,
                    _        => bounds.X   // "left"
                };

                canvas.DrawText(line, x, blockStartY + baselineOffset, paint);
                blockStartY += lineHeight;
            }
        }

        /// <summary>
        /// Draws text as a single non-wrapping line aligned within the bounding box.
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
                "bottom" => bounds.Y +  bounds.Height - (-metrics.Descent),
                _        => bounds.Y + (-metrics.Ascent)   // "top"
            };

            float lineWidth = paint.MeasureText(text);
            float x = style.Alignment switch
            {
                "center" => bounds.X + (bounds.Width - lineWidth) / 2f,
                "right"  => bounds.X +  bounds.Width - lineWidth,
                _        => bounds.X
            };

            canvas.DrawText(text, x, y, paint);
        }

        /// <summary>
        /// Greedy word-wrap: adds words one by one, breaking to a new line
        /// only when the next word would exceed the available width.
        /// </summary>
        private static List<string> WrapText(
            string text,
            float maxWidth,
            SKPaint paint)
        {
            var lines   = new List<string>();
            var words   = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new StringBuilder();

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

        // ══════════════════════════════════════════════════════════════════════
        // PRIVATE: IMAGE RENDERING
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Loads an image asset and draws it into the placeholder bounds.
        ///
        /// KEY FIX: Uses ConcurrentDictionary cache so each unique image file
        /// is read from disk exactly once regardless of how many parallel threads
        /// need it. Eliminates "file in use" errors during Parallel.ForEach.
        ///
        /// FileStream opened with FileShare.Read so multiple threads can
        /// safely read the same file simultaneously if cache is cold.
        /// </summary>
        private void DrawImagePlaceholder(
            SKCanvas canvas,
            Placeholder placeholder,
            string imagePath)
        {
            string? resolvedPath = ResolveImagePath(imagePath);

            if (resolvedPath is null)
            {
                DrawErrorBox(canvas, placeholder.Bounds,
                    $"Not found: {Path.GetFileName(imagePath)}");
                return;
            }

            // GetOrAdd is atomic — if two threads request the same file at the
            // same time, only one performs the disk read. Others wait and reuse.
            byte[] imageBytes;
            try
            {
                imageBytes = _imageCache.GetOrAdd(resolvedPath, path =>
                {
                    // FileShare.Read allows concurrent reads of the same file.
                    using var fs = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    return ms.ToArray();
                });
            }
            catch (Exception ex)
            {
                DrawErrorBox(canvas, placeholder.Bounds, $"Read error: {ex.Message}");
                return;
            }

            // Each thread decodes its own SKBitmap from the shared cached bytes.
            // SKBitmap.Decode on a byte[] is a pure read — fully thread safe.
            using var assetBitmap = SKBitmap.Decode(imageBytes);
            if (assetBitmap is null)
            {
                DrawErrorBox(canvas, placeholder.Bounds,
                    $"Decode failed: {Path.GetFileName(resolvedPath)}");
                return;
            }

            var style    = placeholder.ImageStyle ?? new ImageStyle();
            var bounds   = placeholder.Bounds;
            var destRect = new SKRect(
                bounds.X,
                bounds.Y,
                bounds.X + bounds.Width,
                bounds.Y + bounds.Height);

            SKRect srcRect = CalculateSourceRect(assetBitmap, bounds, style);

            canvas.Save();

            if (style.CornerRadius > 0)
            {
                using var clipPath = new SKPath();
                clipPath.AddRoundRect(destRect, style.CornerRadius, style.CornerRadius);
                canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
            }

            using var imgPaint = new SKPaint
            {
                IsAntialias   = true,
                FilterQuality = SKFilterQuality.High
            };

            canvas.DrawBitmap(assetBitmap, srcRect, destRect, imgPaint);
            canvas.Restore();
        }

        /// <summary>
        /// Resolves an image path using three strategies:
        ///   1. Exact path as given
        ///   2. Append common extensions if no extension present
        ///   3. Case-insensitive filename search in the same directory
        /// Returns null if the file cannot be found by any strategy.
        /// </summary>
        private static string? ResolveImagePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return null;

            // Strategy 1: exact path
            if (File.Exists(rawPath)) return rawPath;

            // Strategy 2: try common image extensions if no extension given
            if (string.IsNullOrEmpty(Path.GetExtension(rawPath)))
            {
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif" })
                {
                    string candidate = rawPath + ext;
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // Strategy 3: case-insensitive search in parent directory
            string? dir  = Path.GetDirectoryName(rawPath);
            string  file = Path.GetFileName(rawPath);

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                string? match = Directory
                    .GetFiles(dir)
                    .FirstOrDefault(f => string.Equals(
                        Path.GetFileName(f), file,
                        StringComparison.OrdinalIgnoreCase));

                if (match is not null) return match;
            }

            return null;
        }

        /// <summary>
        /// Calculates the source crop rectangle for cover / contain / stretch modes.
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
                "stretch" => new SKRect(0, 0, srcW, srcH),
                "contain" => new SKRect(0, 0, srcW, srcH),
                _         => ComputeCoverSrcRect(srcW, srcH, dstW, dstH, style)
            };
        }

        /// <summary>
        /// Cover mode: crops the source so it fills the destination completely,
        /// anchored by AnchorX/AnchorY (0.5 = center crop by default).
        /// </summary>
        private static SKRect ComputeCoverSrcRect(
            float srcW, float srcH,
            float dstW, float dstH,
            ImageStyle style)
        {
            float srcAspect = srcW / srcH;
            float dstAspect = dstW / dstH;

            float cropW, cropH;

            if (srcAspect > dstAspect)
            {
                cropH = srcH;
                cropW = srcH * dstAspect;
            }
            else
            {
                cropW = srcW;
                cropH = srcW / dstAspect;
            }

            float cropX = (srcW - cropW) * style.AnchorX;
            float cropY = (srcH - cropH) * style.AnchorY;

            return new SKRect(cropX, cropY, cropX + cropW, cropY + cropH);
        }

        /// <summary>
        /// Draws a visible red error box so broken images are immediately obvious
        /// in the output instead of silently producing a blank area.
        /// </summary>
        private static void DrawErrorBox(SKCanvas canvas, Bounds bounds, string message)
        {
            var rect = new SKRect(
                bounds.X, bounds.Y,
                bounds.X + bounds.Width,
                bounds.Y + bounds.Height);

            using var fillPaint = new SKPaint
            {
                Color = new SKColor(220, 50, 50, 100),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(rect, fillPaint);

            using var borderPaint = new SKPaint
            {
                Color       = SKColors.Red,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 2f,
                IsAntialias = true
            };
            canvas.DrawRect(rect, borderPaint);

            using var textPaint = new SKPaint
            {
                Color       = SKColors.White,
                TextSize    = Math.Min(14f, bounds.Height * 0.25f),
                IsAntialias = true
            };
            canvas.DrawText(
                $"! {message}",
                bounds.X + 6f,
                bounds.Y + textPaint.TextSize + 4f,
                textPaint);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PRIVATE: UTILITIES
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Parses a hex color string to SKColor.
        /// Supports #RGB, #RRGGBB, #AARRGGBB. Falls back to black on failure.
        /// </summary>
        private static SKColor ParseHexColor(string hex)
        {
            try
            {
                string clean = hex.TrimStart('#');
                return clean.Length switch
                {
                    3 => new SKColor(
                        (byte)(Convert.ToByte(clean[0..1], 16) * 17),
                        (byte)(Convert.ToByte(clean[1..2], 16) * 17),
                        (byte)(Convert.ToByte(clean[2..3], 16) * 17)),

                    6 => new SKColor(
                        Convert.ToByte(clean[0..2], 16),
                        Convert.ToByte(clean[2..4], 16),
                        Convert.ToByte(clean[4..6], 16)),

                    8 => new SKColor(
                        Convert.ToByte(clean[2..4], 16),
                        Convert.ToByte(clean[4..6], 16),
                        Convert.ToByte(clean[6..8], 16),
                        Convert.ToByte(clean[0..2], 16)),

                    _ => SKColors.Black
                };
            }
            catch
            {
                return SKColors.Black;
            }
        }

        /// <summary>
        /// Derives output filename from the designated column or falls back
        /// to a zero-padded row index. Sanitizes invalid Windows filename characters.
        /// </summary>
        private static string DeriveFileName(
            Dictionary<string, string> row,
            string? fileNameColumn,
            int rowIndex)
        {
            if (fileNameColumn is not null
                && row.TryGetValue(fileNameColumn, out string? nameVal)
                && !string.IsNullOrWhiteSpace(nameVal))
            {
                return string.Join("_", nameVal.Split(Path.GetInvalidFileNameChars()));
            }

            return rowIndex.ToString("D6");
        }
    }
}
