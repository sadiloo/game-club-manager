using GameClub.Kiosk.Core.Offline;

namespace GameClub.Kiosk.Core.Tests.Fakes;

internal sealed class ThrowingOfflineEventQueue : IOfflineEventQueue
{
    private readonly HashSet<string> _throwOnTypes;

    public ThrowingOfflineEventQueue(params string[] throwOnTypes)
    {
        _throwOnTypes = new HashSet<string>(throwOnTypes, StringComparer.Ordinal);
    }

    public int EnqueueCallCount { get; private set; }

    public Task EnqueueAsync(Guid eventId, string type, string payloadJson, DateTime createdUtc, CancellationToken ct = default)
    {
        EnqueueCallCount++;
        if (_throwOnTypes.Contains(type))
        {
            throw new InvalidOperationException($"Simulated enqueue failure for {type}.");
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueuedEvent>> PendingAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<QueuedEvent>>(Array.Empty<QueuedEvent>());

    public Task AckAsync(Guid eventId, CancellationToken ct = default) => Task.CompletedTask;

    public void Close()
    {
    }
}