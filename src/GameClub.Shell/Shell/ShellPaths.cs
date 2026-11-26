using System;
using System.IO;

namespace GameClub.Shell.Shell;

internal static class ShellPaths
{
    public const string PanelOrigin = "http://127.0.0.1:5000";
    public const string PanelUrl = PanelOrigin + "/operator/";
    public static string? AllowedPanelOriginOverride { get; set; }
    public static string ResolveAllowedPanelOrigin() => AllowedPanelOriginOverride ?? PanelOrigin;
    public const string ServiceName = "CheckpointServer";
    public const string ServerConfigPath = @"C:\ProgramData\Checkpoint\config\server.json";
    public static string ProgramDataLogs =>
        Environment.GetEnvironmentVariable("CHECKPOINT_PROGRAMDATA_LOGS")
        ?? @"C:\ProgramData\Checkpoint\logs";
    public static string ShellRoot =>
        Environment.GetEnvironmentVariable("CHECKPOINT_SHELL_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Checkpoint", "Shell");

    public static string WindowJsonPath => Path.Combine(ShellRoot, "window.json");
    public static string ShellLogsDir => Path.Combine(ShellRoot, "logs");
    public static string WebView2UserData => Path.Combine(ShellRoot, "WebView2");
    public static string AutostartValueName => "CheckpointShell";
}