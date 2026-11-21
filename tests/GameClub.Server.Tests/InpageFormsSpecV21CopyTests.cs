namespace GameClub.Server.Tests;

/// <summary>Exact final strings from spec-mvp-inpage-forms v2.1 §11 (sha256 4533d43f…).</summary>
public sealed class InpageFormsSpecV21CopyTests
{
    private static string OperatorAppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    public static TheoryData<string> F14FinalCopy { get; } = new()
    {
        "جدول سطح‌ها",
        "هر مشتری بالاترین سطحی را می‌گیرد که به حداقل ساعت بازی‌اش رسیده است.",
        "نام سطح",
        "حداقل ساعت بازی",
        "درصد تخفیف",
        "افزودن سطح",
        "حذف",
        "ذخیرهٔ جدول",
        "در حال بارگذاری…",
        "جدول سطح‌ها بارگذاری نشد.",
        "هنوز سطحی نیست.",
        "با «افزودن سطح» اولین سطح را بسازید.",
        "دست‌کم یک سطح لازم است.",
        "نام سطح را وارد کنید.",
        "این نام برای سطح دیگری هم آمده است.",
        "حداقل ساعت را وارد کنید.",
        "باید از سطح قبلی ({h} ساعت) بیشتر باشد.",
        "درصد را وارد کنید.",
        "درصد باید بین ۰ و ۱۰۰ باشد.",
        "جدول سطح‌ها درست نیست. نام‌ها، ساعت‌ها و درصدها را بررسی کنید.",
        "فقط مدیر یا مالک می‌تواند جدول سطح‌ها را عوض کند.",
        "جدول سطح‌ها ذخیره نشد. دوباره تلاش کنید.",
        "جدول سطح‌ها ذخیره شد.",
        "{n} خانه درست نیست. خانه‌های قرمز را درست کنید.",
        "تلاش دوباره",
    };

    [Theory]
    [MemberData(nameof(F14FinalCopy))]
    public void f14_spec_v21_final_strings_in_cp_text(string expected)
    {
        var js = File.ReadAllText(OperatorAppJsPath());
        Assert.Contains(expected, js, StringComparison.Ordinal);
    }

    [Fact]
    public void f14_level_editor_sets_name_via_dom_not_unescaped_innerhtml()
    {
        var js = File.ReadAllText(OperatorAppJsPath());
        Assert.Contains("nameInp.value = row.name", js, StringComparison.Ordinal);
        Assert.Contains("row.name ?? \"\"", js, StringComparison.Ordinal);
        var fnStart = js.IndexOf("function renderLevelLadderEditor", StringComparison.Ordinal);
        Assert.True(fnStart >= 0);
        var fnEnd = js.IndexOf("function readLevelLadderEditor", fnStart, StringComparison.Ordinal);
        Assert.True(fnEnd > fnStart);
        var ladderEditor = js[fnStart..fnEnd];
        Assert.DoesNotMatch("innerHTML\\s*=", ladderEditor);
        Assert.Contains("CP_TEXT.f14", js, StringComparison.Ordinal);
    }

    [Fact]
    public void f12_birthday_settings_uses_cp_text_f12_not_f14()
    {
        var js = File.ReadAllText(OperatorAppJsPath());
        Assert.Contains("title: CP_TEXT.f12.title", js, StringComparison.Ordinal);
        Assert.Contains("درصد تخفیف تولد", js, StringComparison.Ordinal);
        Assert.Contains("تعداد روزها", js, StringComparison.Ordinal);
    }
}