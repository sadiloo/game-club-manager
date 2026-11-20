using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Services;

public static class KioskStatusInlineApplicator
{
    private const string MutedBrushKey = "Kiosk.MutedBrush";
    private const string DangerBrushKey = "Kiosk.DangerBrush";

    public static void Apply(TextBlock status, KioskStatusPresentation presentation)
    {
        var combined = presentation.ToCombinedText();
        status.Text = combined;
        status.Inlines.Clear();
        foreach (var seg in presentation.StatusSegments)
        {
            status.Inlines.Add(CreateRun(seg, MutedBrushKey));
        }

        if (presentation.ShowSeparator)
        {
            status.Inlines.Add(CreateRun(
                new KioskStatusRunSegment(KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.Sep), false),
                MutedBrushKey));
        }

        foreach (var seg in presentation.ErrorSegments)
        {
            status.Inlines.Add(CreateRun(seg, DangerBrushKey));
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

    private static Run CreateRun(KioskStatusRunSegment seg, string brushKey)
    {
        var run = new Run(seg.Text) { FontSize = 16 };
        run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        if (seg.IsolateLatin)
        {
            run.FlowDirection = FlowDirection.LeftToRight;
        }

        return run;
    }
}