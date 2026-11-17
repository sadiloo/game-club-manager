using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Services;

public static partial class GameRateTomanParser
{
    private const string InvalidRateMessage = "مقدار نرخ نامعتبر است؛ فقط رقم با جداکننده هزارگان مجاز است.";

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictAsciiDigits();

    [GeneratedRegex("^[0-9\u06F0-\u06F9\u0660-\u0669,\u060C\u066C]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedRateChars();

    public static long ParseRequired(JsonElement value, string? nonPositiveMessage = null)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", InvalidRateMessage);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var parsed = TryParseStrict(value.GetString() ?? string.Empty, out var toman);
            if (!parsed)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", InvalidRateMessage);
            }

            return toman;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            var raw = value.GetRawText();
            if (!StrictAsciiDigits().IsMatch(raw))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", InvalidRateMessage);
            }

            if (!value.TryGetInt64(out var toman) || toman <= 0)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "invalid_rate",
                    nonPositiveMessage ?? "نرخ ساعتی بازی باید بیشتر از صفر باشد.");
            }

            return toman;
        }

        throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", InvalidRateMessage);
    }

    public static long? ParseOptional(JsonElement value, string? nonPositiveMessage = null)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ParseRequired(value, nonPositiveMessage);
    }

    public static bool TryParseStrict(string? raw, out long toman)
    {
        toman = 0;
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!AllowedRateChars().IsMatch(trimmed))
        {
            return false;
        }

        var digits = new char[trimmed.Length];
        var n = 0;
        foreach (var ch in trimmed)
        {
            if (ch is ',' or '\u060C' or '\u066C')
            {
                continue;
            }

            var d = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= '\u06F0' and <= '\u06F9' => ch - '\u06F0',
                >= '\u0660' and <= '\u0669' => ch - '\u0660',
                _ => -1,
            };
            if (d < 0)
            {
                return false;
            }

            digits[n++] = (char)('0' + d);
        }

        if (n == 0)
        {
            return false;
        }

        var ascii = new string(digits, 0, n);
        if (!long.TryParse(ascii, NumberStyles.None, CultureInfo.InvariantCulture, out toman))
        {
            return false;
        }

        return toman > 0;
    }
}