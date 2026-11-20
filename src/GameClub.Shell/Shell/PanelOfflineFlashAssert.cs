using System;
using System.Collections.Generic;

namespace GameClub.Shell.Shell;

/// <summary>
/// Pure anti-flash measure for transient probe flap windows (B-1).
/// Offline = data: offline marker only; every other sample counts as non-offline (panel/error/blank/null).
/// </summary>
internal static class PanelOfflineFlashAssert
{
    public const int MinSampleCount = 10;
    public const double MinClassifiedFraction = 0.9;
    public const double MinOfflineFraction = 0.95;
    public const int MaxMillisecondsToFirstOffline = 10_000;
    public const int MaxPanelCycleDurationMilliseconds = 5_000;
    public const int DefaultSampleIntervalMilliseconds = 500;

    public enum SampleViewKind
    {
        Offline,
        NonOffline,
        NoWebView,
    }

    public sealed record FlashSample(SampleViewKind ViewKind, int ElapsedMillisecondsFromWindowStart);

    public sealed record FlashMeasureResult(
        bool Passes,
        int TotalSamples,
        int ClassifiedSamples,
        int OfflineSamples,
        int OfflineToPanelTransitions,
        int PanelToOfflineTransitions,
        int FlashCycles,
        bool LastSampleOffline,
        int MillisecondsToFirstOffline,
        int LongestPanelCycleMilliseconds,
        string? FailureReason);

