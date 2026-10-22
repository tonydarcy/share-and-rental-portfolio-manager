namespace PortfolioManager.Core.Clients;

/// <summary>Australian Business Number and Australian Company Number check digits (ATO and ASIC rules).</summary>
public static class BusinessNumbers
{
    private static readonly int[] AbnWeights = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];
    private static readonly int[] AcnWeights = [8, 7, 6, 5, 4, 3, 2, 1];

    /// <summary>The number with spaces (and nothing else) taken out; null when nothing was entered.</summary>
    public static string? Clean(string? value)
    {
        var text = value?.Replace(" ", "").Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// 11 digits; take 1 from the first digit, weight each digit by 10, 1, 3, 5 ... 19,
    /// and the total must divide by 89.
    /// </summary>
    public static bool IsValidAbn(string? value)
    {
        var abn = Clean(value);
        if (abn is not { Length: 11 } || !abn.All(char.IsAsciiDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 11; i++)
            sum += ((abn[i] - '0') - (i == 0 ? 1 : 0)) * AbnWeights[i];
        return sum % 89 == 0;
    }

    /// <summary>
    /// 9 digits; weight the first eight by 8 down to 1, and the last digit is
    /// (10 - total mod 10) mod 10.
    /// </summary>
    public static bool IsValidAcn(string? value)
    {
        var acn = Clean(value);
        if (acn is not { Length: 9 } || !acn.All(char.IsAsciiDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 8; i++)
            sum += (acn[i] - '0') * AcnWeights[i];
        return (10 - sum % 10) % 10 == acn[8] - '0';
    }

    /// <summary>A company's ABN is its ACN with two check digits in front.</summary>
    public static bool AbnMatchesAcn(string? abn, string? acn) =>
        Clean(abn) is { Length: 11 } a && Clean(acn) is { Length: 9 } c && a.EndsWith(c, StringComparison.Ordinal);

    /// <summary>"51 824 753 556"; anything that isn't 11 digits is shown as entered.</summary>
    public static string FormatAbn(string? value) =>
        Clean(value) is { Length: 11 } a && a.All(char.IsAsciiDigit) ? $"{a[..2]} {a[2..5]} {a[5..8]} {a[8..]}" : value?.Trim() ?? "";

    /// <summary>"000 000 019"; anything that isn't 9 digits is shown as entered.</summary>
    public static string FormatAcn(string? value) =>
        Clean(value) is { Length: 9 } a && a.All(char.IsAsciiDigit) ? $"{a[..3]} {a[3..6]} {a[6..]}" : value?.Trim() ?? "";
}
