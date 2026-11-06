namespace GameClub.Kiosk.Core;

/// <summary>
/// A dead hub, a timeout, or a TLS failure becomes <see cref="LoginResult.Disconnected"/>
/// or an offline logout. Callers on the UI thread must not see the exception.
/// </summary>
public sealed class SafeCafeServer : ICafeServer
{
    private readonly ICafeServer _inner;

    public SafeCafeServer(ICafeServer inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.LoginAsync(username, password, stationId, at, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return LoginResult.Disconnected();
        }
    }

    public async Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.LogoutAsync(report, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new LogoutResult { Offline = true, Message = "ارتباط با سرور قطع است." };
        }
    }

    public async Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
