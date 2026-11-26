namespace GameClub.Server.Testing;

/// <summary>
/// Test-harness-only override for PC special-hour bands. Production never sets <see cref="Enabled"/>.
/// </summary>
public sealed class PcSpecialBandHarnessOptions
{
    public const string SectionName = "TestHarness:PcSpecialBand";

    public bool Enabled { get; set; }

    /// <summary>Tehran local start, HH:mm (24h).</summary>
    public string? PcNormalStart { get; set; }

    /// <summary>Tehran local end, HH:mm (24h).</summary>
    public string? PcNormalEnd { get; set; }

    public string? PcVipStart { get; set; }

    public string? PcVipEnd { get; set; }
}