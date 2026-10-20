using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Profile-local, presentation-only state. Never write to audio files or rekordbox.
/// A closed/minimized window always returns as a normal, visible window.
/// </summary>
public static class WindowPlacementStore
{
    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RekordboxMyTagSync", "window-placement.json");

    public static void Restore(Window window, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        path ??= DefaultPath;
        if (!File.Exists(path)) return;
        Placement? state;
        try { state = JsonSerializer.Deserialize<Placement>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return; // Corrupt optional UI preferences must not prevent application startup.
        }

        if (state is null ||
            !double.IsFinite(state.Left) || !double.IsFinite(state.Top) ||
            !double.IsFinite(state.Width) || !double.IsFinite(state.Height) ||
            state.Width < window.MinWidth || state.Height < window.MinHeight)
            return;

        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var saved = new Rect(state.Left, state.Top, state.Width, state.Height);
        var visible = Rect.Intersect(desktop, saved);
        if (visible.IsEmpty || visible.Width < 120 || visible.Height < 80)
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = state.Left;
        window.Top = state.Top;
        window.Width = state.Width;
        window.Height = state.Height;
        if (state.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Fit compact primary desktops in WPF DIPs without moving saved windows
    /// wholly on another monitor. Does not touch any rekordbox data.
    /// </summary>
    public static void FitToPrimaryWorkArea(Window window) =>
        FitToWorkArea(window, SystemParameters.WorkArea);

    internal static void FitToWorkArea(Window window, Rect area)
    {
        ArgumentNullException.ThrowIfNull(window);
        const double gap = 8;
        if (window.WindowState != WindowState.Normal ||
            area.IsEmpty || !double.IsFinite(area.X) || !double.IsFinite(area.Y) ||
            !double.IsFinite(area.Width) || !double.IsFinite(area.Height) ||
            area.Width < window.MinWidth + gap * 2 ||
            area.Height < window.MinHeight + gap * 2)
            return;

        var left = double.IsFinite(window.Left) ? window.Left : area.Left + gap;
        var top = double.IsFinite(window.Top) ? window.Top : area.Top + gap;
        var width = double.IsFinite(window.Width) ? window.Width : window.MinWidth;
        var height = double.IsFinite(window.Height) ? window.Height : window.MinHeight;
        var visible = Rect.Intersect(area, new Rect(left, top,
            Math.Max(width, window.MinWidth), Math.Max(height, window.MinHeight)));
        if (visible.IsEmpty || visible.Width < 120 || visible.Height < 80)
            return; // Preserves user placement entirely on a secondary display.

        width = Math.Clamp(width, window.MinWidth, area.Width - gap * 2);
        height = Math.Clamp(height, window.MinHeight, area.Height - gap * 2);
        left = Math.Clamp(left, area.Left + gap, area.Right - width - gap);
        top = Math.Clamp(top, area.Top + gap, area.Bottom - height - gap);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = width;
        window.Height = height;
        window.Left = left;
        window.Top = top;
    }

    public static void Save(Window window, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        path ??= DefaultPath;
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width < window.MinWidth || bounds.Height < window.MinHeight)
            return;

        var state = new Placement(bounds.X, bounds.Y, bounds.Width, bounds.Height,
            window.WindowState == WindowState.Maximized);
        var payload = JsonSerializer.Serialize(state);

        // Wear-friendly: a resize-free session must not rewrite the state file.
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), payload, StringComparison.Ordinal))
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("Window placement file has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, payload);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

 
/// <summary>
/// Per-user, presentation-only WPF DataGrid column widths. Stable grid names
/// and column counts protect against accidentally replaying a stale layout
/// after a future UI schema change. Never writes any rekordbox/foobar data.
/// </summary>
public static class GridColumnWidthStore
{
    private const double MinColumnWidth = 32;
    private const double MaxColumnWidth = 4096;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RekordboxMyTagSync", "column-widths.json");

    public static void Restore(
        IReadOnlyDictionary<string, DataGrid> grids, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(grids);
        var saved = Read(path ?? DefaultPath);
        foreach (var (name, grid) in grids)
        {
            if (!saved.TryGetValue(name, out var widths) || widths is null ||
                widths.Length != grid.Columns.Count ||
                widths.Any(width => !Valid(width)))
                continue;

            for (var i = 0; i < widths.Length; i++)
                grid.Columns[i].Width = new DataGridLength(
                    widths[i], DataGridLengthUnitType.Pixel);
        }
    }

    public static void Save(
        IReadOnlyDictionary<string, DataGrid> grids, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(grids);
        path ??= DefaultPath;
        var widthsByGrid = Read(path);
        foreach (var (name, grid) in grids)
        {
            if (grid.Columns.Count == 0) continue;
            var widths = grid.Columns.Select(column =>
                column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth)
                .ToArray();
            // An unvisited tab may not have been measured. Preserve its older
            // persisted widths instead of overwriting them with zeroes.
            if (widths.Any(width => !Valid(width))) continue;
            widthsByGrid[name] = widths;
        }

        var json = JsonSerializer.Serialize(widthsByGrid);
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
            return;

        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("Column layout file has no directory.");
        Directory.CreateDirectory(parent);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Dictionary<string, double[]> Read(string path)
    {
        var empty = new Dictionary<string, double[]>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024)
                return empty;
            return JsonSerializer.Deserialize<Dictionary<string, double[]>>(
                File.ReadAllText(path)) ?? empty;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Optional presentation state must never prevent application use.
            return empty;
        }
    }

    private static bool Valid(double width) =>
        double.IsFinite(width) && width >= MinColumnWidth && width <= MaxColumnWidth;
}
