namespace GameClub.Server.Tests;

/// <summary>Shared assertion for Playwright-recorded native <c>alert</c>/<c>confirm</c>/<c>prompt</c> calls.</summary>
internal static class NativeDialogRecorderAssert
{
    public static void AssertNoUnexpectedNativeDialogs(IReadOnlyCollection<string> calls)
    {
        Assert.True(
            calls.Count == 0,
            calls.Count == 0
                ? null
                : $"unexpected native dialog(s): {string.Join("; ", calls)}");
    }
}