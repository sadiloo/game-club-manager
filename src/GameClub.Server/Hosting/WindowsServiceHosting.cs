using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;

namespace GameClub.Server.Hosting;

internal static class WindowsServiceHosting
{
    internal const string DefaultServerConfigPath = @"C:\ProgramData\Checkpoint\config\server.json";
    internal const string DefaultLogsDirectory = @"C:\ProgramData\Checkpoint\logs";

    public static void ConfigureServerJson(WebApplicationBuilder builder)
    {
        var overridePath = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_CONFIG");
        var path = string.IsNullOrWhiteSpace(overridePath) ? DefaultServerConfigPath : overridePath;
        if (File.Exists(path))
        {
            builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
        }
        else if (!string.IsNullOrWhiteSpace(overridePath))
        {
            builder.Configuration.AddJsonFile(overridePath, optional: true, reloadOnChange: false);
        }
    }

    public static void UseWindowsServiceWhenApplicable(WebApplicationBuilder builder)
    {
        if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
        {
            return;
        }

        builder.Host.UseWindowsService(options => options.ServiceName = "CheckpointServer");
        builder.Host.UseContentRoot(AppContext.BaseDirectory);
    }

    public static void ConfigureServiceFileLogging(WebApplicationBuilder builder)
    {
        if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
        {
            return;
        }

        var logsDir = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_LOGS_DIR") ?? DefaultLogsDirectory;
        Directory.CreateDirectory(logsDir);
        var logPath = Path.Combine(logsDir, "server-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                shared: true)
            .CreateLogger();

        builder.Host.UseSerilog();
    }
}