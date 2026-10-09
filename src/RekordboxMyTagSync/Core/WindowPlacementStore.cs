using System.IO;
using System.Text.Json;
using System.Windows;

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
