using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using GameClub.Server.Operator;
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
            if (TryParseStrict(value.GetString(), out var parsed))
            {
                if (parsed > OperatorMoneyValidation.MaxToman)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "مبلغ باید عدد صحیح مثبت باشد.");
                }

                return parsed;
            }

            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", InvalidRateMessage);
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

            if (toman > OperatorMoneyValidation.MaxToman)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "مبلغ باید عدد صحیح مثبت باشد.");
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

        if (!HasValidThousandsGrouping(trimmed))
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
        if (ascii.Length > 1 && ascii[0] == '0')
        {
            return false;
        }

        if (!long.TryParse(ascii, NumberStyles.None, CultureInfo.InvariantCulture, out toman))
        {
            return false;
        }

        return toman > 0;
    }

    private static bool HasValidThousandsGrouping(string trimmed)
    {
        var groups = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in trimmed)
        {
            if (ch is ',' or '\u060C' or '\u066C')
            {
                if (current.Length == 0)
                {
                    return false;
                }

                groups.Add(current.ToString());
                current.Clear();
            }
            else if (ch is >= '0' and <= '9' or >= '\u06F0' and <= '\u06F9' or >= '\u0660' and <= '\u0669')
            {
                current.Append(ch);
            }
            else
            {
                return false;
            }
        }

        if (current.Length == 0 && groups.Count > 0)
        {
            return false;
        }

        if (current.Length > 0)
        {
            groups.Add(current.ToString());
        }

        if (groups.Count == 0)
        {
            return false;
        }

        foreach (var group in groups)
        {
            if (group.Length == 0)
            {
                return false;
            }
        }

        if (groups.Count == 1)
        {
            return true;
        }

        if (groups[0].Length is < 1 or > 3)
        {
            return false;
        }

        for (var i = 1; i < groups.Count; i++)
        {
            if (groups[i].Length != 3)
            {
                return false;
            }
        }

        return true;
    }
}