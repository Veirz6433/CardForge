using BulkImageGenerator.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BulkImageGenerator.Views
{
    public partial class MainWindow : Window
    {
        // ── Drag state ────────────────────────────────────────────────────────
        private bool _isDragging;
        private Point _dragStartMousePos;
        private double _dragStartLeft;
        private double _dragStartTop;
        private PlaceholderViewModel? _draggingVm;
        private ContentPresenter? _draggingContainer;

        public MainWindow()
        {
            InitializeComponent();

            // Set DataContext explicitly here — more reliable than inline XAML instantiation
            DataContext = new MainViewModel();
        }

        private MainViewModel ViewModel => (MainViewModel)DataContext;

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is Canvas)
                ViewModel.ClearSelectionCommand.Execute(null);
        }

        private void PlaceholderItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ContentPresenter container) return;
            if (container.Content is not PlaceholderViewModel vm) return;

            ViewModel.SelectPlaceholder(vm);

            if (e.LeftButton != MouseButtonState.Pressed) return;

            _isDragging        = true;
            _draggingVm        = vm;
            _draggingContainer = container;
            _dragStartMousePos = e.GetPosition((UIElement)sender);
            _dragStartLeft     = vm.Left;
            _dragStartTop      = vm.Top;

            container.CaptureMouse();
            e.Handled = true;
        }

        private void PlaceholderItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _draggingVm is null || _draggingContainer is null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point currentPos = e.GetPosition(_draggingContainer);
            double deltaX = currentPos.X - _dragStartMousePos.X;
            double deltaY = currentPos.Y - _dragStartMousePos.Y;

            _draggingVm.Left = Math.Max(0, Math.Min(
                _dragStartLeft + deltaX,
                ViewModel.CanvasWidth - _draggingVm.Width));

            _draggingVm.Top = Math.Max(0, Math.Min(
                _dragStartTop + deltaY,
                ViewModel.CanvasHeight - _draggingVm.Height));
        }

        private void PlaceholderItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _draggingContainer?.ReleaseMouseCapture();
            _draggingVm        = null;
            _draggingContainer = null;
        }

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb) return;
            if (ViewModel.SelectedPlaceholder is not { } vm) return;

            string handle = thumb.Tag?.ToString() ?? "BR";
            const double minSize = 20;
            double dx = e.HorizontalChange;
            double dy = e.VerticalChange;

            switch (handle)
            {
                case "TL": vm.Left += dx; vm.Top += dy; vm.Width  = Math.Max(minSize, vm.Width  - dx); vm.Height = Math.Max(minSize, vm.Height - dy); break;
                case "TC":                vm.Top += dy;                                                  vm.Height = Math.Max(minSize, vm.Height - dy); break;
                case "TR":               vm.Top += dy; vm.Width  = Math.Max(minSize, vm.Width  + dx); vm.Height = Math.Max(minSize, vm.Height - dy); break;
                case "ML": vm.Left += dx;              vm.Width  = Math.Max(minSize, vm.Width  - dx);                                                  break;
                case "MR":                             vm.Width  = Math.Max(minSize, vm.Width  + dx);                                                  break;
                case "BL": vm.Left += dx;              vm.Width  = Math.Max(minSize, vm.Width  - dx); vm.Height = Math.Max(minSize, vm.Height + dy); break;
                case "BC":                                                                              vm.Height = Math.Max(minSize, vm.Height + dy); break;
                case "BR":                             vm.Width  = Math.Max(minSize, vm.Width  + dx); vm.Height = Math.Max(minSize, vm.Height + dy); break;
            }
        }
    }
}
