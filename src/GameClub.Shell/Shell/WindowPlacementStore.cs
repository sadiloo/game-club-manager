using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;

namespace GameClub.Shell.Shell;

internal sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
    public bool IsMaximized { get; set; }
    public bool TrayHintShown { get; set; }
}

internal static class WindowPlacementStore
{
    private const double DefaultWidth = 1280;
    private const double DefaultHeight = 800;

    public static void Apply(Window window)
    {
        var placement = Normalize(Load());

        void ApplyBounds()
        {
            var (left, top) = ClampToWorkingArea(placement.Left, placement.Top, placement.Width, placement.Height);
            var (width, height) = ClampDimensionsToWorkingArea(left, top, placement.Width, placement.Height);
            window.WindowState = WindowState.Normal;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
        }

        // SourceInitialized runs once the HWND exists and before the first Show(); applying
        // restore bounds then WindowState here avoids WPF resetting Maximized when bounds are set earlier.
        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoadedOnce;

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            ApplyBounds();
        }

        void OnLoadedOnce(object? sender, EventArgs e)
        {
            window.Loaded -= OnLoadedOnce;
            if (placement.IsMaximized)
            {
                ApplyBounds();
                window.WindowState = WindowState.Maximized;
            }
        }
    }

    public static void Save(Window window, bool trayHintShown)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        var placement = Normalize(new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = window.WindowState == WindowState.Maximized,
            TrayHintShown = trayHintShown,
        });
        Directory.CreateDirectory(ShellPaths.ShellRoot);
        File.WriteAllText(ShellPaths.WindowJsonPath, JsonSerializer.Serialize(placement));
    }

    public static bool TrayHintShown => Load().TrayHintShown;

    private static WindowPlacement Load()
    {
        try
        {
            if (!File.Exists(ShellPaths.WindowJsonPath))
            {
                return new WindowPlacement();
            }

            var raw = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(ShellPaths.WindowJsonPath)) ?? new WindowPlacement();
            return Normalize(raw);
        }
        catch
        {
            return new WindowPlacement();
        }
    }

    private static WindowPlacement Normalize(WindowPlacement placement)
    {
        return new WindowPlacement
        {
            Left = SanitizePosition(placement.Left),
            Top = SanitizePosition(placement.Top),
            Width = SanitizeDimension(placement.Width, DefaultWidth),
            Height = SanitizeDimension(placement.Height, DefaultHeight),
            IsMaximized = placement.IsMaximized,
            TrayHintShown = placement.TrayHintShown,
        };
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double SanitizePosition(double value) => IsFinite(value) ? value : 0;

    private static double SanitizeDimension(double value, double fallback) =>
        IsFinite(value) && value > 0 ? value : fallback;

    internal static (double left, double top) ClampPositionForTests(double left, double top, double width, double height)
        => ClampToWorkingArea(left, top, width, height);

    private static (double width, double height) ClampDimensionsToWorkingArea(double left, double top, double width, double height)
    {
        var screen = Screen.FromPoint(new System.Drawing.Point((int)left, (int)top));
        var area = screen.WorkingArea;
        return (Math.Min(width, area.Width), Math.Min(height, area.Height));
    }

    private static (double left, double top) ClampToWorkingArea(double left, double top, double width, double height)
    {
        var screen = Screen.FromPoint(new System.Drawing.Point((int)left, (int)top));
        var area = screen.WorkingArea;
        if (left + width < area.Left) left = area.Left;
        if (top + height < area.Top) top = area.Top;
        if (left > area.Right - 100) left = area.Left;
        if (top > area.Bottom - 100) top = area.Top;
        return (left, top);
    }
}