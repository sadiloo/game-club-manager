#if NET48
using System;
#endif
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests;

internal static class IntegrationTestCi
{
    public static bool IsActive =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    public static void FailIfActive(string message)
    {
        if (IsActive)
        {
            Assert.Fail(message);
        }
    }
}