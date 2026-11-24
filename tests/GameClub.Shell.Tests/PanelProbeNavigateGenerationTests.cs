using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelProbeNavigateGenerationTests
{
    [Fact]
    public void stale_probe_generation_blocks_late_navigate_after_interleaving_invalidation()
    {
        var captured = PanelProbeNavigateGeneration.CaptureForProbe();
        PanelProbeNavigateGeneration.Invalidate();
        Assert.False(PanelProbeNavigateGeneration.IsProbeNavigateStillValid(captured));
    }

    [Fact]
    public void probe_generation_stays_valid_when_no_interleaving_navigation()
    {
        var captured = PanelProbeNavigateGeneration.CaptureForProbe();
        Assert.True(PanelProbeNavigateGeneration.IsProbeNavigateStillValid(captured));
    }
}