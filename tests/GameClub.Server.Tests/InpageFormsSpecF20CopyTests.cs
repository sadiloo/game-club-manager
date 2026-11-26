namespace GameClub.Server.Tests;

/// <summary>F20 final copy from spec-mvp-inpage-forms v2.4 §F20 (sha256 00e79a54…b2aa2).</summary>
public sealed class InpageFormsSpecF20CopyTests
{
    private static string OperatorAppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    public static TheoryData<string> F20FinalCopy { get; } = new()
    {
        "رند کردن مبلغ",
        "تنظیم رند کردن ذخیره شد.",
        "رند کردن مبلغ فاکتور",
        "اگر روشن باشد، مبلغ قابل دریافت رند می‌شود.",
        "گام رند کردن (تومان)",
        "مبلغ به مضرب این عدد رند می‌شود، مثلاً ۵٬۰۰۰.",
        "رند کردن پرداخت از کیف پول",
        "اگر خاموش باشد، پرداخت از کیف پول رند نمی‌شود.",
        "رند نکردن پرداخت کیف پول کمتر از (تومان)",
        "پرداخت از کیف پول کمتر از این مبلغ رند نمی‌شود.",
        "ثبت باقی‌مانده به‌صورت نسیه",
        "اگر موجودی کیف پول کم باشد، اپراتور باقی‌مانده را نقد یا نسیه می‌گیرد. اگر خاموش باشد، باقی‌مانده فقط نقد است.",
        "نسیه",
        "پرداخت نقد",
        "خالی یا ۰ یعنی نسیه ممنوع. فقط برای کنسول و میز.",
    };

    [Theory]
    [MemberData(nameof(F20FinalCopy))]
    public void f20_spec_v24_final_strings_in_cp_text(string expected)
    {
        var js = File.ReadAllText(OperatorAppJsPath());
        Assert.Contains(expected, js, StringComparison.Ordinal);
    }

    [Fact]
    public void f20_rounding_settings_uses_cp_text_f20()
    {
        var js = File.ReadAllText(OperatorAppJsPath());
        Assert.Contains("title: CP_TEXT.f20.title", js, StringComparison.Ordinal);
        Assert.Contains("kind: \"switch\"", js, StringComparison.Ordinal);
    }
}