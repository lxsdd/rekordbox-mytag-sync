using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RekordboxMyTagSync
{
    // Shared solution from lxsdd/dj-library: WPF does not reliably route
    // WM_MOUSEHWHEEL from Windows Precision Touchpads
    // into nested DataGrid/ScrollViewer controls. This router forwards the native
    // horizontal wheel message to the scrollable control currently under the mouse.
    public static class HorizontalScrollSupport
    {
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const double PixelsPerWheelNotch = 72.0;
        private static readonly Dictionary<Window, Router> Routers = new Dictionary<Window, Router>();
        private static readonly DependencyProperty GridEnabledProperty = DependencyProperty.RegisterAttached(
            "GridEnabled", typeof(bool), typeof(HorizontalScrollSupport), new PropertyMetadata(false));

        public static void Enable(DataGrid grid)
        {
            if (grid == null || (bool)grid.GetValue(GridEnabledProperty)) return;
            grid.SetValue(GridEnabledProperty, true);
            grid.Loaded += GridLoaded;
            Window window = Window.GetWindow(grid);
            if (window != null) Enable(window);
        }

        public static bool IsEnabled(DataGrid grid)
        {
            return grid != null && (bool)grid.GetValue(GridEnabledProperty);
        }

        private static void GridLoaded(object sender, RoutedEventArgs e)
        {
            DataGrid grid = sender as DataGrid;
            Window window = grid == null ? null : Window.GetWindow(grid);
            if (window != null) Enable(window);
        }

        public static void Enable(Window window)
        {
            if (window == null || Routers.ContainsKey(window)) return;
            Router router = new Router(window);
            Routers[window] = router;
            router.Attach();
        }

        private sealed class Router
        {
            private readonly Window _window;
            private HwndSource _source;
            private HwndSourceHook _hook;

            public Router(Window window)
            {
                _window = window;
            }

            public void Attach()
            {
                _window.SourceInitialized += SourceInitialized;
                _window.Closed += WindowClosed;
                _window.PreviewMouseWheel += PreviewMouseWheel;
                TryAttachSource();
            }

            private void SourceInitialized(object sender, EventArgs e)
            {
                TryAttachSource();
            }

            private void TryAttachSource()
            {
                if (_source != null) return;
                _source = PresentationSource.FromVisual(_window) as HwndSource;
                if (_source == null) return;
                _hook = new HwndSourceHook(WndProc);
                _source.AddHook(_hook);
            }

            private void WindowClosed(object sender, EventArgs e)
            {
                _window.PreviewMouseWheel -= PreviewMouseWheel;
                _window.SourceInitialized -= SourceInitialized;
                _window.Closed -= WindowClosed;
                if (_source != null && _hook != null)
                {
                    try { _source.RemoveHook(_hook); }
                    catch { }
                }
                Routers.Remove(_window);
            }

            private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;

                int raw = unchecked((int)wParam.ToInt64());
                short delta = unchecked((short)((raw >> 16) & 0xFFFF));
                if (delta == 0) return IntPtr.Zero;

                ScrollViewer viewer = FindTargetScrollViewer();
                if (viewer == null || viewer.ScrollableWidth <= 0.5) return IntPtr.Zero;

                ScrollHorizontal(viewer, delta);
                handled = true;
                return IntPtr.Zero;
            }

            // Shift + ordinary mouse wheel is also supported as a desktop fallback.
            private void PreviewMouseWheel(object sender, MouseWheelEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift) return;
                ScrollViewer viewer = FindTargetScrollViewer();
                if (viewer == null || viewer.ScrollableWidth <= 0.5) return;

                ScrollHorizontal(viewer, e.Delta);
                e.Handled = true;
            }

            private static void ScrollHorizontal(ScrollViewer viewer, double delta)
            {
                // WM_MOUSEHWHEEL positive delta means scrolling to the right.
                double amount = (delta / 120.0) * PixelsPerWheelNotch;
                double target = viewer.HorizontalOffset + amount;
                if (target < 0) target = 0;
                if (target > viewer.ScrollableWidth) target = viewer.ScrollableWidth;
                viewer.ScrollToHorizontalOffset(target);
            }

            private static ScrollViewer FindTargetScrollViewer()
            {
                DependencyObject underMouse = Mouse.DirectlyOver as DependencyObject;
                ScrollViewer viewer = FindScrollableAncestor(underMouse);
                if (viewer != null) return viewer;

                DataGrid grid = FindAncestor<DataGrid>(underMouse);
                if (grid != null)
                {
                    viewer = FindScrollableDescendant(grid);
                    if (viewer != null) return viewer;
                }

                DependencyObject focused = Keyboard.FocusedElement as DependencyObject;
                viewer = FindScrollableAncestor(focused);
                if (viewer != null) return viewer;

                grid = FindAncestor<DataGrid>(focused);
                if (grid != null) return FindScrollableDescendant(grid);
                return null;
            }

            private static ScrollViewer FindScrollableAncestor(DependencyObject start)
            {
                DependencyObject current = start;
                while (current != null)
                {
                    ScrollViewer viewer = current as ScrollViewer;
                    if (viewer != null && viewer.ScrollableWidth > 0.5) return viewer;
                    current = GetParent(current);
                }
                return null;
            }

            private static T FindAncestor<T>(DependencyObject start) where T : DependencyObject
            {
                DependencyObject current = start;
                while (current != null)
                {
                    T typed = current as T;
                    if (typed != null) return typed;
                    current = GetParent(current);
                }
                return null;
            }

            private static DependencyObject GetParent(DependencyObject value)
            {
                if (value == null) return null;
                Visual visual = value as Visual;
                if (visual != null)
                {
                    try { return VisualTreeHelper.GetParent(visual); }
                    catch { }
                }
                try { return LogicalTreeHelper.GetParent(value); }
                catch { return null; }
            }

            private static ScrollViewer FindScrollableDescendant(DependencyObject root)
            {
                if (root == null) return null;
                int count;
                try { count = VisualTreeHelper.GetChildrenCount(root); }
                catch { return null; }

                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(root, i);
                    ScrollViewer viewer = child as ScrollViewer;
                    if (viewer != null && viewer.ScrollableWidth > 0.5) return viewer;
                    viewer = FindScrollableDescendant(child);
                    if (viewer != null) return viewer;
                }
                return null;
            }
        }
    }
}
