namespace GameClub.Server.Tests;

internal static class BrowserInstallGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _ready;

    public static async Task EnsureChromiumAsync()
    {
        if (_ready)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (_ready)
            {
                return;
            }

            var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exit != 0)
            {
                throw new InvalidOperationException($"Playwright browser install failed with exit code {exit}.");
            }

            _ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
