using BulkImageGenerator.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;

namespace BulkImageGenerator.ViewModels
{
    /// <summary>
    /// Observable wrapper around a Placeholder model.
    ///
    /// The WPF Canvas binds Left/Top/Width/Height for real-time drag+resize.
    /// The Properties Panel binds all style fields flat (not nested) for easy
    /// two-way binding without sub-ViewModel complexity.
    ///
    /// IMPORTANT: Do NOT override OnPropertyChanged(PropertyChangedEventArgs) directly
    /// when using CommunityToolkit source generators — use the generated
    /// partial void On[PropertyName]Changed(T value) hooks instead [web:38].
    /// </summary>
    public sealed partial class PlaceholderViewModel : ObservableObject
    {
        // ─── Backing model (source of truth for serialization) ─────────────────
        public Placeholder Model { get; }

        // ─── Identity ──────────────────────────────────────────────────────────
        public string Id => Model.Id;

        // ─── Canvas position & size ────────────────────────────────────────────
        // These four drive Canvas.Left, Canvas.Top, Width, Height in XAML.
        // The partial On...Changed hooks keep the derived offset properties
        // in sync without overriding OnPropertyChanged (which breaks the Toolkit).

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WidthOffset))]
        [NotifyPropertyChangedFor(nameof(HalfWidthOffset))]
        [NotifyPropertyChangedFor(nameof(DisplayLabel))]
        private double _width;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HeightOffset))]
        [NotifyPropertyChangedFor(nameof(HalfHeightOffset))]
        private double _height;

        [ObservableProperty]
        private double _left;

        [ObservableProperty]
        private double _top;

        // ─── Computed resize-handle offsets ────────────────────────────────────
        // Canvas.Left/Top for each of the 8 resize Thumbs.
        // Calculated from Width/Height. Re-notified via [NotifyPropertyChangedFor].

        /// <summary>Canvas.Left for right-edge handles (Width - 5px half-handle).</summary>
        public double WidthOffset      => Width  - 5;

        /// <summary>Canvas.Top for bottom-edge handles (Height - 5px half-handle).</summary>
        public double HeightOffset     => Height - 5;

        /// <summary>Canvas.Left for center-horizontal handle.</summary>
        public double HalfWidthOffset  => (Width  / 2) - 5;

        /// <summary>Canvas.Top for center-vertical handle.</summary>
        public double HalfHeightOffset => (Height / 2) - 5;

        // ─── Selection state ───────────────────────────────────────────────────
        [ObservableProperty]
        private bool _isSelected;

        // ─── Core placeholder metadata ─────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayLabel))]
        [NotifyPropertyChangedFor(nameof(IsTextType))]
        [NotifyPropertyChangedFor(nameof(IsImageType))]
        private string _variableName = string.Empty;

        /// <summary>"text" or "image". Drives Properties Panel section visibility.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayLabel))]
        [NotifyPropertyChangedFor(nameof(IsTextType))]
        [NotifyPropertyChangedFor(nameof(IsImageType))]
        private string _type = "text";

        /// <summary>True when this placeholder is a text block.</summary>
        public bool IsTextType  => Type.Equals("text",  StringComparison.OrdinalIgnoreCase);

        /// <summary>True when this placeholder is an image box.</summary>
        public bool IsImageType => Type.Equals("image", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Human-readable label shown inside the canvas box.
        /// Format: "[T] {name}" or "[I] {photo}"
        /// </summary>
        public string DisplayLabel =>
            $"[{(IsTextType ? "T" : "I")}] {{{VariableName}}}";

        // ─── Text Style fields ─────────────────────────────────────────────────
        // Exposed flat for direct binding in the Properties Panel.
        // These fields only matter when Type == "text".

        [ObservableProperty] private string _fontFamily        = "Arial";
        [ObservableProperty] private double _fontSize          = 24;
        [ObservableProperty] private string _textColor         = "#FF000000";
        [ObservableProperty] private bool   _isBold            = false;
        [ObservableProperty] private bool   _isItalic          = false;
        [ObservableProperty] private string _alignment         = "left";
        [ObservableProperty] private string _verticalAlignment = "top";
        [ObservableProperty] private bool   _wordWrap          = true;
        [ObservableProperty] private float  _lineHeight        = 1.2f;

        // ─── Image Style fields ────────────────────────────────────────────────
        // These fields only matter when Type == "image".

        [ObservableProperty] private string _fitMode      = "cover";
        [ObservableProperty] private double _anchorX      = 0.5;
        [ObservableProperty] private double _anchorY      = 0.5;
        [ObservableProperty] private double _cornerRadius = 0;

        // ─── Constructor ───────────────────────────────────────────────────────

        public PlaceholderViewModel(Placeholder model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));

            // Sync observable fields from the backing model on construction.
            _left          = model.Bounds.X;
            _top           = model.Bounds.Y;
            _width         = model.Bounds.Width;
            _height        = model.Bounds.Height;
            _variableName  = model.VariableName;
            _type          = model.Type;

            if (model.TextStyle is { } ts)
            {
                _fontFamily        = ts.FontFamily;
                _fontSize          = ts.FontSize;
                _textColor         = ts.Color;
                _isBold            = ts.Bold;
                _isItalic          = ts.Italic;
                _alignment         = ts.Alignment;
                _verticalAlignment = ts.VerticalAlignment;
                _wordWrap          = ts.WordWrap;
                _lineHeight        = ts.LineHeight;
            }

            if (model.ImageStyle is { } img)
            {
                _fitMode       = img.FitMode;
                _anchorX       = img.AnchorX;
                _anchorY       = img.AnchorY;
                _cornerRadius  = img.CornerRadius;
            }
        }

        // ─── Flush VM state back into the serializable model ──────────────────
        /// <summary>
        /// Must be called before saving the template to JSON or starting generation.
        /// Writes all current observable property values back into the backing Model.
        /// </summary>
        public void SyncToModel()
        {
            Model.VariableName  = VariableName;
            Model.Type          = Type;
            Model.Bounds.X      = (float)Left;
            Model.Bounds.Y      = (float)Top;
            Model.Bounds.Width  = (float)Width;
            Model.Bounds.Height = (float)Height;

            if (IsTextType)
            {
                Model.TextStyle = new TextStyle
                {
                    FontFamily        = FontFamily,
                    FontSize          = (float)FontSize,
                    Color             = TextColor,
                    Bold              = IsBold,
                    Italic            = IsItalic,
                    Alignment         = Alignment,
                    VerticalAlignment = VerticalAlignment,
                    WordWrap          = WordWrap,
                    LineHeight        = LineHeight,
                };
                Model.ImageStyle = null;
            }
            else
            {
                Model.ImageStyle = new ImageStyle
                {
                    FitMode      = FitMode,
                    AnchorX      = (float)AnchorX,
                    AnchorY      = (float)AnchorY,
                    CornerRadius = (float)CornerRadius,
                };
                Model.TextStyle = null;
            }
        }
    }
}
