using BulkImageGenerator.Models;
using BulkImageGenerator.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BulkImageGenerator.ViewModels
{
    public sealed partial class MainViewModel : ObservableObject
    {
        // ── Services ───────────────────────────────────────────────────────────
        private readonly ImageGeneratorService _generatorService = new();
        private readonly ExcelService _excelService = new();

        // ── Template ───────────────────────────────────────────────────────────
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanvasWidth))]
        [NotifyPropertyChangedFor(nameof(CanvasHeight))]
        private Template _currentTemplate = new();

        [ObservableProperty]
        private string _backgroundImagePath = string.Empty;

        public double CanvasWidth  => CurrentTemplate.CanvasWidth;
        public double CanvasHeight => CurrentTemplate.CanvasHeight;

        // ── Placeholders ───────────────────────────────────────────────────────
        public ObservableCollection<PlaceholderViewModel> Placeholders { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        [NotifyPropertyChangedFor(nameof(IsTextSelected))]
        [NotifyPropertyChangedFor(nameof(IsImageSelected))]
        private PlaceholderViewModel? _selectedPlaceholder;

        public bool HasSelection   => SelectedPlaceholder is not null;
        public bool IsTextSelected  => SelectedPlaceholder?.Type == "text";
        public bool IsImageSelected => SelectedPlaceholder?.Type == "image";

        // ── Excel ──────────────────────────────────────────────────────────────
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ExcelFileName))]
        private string _excelFilePath = string.Empty;

        public string ExcelFileName => string.IsNullOrEmpty(ExcelFilePath)
            ? "No file imported"
            : Path.GetFileName(ExcelFilePath);

        [ObservableProperty]
        private int _excelRowCount;

        private List<Dictionary<string, string>> _parsedRows = new();

        // ── Assets Folder ──────────────────────────────────────────────────────
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AssetsFolderName))]
        private string _assetsFolder = string.Empty;

        public string AssetsFolderName => string.IsNullOrEmpty(AssetsFolder)
            ? "No folder selected"
            : Path.GetFileName(AssetsFolder.TrimEnd('\\', '/'));

        // ── Output ─────────────────────────────────────────────────────────────
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        [ObservableProperty]
        private string _fileNameColumn = string.Empty;

        // ── Progress & Status ──────────────────────────────────────────────────
        [ObservableProperty] private int    _progressValue;
        [ObservableProperty] private int    _progressMax = 1;
        [ObservableProperty] private string _statusMessage = "Ready. Load a background image to start.";
        [ObservableProperty] private bool   _isGenerating;

        // ══════════════════════════════════════════════════════════════════════
        // COMMANDS
        // ══════════════════════════════════════════════════════════════════════

        // ── Background Image ───────────────────────────────────────────────────
        [RelayCommand]
        private void LoadBackgroundImage()
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Select Background Image",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp|All Files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            BackgroundImagePath = dialog.FileName;
            CurrentTemplate.BackgroundImagePath = dialog.FileName;
            StatusMessage = $"Background loaded: {Path.GetFileName(dialog.FileName)}";
        }

        // ── Add Text Placeholder ───────────────────────────────────────────────
        [RelayCommand]
        private void AddTextPlaceholder()
        {
            var model = new Placeholder
            {
                VariableName = $"text{Placeholders.Count + 1}",
                Type         = "text",
                Bounds       = Bounds.Create(
                    x:      (float)(CanvasWidth  / 2 - 150),
                    y:      (float)(CanvasHeight / 2 - 30),
                    width:  300,
                    height: 60),
                TextStyle = new TextStyle()
            };

            var vm = new PlaceholderViewModel(model);
            Placeholders.Add(vm);
            CurrentTemplate.Placeholders.Add(model);
            SelectPlaceholder(vm);
            StatusMessage = $"Added text placeholder '{{{model.VariableName}}}'.";
        }

        // ── Add Image Placeholder ──────────────────────────────────────────────
        [RelayCommand]
        private void AddImagePlaceholder()
        {
            var model = new Placeholder
            {
                VariableName = $"image{Placeholders.Count + 1}",
                Type         = "image",
                Bounds       = Bounds.Create(
                    x:      (float)(CanvasWidth  / 2 - 75),
                    y:      (float)(CanvasHeight / 2 - 75),
                    width:  150,
                    height: 150),
                ImageStyle = new ImageStyle()
            };

            var vm = new PlaceholderViewModel(model);
            Placeholders.Add(vm);
            CurrentTemplate.Placeholders.Add(model);
            SelectPlaceholder(vm);
            StatusMessage = $"Added image placeholder '{{{model.VariableName}}}'.";
        }

        // ── Delete Selected Placeholder ────────────────────────────────────────
        [RelayCommand]
        private void DeleteSelectedPlaceholder()
        {
            if (SelectedPlaceholder is null) return;
            CurrentTemplate.Placeholders.Remove(SelectedPlaceholder.Model);
            Placeholders.Remove(SelectedPlaceholder);
            SelectedPlaceholder = null;
            StatusMessage = "Placeholder deleted.";
        }

        // ── Selection Helpers (called from code-behind) ────────────────────────
        public void SelectPlaceholder(PlaceholderViewModel? vm)
        {
            foreach (var p in Placeholders)
                p.IsSelected = false;

            SelectedPlaceholder = vm;
            if (vm is not null) vm.IsSelected = true;
        }

        [RelayCommand]
        private void ClearSelection() => SelectPlaceholder(null);

        // ── Save Template ──────────────────────────────────────────────────────
        [RelayCommand]
        private void SaveTemplate()
        {
            foreach (var vm in Placeholders)
                vm.SyncToModel();

            var dialog = new SaveFileDialog
            {
                Title      = "Save Template",
                Filter     = "BulkImageGenerator Template|*.bigt|JSON|*.json",
                DefaultExt = ".bigt",
                FileName   = CurrentTemplate.Name
            };

            if (dialog.ShowDialog() != true) return;

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(CurrentTemplate, options));
            StatusMessage = $"Template saved: {Path.GetFileName(dialog.FileName)}";
        }

        // ── Load Template ──────────────────────────────────────────────────────
        [RelayCommand]
        private void LoadTemplate()
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Open Template",
                Filter = "BulkImageGenerator Template|*.bigt|JSON|*.json|All Files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var template = JsonSerializer.Deserialize<Template>(File.ReadAllText(dialog.FileName));
                if (template is null) throw new InvalidDataException("Template file is empty or corrupt.");

                CurrentTemplate     = template;
                BackgroundImagePath = template.BackgroundImagePath;

                Placeholders.Clear();
                foreach (var model in template.Placeholders)
                    Placeholders.Add(new PlaceholderViewModel(model));

                StatusMessage = $"Template loaded: {template.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load template:\n{ex.Message}",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Browse Assets Folder ───────────────────────────────────────────────
        [RelayCommand]
        private void BrowseAssetsFolder()
        {
            var dialog = new OpenFileDialog
            {
                Title           = "Select Assets Folder — pick any file inside it",
                CheckFileExists = false,
                FileName        = "Select Folder",
                Filter          = "Any File|*.*",
                ValidateNames   = false
            };

            if (dialog.ShowDialog() != true) return;

            AssetsFolder  = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            StatusMessage = $"Assets folder set: {AssetsFolderName}";
        }

        // ── Import Excel ───────────────────────────────────────────────────────
        [RelayCommand]
        private void ImportExcel()
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Import Excel Data",
                Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                ExcelFilePath = dialog.FileName;
                _parsedRows   = _excelService.ParseFile(dialog.FileName);
                ExcelRowCount = _parsedRows.Count;
                ProgressMax   = Math.Max(1, ExcelRowCount);
                StatusMessage = $"Imported {ExcelRowCount} rows from '{ExcelFileName}'.";
                ValidatePlaceholderMapping();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse Excel file:\n{ex.Message}",
                    "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ValidatePlaceholderMapping()
        {
            if (_parsedRows.Count == 0) return;
            var headers = _parsedRows[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unmapped = Placeholders
                .Where(p => !headers.Contains(p.VariableName))
                .Select(p => $"  • {{{p.VariableName}}}")
                .ToList();

            if (unmapped.Count > 0)
                MessageBox.Show(
                    "These placeholders have no matching Excel column:\n"
                    + string.Join("\n", unmapped)
                    + "\n\nThey will be skipped during generation.",
                    "Mapping Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ── Browse Output Directory ────────────────────────────────────────────
        [RelayCommand]
        private void BrowseOutputDirectory()
        {
            var dialog = new OpenFileDialog
            {
                Title           = "Select Output Directory — pick any file inside it",
                CheckFileExists = false,
                FileName        = "Select Folder",
                Filter          = "Any File|*.*",
                ValidateNames   = false
            };

            if (dialog.ShowDialog() == true)
            {
                OutputDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
                StatusMessage   = $"Output directory: {OutputDirectory}";
            }
        }

        // ── Resolve Image Paths ────────────────────────────────────────────────
        /// <summary>
        /// Converts bare filenames in image columns (e.g. "alice.jpg") into
        /// full absolute paths by prepending the selected AssetsFolder.
        /// Full paths are left unchanged. Skipped if AssetsFolder is empty.
        /// </summary>
        private List<Dictionary<string, string>> ResolveImagePaths(
            List<Dictionary<string, string>> rows)
        {
            var imageKeys = Placeholders
                .Where(p => p.Type.Equals("image", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.VariableName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (imageKeys.Count == 0 || string.IsNullOrWhiteSpace(AssetsFolder))
                return rows;

            var resolved = new List<Dictionary<string, string>>(rows.Count);

            foreach (var row in rows)
            {
                var newRow = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase);

                foreach (var key in imageKeys)
                {
                    if (!newRow.TryGetValue(key, out string? val)
                        || string.IsNullOrWhiteSpace(val)) continue;

                    // Only prepend folder if the value has no directory component.
                    if (string.IsNullOrEmpty(Path.GetDirectoryName(val)))
                        newRow[key] = Path.Combine(AssetsFolder, val);
                }

                resolved.Add(newRow);
            }

            return resolved;
        }

        // ── Generate All (Main Command) ────────────────────────────────────────
        /// <summary>
        /// IncludeCancelCommand = true auto-generates GenerateAllCancelCommand
        /// which is bound to the Cancel button — no manual CancellationTokenSource needed.
        /// </summary>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task GenerateAllAsync(CancellationToken cancellationToken)
        {
            // ── Pre-flight checks ──────────────────────────────────────────────
            if (_parsedRows.Count == 0)
            {
                MessageBox.Show("Please import an Excel file first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(BackgroundImagePath) || !File.Exists(BackgroundImagePath))
            {
                MessageBox.Show("Please load a valid background image first.",
                    "No Background", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                MessageBox.Show("Please select an output directory.",
                    "No Output Path", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Flush all VM state to backing models before generation.
            foreach (var vm in Placeholders)
                vm.SyncToModel();

            // ── Progress reporter ──────────────────────────────────────────────
            // IProgress<T> captures the UI SynchronizationContext here,
            // so Report() calls automatically marshal to the UI thread.
            var progress = new Progress<(int Completed, int Total)>(report =>
            {
                ProgressValue = report.Completed;
                ProgressMax   = report.Total;
                StatusMessage = $"Generating... {report.Completed} / {report.Total}";
            });

            IsGenerating  = true;
            ProgressValue = 0;
            StatusMessage = "Starting generation...";

            try
            {
                // Resolve bare image filenames → full paths.
                var resolvedRows = ResolveImagePaths(_parsedRows);

                await _generatorService.PrepareAsync(CurrentTemplate);

                await _generatorService.GenerateAllAsync(
                    rows:              resolvedRows,
                    outputDirectory:   OutputDirectory,
                    fileNameColumn:    string.IsNullOrWhiteSpace(FileNameColumn) ? null : FileNameColumn,
                    progress:          progress,
                    cancellationToken: cancellationToken);

                StatusMessage = $"✅ Done! {_parsedRows.Count} images saved to: {OutputDirectory}";
                MessageBox.Show(
                    $"{_parsedRows.Count} images generated successfully!\n\nSaved to:\n{OutputDirectory}",
                    "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "⚠️ Generation cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Error: {ex.Message}";
                MessageBox.Show($"Generation failed:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsGenerating = false;
            }
        }
    }
}
