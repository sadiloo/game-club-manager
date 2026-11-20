using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Services;

public static class KioskStatusInlineApplicator
{
    public static void Apply(TextBlock status, KioskStatusPresentation presentation)
    {
        status.Text = string.Empty;
        status.Inlines.Clear();
        var muted = (Brush)status.FindResource("Kiosk.MutedBrush");
        var danger = (Brush)status.FindResource("Kiosk.DangerBrush");
        foreach (var seg in presentation.StatusSegments)
        {
            status.Inlines.Add(CreateRun(seg, muted));
        }

        if (presentation.ShowSeparator)
        {
            status.Inlines.Add(CreateRun(
                new KioskStatusRunSegment(KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.Sep), false),
                muted));
        }

        foreach (var seg in presentation.ErrorSegments)
        {
            status.Inlines.Add(CreateRun(seg, danger));
        }
    }

    public static string ReadCombinedText(TextBlock status)
    {
        if (status.Inlines.Count == 0)
        {
            return status.Text ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var inline in status.Inlines)
        {
            if (inline is Run run)
            {
                sb.Append(run.Text);
            }
        }

        return sb.ToString();
    }

    private static Run CreateRun(KioskStatusRunSegment seg, Brush brush)
    {
        var run = new Run(seg.Text) { Foreground = brush, FontSize = 16 };
        if (seg.IsolateLatin)
        {
            run.FlowDirection = FlowDirection.LeftToRight;
        }

        return run;
    }
}