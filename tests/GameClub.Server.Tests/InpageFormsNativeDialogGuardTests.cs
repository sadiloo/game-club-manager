namespace GameClub.Server.Tests;

public sealed class InpageFormsNativeDialogGuardTests
{
    [Fact]
    public void native_dialog_recorder_assert_fails_when_dialog_was_recorded()
    {
        var calls = new List<string> { "alert:test-dialog" };
        var failure = Record.Exception(() => NativeDialogRecorderAssert.AssertNoUnexpectedNativeDialogs(calls));
        Assert.NotNull(failure);
        Assert.Contains("unexpected native dialog", failure.Message, StringComparison.Ordinal);
        Assert.Contains("alert:test-dialog", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void native_dialog_recorder_assert_passes_when_empty()
    {
        var failure = Record.Exception(() => NativeDialogRecorderAssert.AssertNoUnexpectedNativeDialogs([]));
        Assert.Null(failure);
    }
}