    public static FlashMeasureResult Evaluate(
        IReadOnlyList<FlashSample> samples,
        int sampleIntervalMilliseconds = DefaultSampleIntervalMilliseconds)
    {
        if (samples.Count == 0)
        {
            return Fail(0, 0, 0, 0, 0, 0, false, -1, 0, "no samples");
        }

        var total = samples.Count;
        var classified = 0;
        var offlineSamples = 0;
        var offlineToPanel = 0;
        var panelToOffline = 0;
        bool? previousOffline = null;
        var lastOffline = false;
        int? firstOfflineMs = null;
        int? panelCycleStartMs = null;
        var longestPanelCycleMs = 0;
        var inPanelCycle = false;

        foreach (var sample in samples)
        {
            if (sample.ViewKind == SampleViewKind.NoWebView)
            {
                classified++;
                lastOffline = false;
                if (previousOffline == true)
                {
                    panelToOffline++;
                    if (panelCycleStartMs.HasValue)
                    {
                        var cycleMs = sample.ElapsedMillisecondsFromWindowStart - panelCycleStartMs.Value;
                        if (cycleMs > longestPanelCycleMs)
                        {
                            longestPanelCycleMs = cycleMs;
                        }
                    }

                    panelCycleStartMs = null;
                    inPanelCycle = false;
                }

                previousOffline = false;
                continue;
            }

            classified++;
            var isOffline = sample.ViewKind == SampleViewKind.Offline;
            if (isOffline)
            {
                offlineSamples++;
                firstOfflineMs ??= sample.ElapsedMillisecondsFromWindowStart;
            }

            if (previousOffline.HasValue)
            {
                if (previousOffline.Value && !isOffline)
                {
                    offlineToPanel++;
                    panelCycleStartMs = sample.ElapsedMillisecondsFromWindowStart;
                    inPanelCycle = true;
                }

                if (!previousOffline.Value && isOffline)
                {
                    panelToOffline++;
                    if (panelCycleStartMs.HasValue)
                    {
                        var cycleMs = sample.ElapsedMillisecondsFromWindowStart - panelCycleStartMs.Value;
                        if (cycleMs > longestPanelCycleMs)
                        {
                            longestPanelCycleMs = cycleMs;
                        }
                    }

                    panelCycleStartMs = null;
                    inPanelCycle = false;
                }
            }

            previousOffline = isOffline;
            lastOffline = isOffline;
        }

        if (inPanelCycle && panelCycleStartMs.HasValue)
        {
            var lastMs = samples[samples.Count - 1].ElapsedMillisecondsFromWindowStart;
            var cycleMs = lastMs - panelCycleStartMs.Value + sampleIntervalMilliseconds;
            if (cycleMs > longestPanelCycleMs)
            {
                longestPanelCycleMs = cycleMs;
            }
        }

        var cycles = Math.Min(offlineToPanel, panelToOffline);
        var classifiedFraction = (double)classified / total;
        var offlineFraction = (double)offlineSamples / total;

        if (total < MinSampleCount)
        {
            return Fail(total, classified, offlineSamples, offlineToPanel, panelToOffline, cycles, lastOffline,
                firstOfflineMs ?? -1, longestPanelCycleMs, $"total samples {total} < {MinSampleCount}");
        }

        if (classifiedFraction < MinClassifiedFraction)
        {
            return Fail(total, classified, offlineSamples, offlineToPanel, panelToOffline, cycles, lastOffline,
                firstOfflineMs ?? -1, longestPanelCycleMs,
                $"classified/total {classifiedFraction:F3} < {MinClassifiedFraction:F1}");
        }

        if (offlineFraction < MinOfflineFraction)
        {
            return Fail(total, classified, offlineSamples, offlineToPanel, panelToOffline, cycles, lastOffline,
                firstOfflineMs ?? -1, longestPanelCycleMs,
                $"offline/total {offlineFraction:F3} < {MinOfflineFraction:F2}");
        }

        if (firstOfflineMs is null || firstOfflineMs > MaxMillisecondsToFirstOffline)
        {
            return Fail(total, classified, offlineSamples, offlineToPanel, panelToOffline, cycles, lastOffline,
                firstOfflineMs ?? -1, longestPanelCycleMs,
                $"first offline at {firstOfflineMs ?? -1}ms > {MaxMillisecondsToFirstOffline}ms");
        }

        if (longestPanelCycleMs > MaxPanelCycleDurationMilliseconds)
        {
            return Fail(total, classified, offlineSamples, offlineToPanel, panelToOffline, cycles, lastOffline,
                firstOfflineMs.Value, longestPanelCycleMs,
                $"panel cycle {longestPanelCycleMs}ms > {MaxPanelCycleDurationMilliseconds}ms");
        }

        if (cycles > 1 || offlineToPanel > 1 || panelToOffline > 1)
        {
            return Fail(total, classified, offlineSamples, offlineToPanel, panelToOffline, cycles, lastOffline,
                firstOfflineMs.Value, longestPanelCycleMs,
                $"cycles={cycles} offline->panel={offlineToPanel} panel->offline={panelToOffline}");
        }

        if (!lastOffline)
        {
            return Fail(total, classified, offlineSamples, offlineToPanel, panelToOffline, cycles, lastOffline,
                firstOfflineMs.Value, longestPanelCycleMs, "last sample not offline");
        }

        return new FlashMeasureResult(
            true,
            total,
            classified,
            offlineSamples,
            offlineToPanel,
            panelToOffline,
            cycles,
            true,
            firstOfflineMs.Value,
            longestPanelCycleMs,
            null);
    }

    public static IReadOnlyList<FlashSample> SamplesFromSimSequence(
        string sequence,
        int sampleIntervalMilliseconds = DefaultSampleIntervalMilliseconds)
    {
        var samples = new List<FlashSample>(sequence.Length);
        for (var i = 0; i < sequence.Length; i++)
        {
            var kind = sequence[i] switch
            {
                'O' => SampleViewKind.Offline,
                'N' => SampleViewKind.NoWebView,
                _ => SampleViewKind.NonOffline,
            };
            samples.Add(new FlashSample(kind, i * sampleIntervalMilliseconds));
        }

        return samples;
    }

    private static FlashMeasureResult Fail(
        int total,
        int classified,
        int offline,
        int o2p,
        int p2o,
        int cycles,
        bool lastOffline,
        int firstOfflineMs,
        int longestCycleMs,
        string reason) =>
        new(false, total, classified, offline, o2p, p2o, cycles, lastOffline, firstOfflineMs, longestCycleMs, reason);
}