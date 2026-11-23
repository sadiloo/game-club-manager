using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskServerUnreachableMessageTests
{
    [Fact]
    public void server_unreachable_message_key_is_registered_with_todo_uiux_placeholder()
    {
        Assert.Equal(
            "kiosk.error.serverUnreachable",
            KioskStatusMessages.Keys.ServerUnreachable);
        var text = KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.ServerUnreachable);
        Assert.Contains("TODO_UIUX", text, StringComparison.Ordinal);
        Assert.Contains("F8", text, StringComparison.Ordinal);
        Assert.Contains("admin.server", text, StringComparison.Ordinal);
    }

    [Fact]
    public void socket_exception_classifies_as_unreachable()
    {
        var ex = new SocketException();
        Assert.True(KioskServerTransportErrors.IsUnreachable(ex));
        Assert.Equal(KioskStatusMessages.Keys.ServerUnreachable, KioskServerTransportErrors.TryGetMessageKey(ex));
    }

    [Fact]
    public void http_request_exception_without_status_classifies_as_unreachable()
    {
        var ex = new HttpRequestException("connection refused");
        Assert.True(KioskServerTransportErrors.IsUnreachable(ex));
    }

    [Fact]
    public void login_unreachable_maps_to_server_unreachable_key_not_http_generic()
    {
        var key = KioskLoginUserMessages.ResolveFailureKey(
            null,
            0,
            KioskLoginUserMessages.LoginTransportFailure.Unreachable);
        Assert.Equal(KioskStatusMessages.Keys.ServerUnreachable, key);
        Assert.Contains("TODO_UIUX", KioskLoginUserMessages.DisplayText(key), StringComparison.Ordinal);
    }

    [Fact]
    public void login_timeout_maps_to_timeout_key_not_server_unreachable()
    {
        var key = KioskLoginUserMessages.ResolveFailureKey(
            null,
            0,
            KioskLoginUserMessages.LoginTransportFailure.Timeout);
        Assert.Equal(KioskLoginUserMessages.Keys.Timeout, key);
        Assert.Equal("سرور دیر جواب داد. لطفاً دوباره امتحان کنید.", KioskLoginUserMessages.DisplayText(key));
    }

    [Fact]
    public void login_http_5xx_without_transport_keeps_http_generic_key()
    {
        var key = KioskLoginUserMessages.ResolveFailureKey(responseBody: null, httpStatus: 503);
        Assert.Equal(KioskLoginUserMessages.Keys.HttpGeneric, key);
        Assert.DoesNotContain("TODO_UIUX", KioskLoginUserMessages.DisplayText(key), StringComparison.Ordinal);
    }

    [Fact]
    public void hub_last_error_detail_prefix_marks_unreachable_transport()
    {
        var detail = KioskServerTransportErrors.FormatDetail(new HttpRequestException("connection refused"));
        Assert.True(KioskServerTransportErrors.IsUnreachableDetail(detail));
    }

    [Fact]
    public void http_client_timeout_is_timeout_not_unreachable()
    {
        var timeout = new TimeoutException("timed out");
        var ex = new TaskCanceledException("canceled", timeout);
        Assert.True(KioskServerTransportErrors.IsTimeout(ex));
        Assert.False(KioskServerTransportErrors.IsUnreachable(ex));
        Assert.Equal(KioskLoginUserMessages.Keys.Timeout, KioskServerTransportErrors.TryGetMessageKey(ex));
    }

    [Fact]
    public void http_request_with_timeout_web_exception_is_timeout_not_unreachable()
    {
        var web = new WebException("timeout", null, WebExceptionStatus.Timeout, null);
        var ex = new HttpRequestException("request failed", web);
        Assert.True(KioskServerTransportErrors.IsTimeout(ex));
        Assert.False(KioskServerTransportErrors.IsUnreachable(ex));
        var detail = KioskServerTransportErrors.FormatDetail(ex);
        Assert.True(KioskServerTransportErrors.IsTimeoutDetail(detail));
    }

    [Fact]
    public void status_presentation_shows_server_unreachable_transient_key()
    {
        var state = KioskStationStatus.ResolveState(
            "PC-1",
            KioskTransportPhase.LastError,
            hubTransportConnected: false,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            hasOpenSession: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: null,
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail: KioskServerTransportErrors.FormatDetail(new HttpRequestException("name not resolved")));

        var presentation = KioskStatusPresentationBuilder.Build(
            state,
            loginErrorText: null,
            transientErrorKey: KioskStatusMessages.Keys.ServerUnreachable);

        Assert.Contains("TODO_UIUX", presentation.ToCombinedText(), StringComparison.Ordinal);
    }
}