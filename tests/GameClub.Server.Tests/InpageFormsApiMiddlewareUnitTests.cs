using GameClub.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GameClub.Server.Tests;

public sealed class InpageFormsApiMiddlewareUnitTests
{
    [Fact]
    public async Task aborted_ioexception_is_rethrown_without_error_log()
    {
        var collector = new TestLogCollector();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new TestLogCollectorProvider(collector)));
        var logger = loggerFactory.CreateLogger<ApiExceptionMiddleware>();
        RequestDelegate next = _ => throw new IOException("client reset");
        var middleware = new ApiExceptionMiddleware(next, logger);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/operator/probe";
        var cts = new CancellationTokenSource();
        context.RequestAborted = cts.Token;
        cts.Cancel();

        await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(context));

        var errorLogs = collector.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Empty(errorLogs);
        Assert.NotEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }
}