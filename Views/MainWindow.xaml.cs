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
        // ── Drag state ─────────────────────────────────────────────────────────
        private bool _isDragging;
        private Point _dragStartMouse;
        private double _dragStartLeft;
        private double _dragStartTop;
        private PlaceholderViewModel? _draggingVm;
        private UIElement? _draggingElement;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private MainViewModel VM => (MainViewModel)DataContext;

        // ── Canvas background click → deselect ────────────────────────────────
        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is Canvas)
                VM.ClearSelectionCommand.Execute(null);
        }

        // ── Placeholder clicked: select + start drag ───────────────────────────
        private void Placeholder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not PlaceholderViewModel vm) return;

            VM.SelectPlaceholder(vm);

            _isDragging      = true;
            _draggingVm      = vm;
            _draggingElement = fe;
            _dragStartMouse  = e.GetPosition(fe);
            _dragStartLeft   = vm.Left;
            _dragStartTop    = vm.Top;

            fe.CaptureMouse();
            e.Handled = true;
        }

        // ── Mouse move: reposition placeholder ────────────────────────────────
        private void Placeholder_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _draggingVm is null || _draggingElement is null) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }

            Point current = e.GetPosition(_draggingElement);
            double dx = current.X - _dragStartMouse.X;
            double dy = current.Y - _dragStartMouse.Y;

            _draggingVm.Left = Math.Max(0,
                Math.Min(_dragStartLeft + dx, VM.CanvasWidth  - _draggingVm.Width));
            _draggingVm.Top  = Math.Max(0,
                Math.Min(_dragStartTop  + dy, VM.CanvasHeight - _draggingVm.Height));
        }

        // ── Mouse up: end drag ─────────────────────────────────────────────────
        private void Placeholder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            => EndDrag();

        private void EndDrag()
        {
            if (!_isDragging) return;
            _isDragging = false;
            _draggingElement?.ReleaseMouseCapture();
            _draggingVm      = null;
            _draggingElement = null;
        }

        // ── Resize thumb drag delta ────────────────────────────────────────────
        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb) return;

            // Walk up the visual tree to find the PlaceholderViewModel
            // The Thumb is inside: Canvas > Grid > [DataTemplate] > ContentPresenter
            PlaceholderViewModel? vm = null;
            DependencyObject? parent = thumb;
            while (parent is not null)
            {
                if (parent is FrameworkElement fe && fe.DataContext is PlaceholderViewModel pvm)
                {
                    vm = pvm;
                    break;
                }
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }

            if (vm is null) return;

            string handle  = thumb.Tag?.ToString() ?? "BR";
            const double min = 20;
            double dx = e.HorizontalChange;
            double dy = e.VerticalChange;

            switch (handle)
            {
                case "TL": vm.Left += dx; vm.Top += dy; vm.Width  = Math.Max(min, vm.Width  - dx); vm.Height = Math.Max(min, vm.Height - dy); break;
                case "TC":                vm.Top += dy;                                              vm.Height = Math.Max(min, vm.Height - dy); break;
                case "TR":               vm.Top  += dy; vm.Width  = Math.Max(min, vm.Width  + dx); vm.Height = Math.Max(min, vm.Height - dy); break;
                case "ML": vm.Left += dx;               vm.Width  = Math.Max(min, vm.Width  - dx);                                             break;
                case "MR":                              vm.Width  = Math.Max(min, vm.Width  + dx);                                             break;
                case "BL": vm.Left += dx;               vm.Width  = Math.Max(min, vm.Width  - dx); vm.Height = Math.Max(min, vm.Height + dy); break;
                case "BC":                                                                           vm.Height = Math.Max(min, vm.Height + dy); break;
                case "BR":                              vm.Width  = Math.Max(min, vm.Width  + dx); vm.Height = Math.Max(min, vm.Height + dy); break;
            }

            e.Handled = true;
        }
    }
}
