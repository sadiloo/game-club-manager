namespace GameClub.Server.Tests;

public sealed class InpageFormsNativeDialogGuardTests
{
    [Fact]
    public void native_dialog_recorder_fails_via_assert_not_handler_throw()
    {
        var calls = new List<string>();
        void Handler(string type, string message) => calls.Add($"{type}:{message}");

        Handler("alert", "probe");
        Assert.Single(calls);
        var failure = Record.Exception(() => Assert.Empty(calls));
        Assert.NotNull(failure);
        Assert.IsNotType<InvalidOperationException>(failure);
    }
}