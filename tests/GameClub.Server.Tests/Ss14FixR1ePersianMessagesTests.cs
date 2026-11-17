namespace GameClub.Server.Tests;

public sealed class Ss14FixR1ePersianMessagesTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.GetFullPath(Path.Combine(parts)));

    [Fact]
    public void FixR1e_game_tariff_delete_not_found_message_is_persian()
    {
        var src = Read(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GameClub.Server", "Services", "GameTariffService.cs");
        Assert.Contains("\"بازی یافت نشد.\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Game not found.\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void FixR1e_session_game_support_user_messages_are_persian()
    {
        var src = Read(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GameClub.Server", "Services", "SessionGameSupport.cs");
        Assert.Contains("شناسهٔ دستور الزامی است", src, StringComparison.Ordinal);
        Assert.Contains("نسخهٔ جلسه", src, StringComparison.Ordinal);
        Assert.Contains("جلسه در حال به‌روزرسانی است", src, StringComparison.Ordinal);
        Assert.Contains("جلسه یافت نشد", src, StringComparison.Ordinal);
        Assert.Contains("تغییر بازی فقط برای ایستگاه کنسول مجاز است", src, StringComparison.Ordinal);
        Assert.Contains("جلسه به‌روز شده است", src, StringComparison.Ordinal);
        Assert.Contains("برای این جلسه قبلاً صورتحساب باز شده است", src, StringComparison.Ordinal);
        Assert.Contains("جلسه بخش بازی باز ندارد", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Command id is required", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Session not found", src, StringComparison.Ordinal);
    }
}