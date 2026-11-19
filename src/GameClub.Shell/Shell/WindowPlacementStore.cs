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
    public static void Apply(Window window)
    {
        var placement = Load();
        window.Width = placement.Width;
        window.Height = placement.Height;
        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            window.WindowState = WindowState.Normal;
            var (left, top) = ClampToWorkingArea(placement.Left, placement.Top, placement.Width, placement.Height);
            window.Left = left;
            window.Top = top;
        }
    }

    public static void Save(Window window, bool trayHintShown)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        var placement = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = window.WindowState == WindowState.Maximized,
            TrayHintShown = trayHintShown,
        };
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

            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(ShellPaths.WindowJsonPath)) ?? new WindowPlacement();
        }
        catch
        {
            return new WindowPlacement();
        }
    }

    internal static (double left, double top) ClampPositionForTests(double left, double top, double width, double height)
        => ClampToWorkingArea(left, top, width, height);

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