namespace GameClub.Kiosk.Core;

public static class PersianFormat
{
    private const string Digits = "۰۱۲۳۴۵۶۷۸۹";

    public static string Money(long toman)
    {
        if (toman < 0)
        {
            toman = 0;
        }

        return ToPersianDigits(Group(toman.ToString(System.Globalization.CultureInfo.InvariantCulture))) + " تومان";
    }

    public static string Time(long? remainingSeconds, bool inGrace)
    {
        if (remainingSeconds is null)
        {
            return "نامحدود";
        }

        var total = remainingSeconds.Value < 0 ? 0 : remainingSeconds.Value;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        string text;
        if (hours > 0 && minutes > 0)
        {
            text = ToPersianDigits(hours.ToString()) + " ساعت و " + ToPersianDigits(minutes.ToString()) + " دقیقه";
        }
        else if (hours > 0)
        {
            text = ToPersianDigits(hours.ToString()) + " ساعت";
        }
        else if (minutes > 0 && seconds > 0)
        {
            text = ToPersianDigits(minutes.ToString()) + " دقیقه و " + ToPersianDigits(seconds.ToString()) + " ثانیه";
        }
        else if (minutes > 0)
        {
            text = ToPersianDigits(minutes.ToString()) + " دقیقه";
        }
        else
        {
            text = ToPersianDigits(seconds.ToString()) + " ثانیه";
        }

        return inGrace ? "مهلت: " + text : text;
    }

    public static string ToPersianDigits(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '0' and <= '9')
            {
                chars[i] = Digits[chars[i] - '0'];
            }
        }

        return new string(chars);
    }

    private static string Group(string digits)
    {
        if (digits.Length <= 3)
        {
            return digits;
        }

        var parts = new List<string>();
        for (var end = digits.Length; end > 0; end -= 3)
        {
            var start = Math.Max(0, end - 3);
            parts.Add(digits.Substring(start, end - start));
        }

        parts.Reverse();
        return string.Join("٬", parts);
    }
}
