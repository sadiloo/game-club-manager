#if NET48
#nullable disable
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

public sealed class GuardWatchdogTimelineReaderTests
{
    [Fact]
    public void pipe_request_handled_regex_matches_real_watchdog_lines_with_thread_field()
    {
        Assert.True(GuardWatchdogTimelineReader.MatchesPipeRequestHandledTicket(
            "2025-11-20T20:07:58.7860468Z GUARD_TIMELINE event=pipe_request_handled thread=15 ticket=-1",
            -1));
        Assert.True(GuardWatchdogTimelineReader.MatchesPipeRequestHandledTicket(
            "2025-11-20T20:08:17.0794021Z GUARD_TIMELINE event=pipe_request_handled thread=16 ticket=0",
            0));
    }

    [Fact]
    public void pipe_request_handled_regex_rejects_fragment_without_thread()
    {
        Assert.False(GuardWatchdogTimelineReader.MatchesPipeRequestHandledTicket(
            "GUARD_TIMELINE event=pipe_request_handled ticket=-1",
            -1));
        Assert.False(GuardWatchdogTimelineReader.MatchesPipeRequestHandledTicket(
            "GUARD_TIMELINE event=pipe_request_handled thread=15 ticket=1",
            0));
    }
}
#endif