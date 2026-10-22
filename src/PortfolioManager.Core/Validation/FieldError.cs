namespace PortfolioManager.Core.Validation;

/// <summary>A problem with one form field. Field is the form property's name.</summary>
public sealed record FieldError(string Field, string Message);

/// <summary>Either a value ready to save, or the reasons it can't be saved.</summary>
public sealed record Validated<T>(T? Value, IReadOnlyList<FieldError> Errors) where T : class
{
    public bool IsValid => Value is not null && Errors.Count == 0;
}

/// <summary>Shared checks for the entry forms. Limits match the legacy columns and proc parameters.</summary>
public static class FormRules
{
    /// <summary>The largest amount a decimal(10,2) column can hold.</summary>
    public const decimal MaxMoney = 99_999_999.99m;

    public static readonly DateOnly EarliestDate = new(1900, 1, 1);

    /// <summary>
    /// The latest calendar date anywhere right now, so a trade made "today" in Australia
    /// isn't rejected as future-dated by a server running on UTC.
    /// </summary>
    public static DateOnly LatestToday(DateTime utcNow) => DateOnly.FromDateTime(utcNow.AddHours(14));

    public static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static void MaxLength(List<FieldError> errors, string field, string label, string? value, int max)
    {
        if (value is not null && value.Length > max)
            errors.Add(new(field, $"{label} must be {max} characters or fewer."));
    }

    internal static bool IsCurrencyCode(string value) =>
        value.Length == 3 && value.All(c => c is >= 'A' and <= 'Z');

    internal static bool HasMoreThanCents(decimal value) => value != Math.Round(value, 2);
}
