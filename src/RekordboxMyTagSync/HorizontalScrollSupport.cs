using System.Windows;
using System.Windows.Controls;

namespace RekordboxMyTagSync
{
    // Stable existing production API; common behavior resides in the
    // exact SHA-pinned Lxsdd.SharedDesktop.Wpf implementation.
    public static class HorizontalScrollSupport
    {
        public static void Enable(DataGrid grid)
        {
            Lxsdd.SharedDesktop.Wpf.HorizontalScrollSupport.Enable(grid);
        }

        public static bool IsEnabled(DataGrid grid)
        {
            return Lxsdd.SharedDesktop.Wpf.HorizontalScrollSupport.IsEnabled(grid);
        }

        public static void Enable(Window window)
        {
            Lxsdd.SharedDesktop.Wpf.HorizontalScrollSupport.Enable(window);
        }
    }
}
