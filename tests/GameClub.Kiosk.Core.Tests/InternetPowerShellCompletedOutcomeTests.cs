using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Core.Tests;

public sealed class InternetPowerShellCompletedOutcomeTests
{
    private const string ClixmlProgressBlob =
        "#< CLIXML\r\n"
        + "<Objs Version=\"1.1.3.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
        + "<Obj S=\"progress\" RefId=\"0\">"
        + "<TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T></TN>"
        + "<MS><I32 N=\"Id\">1</I32></MS></Obj></Objs>";

    [Fact]
    public void completed_outcome_exit_zero_with_clixml_stderr_and_ok_stdout_is_success()
    {
        var output = InternetPowerShellCompletedOutcome.ResolveOutput(
            exitCode: 0,
            stdout: "noise\nfirewall_script_ok\n",
            stderr: ClixmlProgressBlob);
        Assert.Equal("firewall_script_ok", output);
    }

    [Fact]
    public void completed_outcome_exit_nonzero_with_clixml_stderr_decodes_error_detail()
    {
        var clixml =
            "#< CLIXML\r\n"
            + "<Objs Version=\"1.1.3.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
            + "<S S=\"Error\">Access is denied</S></Objs>";
        var output = InternetPowerShellCompletedOutcome.ResolveOutput(1, "", clixml);
        Assert.Equal("apply_failed:powershell_error:Access is denied", output);
    }
}