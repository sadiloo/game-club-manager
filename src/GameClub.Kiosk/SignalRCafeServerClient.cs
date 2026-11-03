using GameClub.Kiosk.Core;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk;

/// <summary>
/// Future ASP.NET Core SignalR hub client. The server does not exist yet.
/// On Windows 7, <see cref="SignalRTransports"/> omits WebSockets because
/// <c>ClientWebSocket</c> throws <see cref="PlatformNotSupportedException"/>.
/// Server-Sent Events and long polling remain available on every supported OS.
/// </summary>
public sealed class SignalRCafeServerClient : ICafeServer, IAsyncDisposable
{
    private readonly HubConnection _connection;

    public SignalRCafeServerClient(string hubUrl, bool clientWebSocketSupported)
    {
        var mask = SignalRTransports.Select(clientWebSocketSupported);
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.Transports = Map(mask))
            .Build();
    }

    public async Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await EnsureStarted(cancellationToken).ConfigureAwait(false);
        var dto = await _connection.InvokeAsync<LoginWire>("Login", username, password, stationId, at, cancellationToken).ConfigureAwait(false);
        return ToLogin(dto);
    }

    public async Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken)
    {
        await EnsureStarted(cancellationToken).ConfigureAwait(false);
        var dto = await _connection.InvokeAsync<LogoutWire>("Logout", report, cancellationToken).ConfigureAwait(false);
        return new LogoutResult
        {
            Accepted = dto.Accepted,
            Offline = dto.Offline,
            Message = dto.Message,
            Revision = dto.Revision,
            Account = dto.Account is null ? null : AccountCodec.ToAccount(dto.Account),
        };
    }

    public async Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        await EnsureStarted(cancellationToken).ConfigureAwait(false);
        var games = await _connection.InvokeAsync<List<GameEntry>>("GetGameCatalog", cancellationToken).ConfigureAwait(false);
        return games ?? [];
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task EnsureStarted(CancellationToken cancellationToken)
    {
        if (_connection.State == HubConnectionState.Disconnected)
        {
            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static HttpTransportType Map(int mask)
    {
        var value = HttpTransportType.None;
        if (SignalRTransports.UsesWebSockets(mask))
        {
            value |= HttpTransportType.WebSockets;
        }

        if (SignalRTransports.UsesServerSentEvents(mask))
        {
            value |= HttpTransportType.ServerSentEvents;
        }

        if (SignalRTransports.UsesLongPolling(mask))
        {
            value |= HttpTransportType.LongPolling;
        }

        return value;
    }

    private static LoginResult ToLogin(LoginWire? dto)
    {
        if (dto is null || !dto.Accepted || dto.Account is null || string.IsNullOrWhiteSpace(dto.SessionId))
        {
            return LoginResult.Fail(dto?.Message ?? "ورود ناموفق بود.");
        }

        return new LoginResult
        {
            Accepted = true,
            SessionId = dto.SessionId,
            MemberName = dto.MemberName,
            MemberAge = dto.MemberAge,
            Revision = dto.Revision,
            HourlyToman = dto.HourlyToman,
            Account = AccountCodec.ToAccount(dto.Account),
            Catalog = dto.Catalog ?? [],
        };
    }

    private sealed class LoginWire
    {
        public bool Accepted { get; set; }

        public string? Message { get; set; }

        public string? SessionId { get; set; }

        public string? MemberName { get; set; }

        public int? MemberAge { get; set; }

        public long Revision { get; set; }

        public long HourlyToman { get; set; }

        public AccountSnapshot? Account { get; set; }

        public List<GameEntry>? Catalog { get; set; }
    }

    private sealed class LogoutWire
    {
        public bool Accepted { get; set; }

        public bool Offline { get; set; }

        public string? Message { get; set; }

        public long Revision { get; set; }

        public AccountSnapshot? Account { get; set; }
    }
}
