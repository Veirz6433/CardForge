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
    /// <summary>
    /// Root ViewModel for the main window.
    /// Orchestrates template editing, Excel import, and the bulk generation pipeline.
    ///
    /// MVVM PATTERN NOTES:
    ///   - [ObservableProperty] generates the public property + INotifyPropertyChanged wiring.
    ///   - [RelayCommand] generates an ICommand property from a private method.
    ///   - [RelayCommand(IncludeCancelCommand = true)] auto-generates a paired CancelCommand.
    ///   - No code-behind logic: the View is purely declarative XAML bindings.
    /// </summary>
    public sealed partial class MainViewModel : ObservableObject
    {
        // ── Services ───────────────────────────────────────────────────────────
        private readonly ImageGeneratorService _generatorService = new();
        private readonly ExcelService          _excelService     = new();

        // ── Template State ─────────────────────────────────────────────────────

        /// <summary>The root template configuration (serialized to .bigt JSON).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanvasWidth))]
        [NotifyPropertyChangedFor(nameof(CanvasHeight))]
        private Template _currentTemplate = new();

        /// <summary>Path to the loaded background image, bound to the canvas Image element.</summary>
        [ObservableProperty]
        private string _backgroundImagePath = string.Empty;

        /// <summary>Output pixel width of the template canvas (from CurrentTemplate).</summary>
        public double CanvasWidth  => CurrentTemplate.CanvasWidth;
        /// <summary>Output pixel height of the template canvas.</summary>
        public double CanvasHeight => CurrentTemplate.CanvasHeight;

        // ── Placeholder Collection ─────────────────────────────────────────────

        /// <summary>
        /// The live collection of placeholder ViewModels displayed on the canvas.
        /// Bound to the ItemsControl on the editor canvas.
        /// </summary>
        public ObservableCollection<PlaceholderViewModel> Placeholders { get; } = new();

        /// <summary>The currently selected placeholder (drives Properties Panel visibility).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        [NotifyPropertyChangedFor(nameof(IsTextSelected))]
        [NotifyPropertyChangedFor(nameof(IsImageSelected))]
        private PlaceholderViewModel? _selectedPlaceholder;

        public bool HasSelection  => SelectedPlaceholder is not null;
        public bool IsTextSelected  => SelectedPlaceholder?.Type == "text";
        public bool IsImageSelected => SelectedPlaceholder?.Type == "image";

        // ── Excel & Generation State ───────────────────────────────────────────

        /// <summary>Path to the imported Excel file.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ExcelFileName))]
        private string _excelFilePath = string.Empty;

        public string ExcelFileName => string.IsNullOrEmpty(ExcelFilePath)
            ? "No file imported"
            : Path.GetFileName(ExcelFilePath);

        /// <summary>Number of rows detected in the imported Excel file.</summary>
        [ObservableProperty]
        private int _excelRowCount;

        /// <summary>Directory where generated images will be saved.</summary>
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        /// <summary>Which Excel column to use as the output filename (optional).</summary>
        [ObservableProperty]
        private string _fileNameColumn = string.Empty;

        // ── Progress & Status ──────────────────────────────────────────────────

        [ObservableProperty] private int    _progressValue;
        [ObservableProperty] private int    _progressMax = 1;
        [ObservableProperty] private string _statusMessage = "Ready.";
        [ObservableProperty] private bool   _isGenerating;

        /// <summary>Parsed Excel rows, held in memory between import and generation.</summary>
        private List<Dictionary<string, string>> _parsedRows = new();

        // ── Template Commands ──────────────────────────────────────────────────

        /// <summary>Opens a file dialog to load a background image onto the canvas.</summary>
        [RelayCommand]
        private void LoadBackgroundImage()
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Select Background Image",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff|All Files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            BackgroundImagePath              = dialog.FileName;
            CurrentTemplate.BackgroundImagePath = dialog.FileName;
            StatusMessage = $"Background loaded: {Path.GetFileName(dialog.FileName)}";
        }

        /// <summary>Adds a new Text placeholder at a default position in the center of the canvas.</summary>
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
            StatusMessage = $"Added text placeholder: {{{model.VariableName}}}";
        }

        /// <summary>Adds a new Image placeholder at a default position.</summary>
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
            StatusMessage = $"Added image placeholder: {{{model.VariableName}}}";
        }

        /// <summary>Removes the currently selected placeholder from the canvas and template.</summary>
        [RelayCommand(CanExecute = nameof(HasSelection))]
        private void DeleteSelectedPlaceholder()
        {
            if (SelectedPlaceholder is null) return;

            CurrentTemplate.Placeholders.Remove(SelectedPlaceholder.Model);
            Placeholders.Remove(SelectedPlaceholder);
            SelectedPlaceholder = null;
            StatusMessage = "Placeholder deleted.";
        }

        /// <summary>
        /// Called from code-behind when the user clicks a placeholder on the canvas.
        /// Deselects all others, selects the clicked one.
        /// </summary>
        public void SelectPlaceholder(PlaceholderViewModel? vm)
        {
            foreach (var p in Placeholders)
                p.IsSelected = false;

            SelectedPlaceholder = vm;
            if (vm is not null) vm.IsSelected = true;
        }

        /// <summary>Deselects all placeholders (called when canvas background is clicked).</summary>
        [RelayCommand]
        private void ClearSelection()
        {
            SelectPlaceholder(null);
        }

        // ── Canvas Size Commands ───────────────────────────────────────────────

        [RelayCommand]
        private void SetCanvasSize()
        {
            // For a complete implementation, this would open a dialog.
            // Here we demonstrate the pattern with a direct mutation.
            // CurrentTemplate.CanvasWidth / CanvasHeight are set from the dialog result,
            // then OnPropertyChanged() is raised for CanvasWidth/CanvasHeight.
            OnPropertyChanged(nameof(CanvasWidth));
            OnPropertyChanged(nameof(CanvasHeight));
        }

        // ── Template Save / Load ───────────────────────────────────────────────

        /// <summary>
        /// Serializes the current template (with all placeholder positions/styles) to a .bigt JSON file.
        /// Calls SyncToModel() on all placeholder VMs first to flush UI state into the model.
        /// </summary>
        [RelayCommand]
        private void SaveTemplate()
        {
            // Flush all VM state back into their backing models before serialization.
            foreach (var vm in Placeholders)
                vm.SyncToModel();

            var dialog = new SaveFileDialog
            {
                Title            = "Save Template",
                Filter           = "BulkImageGenerator Template|*.bigt|JSON File|*.json",
                DefaultExt       = ".bigt",
                FileName         = CurrentTemplate.Name
            };

            if (dialog.ShowDialog() != true) return;

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(CurrentTemplate, options);
            File.WriteAllText(dialog.FileName, json);
            StatusMessage = $"Template saved: {Path.GetFileName(dialog.FileName)}";
        }

        /// <summary>
        /// Deserializes a .bigt JSON file and rebuilds the canvas PlaceholderViewModels.
        /// </summary>
        [RelayCommand]
        private void LoadTemplate()
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Open Template",
                Filter = "BulkImageGenerator Template|*.bigt|JSON File|*.json|All Files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                string json     = File.ReadAllText(dialog.FileName);
                var template    = JsonSerializer.Deserialize<Template>(json);
                if (template is null) throw new InvalidDataException("Template file is empty or corrupt.");

                CurrentTemplate     = template;
                BackgroundImagePath = template.BackgroundImagePath;

                // Rebuild observable collection from deserialized model.
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

        // ── Excel Import ───────────────────────────────────────────────────────

        /// <summary>
        /// Opens a file dialog for the user to select an Excel file,
        /// parses it using ExcelService, and stores the rows for generation.
        /// </summary>
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
                ProgressMax   = ExcelRowCount;
                StatusMessage = $"Imported {ExcelRowCount} rows from {ExcelFileName}.";

                // Warn the user if placeholder names are not found in the Excel columns.
                ValidatePlaceholderMapping();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse Excel file:\n{ex.Message}",
                    "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Checks that every placeholder's variableName exists as a column in the Excel data.
        /// Shows a warning (not an error) so the user can review before generating.
        /// </summary>
        private void ValidatePlaceholderMapping()
        {
            if (_parsedRows.Count == 0) return;
            var headers = _parsedRows[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unmapped = Placeholders
                .Where(p => !headers.Contains(p.VariableName))
                .Select(p => $"  • {{{p.VariableName}}}")
                .ToList();

            if (unmapped.Count > 0)
            {
                string msg = "The following placeholders have no matching Excel column:\n"
                           + string.Join("\n", unmapped)
                           + "\n\nThey will be skipped during generation.";
                MessageBox.Show(msg, "Mapping Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── Output Directory ───────────────────────────────────────────────────

        [RelayCommand]
        private void BrowseOutputDirectory()
        {
            // FolderBrowserDialog requires a reference to System.Windows.Forms,
            // OR use the modern Windows.Storage.Pickers (WinRT).
            // Here we use a workaround with OpenFileDialog to stay pure WPF.
            var dialog = new OpenFileDialog
            {
                Title            = "Select Output Directory",
                CheckFileExists  = false,
                CheckPathExists  = true,
                FileName         = "Select Folder",
                Filter           = "Folder|*.none",
                ValidateNames    = false
            };

            if (dialog.ShowDialog() == true)
            {
                OutputDirectory = Path.GetDirectoryName(dialog.FileName)
                                  ?? dialog.FileName;
                StatusMessage = $"Output directory: {OutputDirectory}";
            }
        }

        // ── Bulk Generation Command ────────────────────────────────────────────

        /// <summary>
        /// Main generation command. Runs async on the thread pool via Task.Run (inside the service).
        /// [IncludeCancelCommand = true] auto-generates GenerateAllCancelCommand for the Cancel button.
        /// Progress is reported via IProgress&lt;T&gt; which marshals to the UI SynchronizationContext
        /// automatically — no Dispatcher.Invoke needed [web:22].
        /// </summary>
        [RelayCommand(IncludeCancelCommand = true)]
        private async Task GenerateAllAsync(CancellationToken cancellationToken)
        {
            // ── Pre-flight validation ──────────────────────────────────────────
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

            // Flush all VM state to the backing models before generation.
            foreach (var vm in Placeholders)
                vm.SyncToModel();

            // ── Setup progress reporting ───────────────────────────────────────
            // IProgress<T> captures the current SynchronizationContext at construction time.
            // When Report() is called from a thread-pool thread, it automatically posts
            // the callback to the UI thread — no Dispatcher.Invoke required.
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
                // Prepare the service (loads background image bytes into memory once).
                await _generatorService.PrepareAsync(CurrentTemplate);

                // Run the parallel batch loop — does not block the UI thread.
                await _generatorService.GenerateAllAsync(
                    rows:              _parsedRows,
                    outputDirectory:   OutputDirectory,
                    fileNameColumn:    string.IsNullOrWhiteSpace(FileNameColumn) ? null : FileNameColumn,
                    progress:          progress,
                    cancellationToken: cancellationToken);

                StatusMessage = $"✅ Done! {_parsedRows.Count} images saved to: {OutputDirectory}";
                MessageBox.Show($"{_parsedRows.Count} images generated successfully!\n\nSaved to:\n{OutputDirectory}",
                    "Generation Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "⚠️ Generation cancelled by user.";
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
