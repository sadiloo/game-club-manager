using GameClub.Server.Services;

namespace GameClub.Server.Tests;

public sealed class PcInternetSnapshotSupportTests
{
    [Fact]
    public void SegmentKeySupport_thirty_splits_are_unique()
    {
        var keys = Enumerable.Range(0, 30).Select(SegmentKeySupport.Allocate).ToList();
        Assert.Equal(30, keys.Distinct().Count());
    }
}