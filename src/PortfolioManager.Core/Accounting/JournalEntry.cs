using PortfolioManager.Core.SalesTax;

namespace PortfolioManager.Core.Accounting;

/// <summary>One line of the manual journal form.</summary>
public sealed class JournalLineInput
{
    public int AccountId { get; set; }
    public string? Description { get; set; }
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }

    /// <summary>
    /// A GST / VAT / sales tax code. With one, Debit or Credit is the amount including tax, and the
    /// tax is split out to the tax control account when the journal is saved.
    /// </summary>
    public int? TaxCodeId { get; set; }

    /// <summary>The tax within the amount, when it isn't the code's rate (e.g. the invoice rounds differently). Blank: worked out.</summary>
    public decimal? Tax { get; set; }

    /// <summary>Tracking options (e.g. a department and a project), one from each of the portfolio's two tracking categories.</summary>
    public int? Tracking1Id { get; set; }
    public int? Tracking2Id { get; set; }

    /// <summary>A row with no account and no amounts is just an unused row of the form.</summary>
    public bool IsBlank => AccountId <= 0 && (Debit ?? 0) == 0 && (Credit ?? 0) == 0 && string.IsNullOrWhiteSpace(Description);

    public decimal Gross => (Debit ?? 0) + (Credit ?? 0);

    /// <summary>The tax this line will split out: the amount entered, or the code's rate applied to the amount.</summary>
    public decimal TaxAmount(IReadOnlyDictionary<int, TaxCode> codes) =>
        TaxCodeId is { } id && codes.TryGetValue(id, out var code)
            ? Tax ?? TaxMath.TaxInGross(Gross, code.Rate)
            : 0;
}

/// <summary>A manual journal as entered on the journal entry page (JournalEntry.aspx).</summary>
public sealed class JournalInput
{
    public const int MaxLines = 100;

    public DateTime Date { get; set; } = DateTime.Today;
    public string Description { get; set; } = "";
    public List<JournalLineInput> Lines { get; set; } = [];

    /// <summary>The rows that will be saved, in order.</summary>
    public IReadOnlyList<JournalLineInput> UsedLines => Lines.Where(l => !l.IsBlank).ToList();

    public decimal TotalDebit => UsedLines.Sum(l => l.Debit ?? 0);
    public decimal TotalCredit => UsedLines.Sum(l => l.Credit ?? 0);

    /// <summary>
    /// A journal must balance to the cent, have at least two lines, and post only to the
    /// portfolio's postable accounts (<paramref name="allowedAccountIds"/>). Each line is
    /// either a debit or a credit, in dollars and cents.
    /// </summary>
    public IReadOnlyList<string> Validate(IReadOnlySet<int> allowedAccountIds, IReadOnlyDictionary<int, TaxCode>? taxCodes = null)
    {
        taxCodes ??= new Dictionary<int, TaxCode>();
        var errors = new List<string>();
        if (Date.Year < 1900 || Date.Year > 2100)
            errors.Add("Enter a valid journal date.");
        var description = Description?.Trim() ?? "";
        if (description.Length == 0)
            errors.Add("Enter a description for the journal.");
        else if (description.Length > 500)
            errors.Add("The description can be at most 500 characters.");

        var lines = UsedLines;
        if (lines.Count > MaxLines)
            errors.Add($"A journal can have at most {MaxLines} lines.");

        var lineErrors = false;
        for (var i = 0; i < Lines.Count; i++)
        {
            var l = Lines[i];
            if (l.IsBlank)
                continue;
            var n = i + 1;
            var debit = l.Debit ?? 0;
            var credit = l.Credit ?? 0;
            var before = errors.Count;

            if (l.AccountId <= 0)
                errors.Add($"Line {n}: choose an account.");
            else if (!allowedAccountIds.Contains(l.AccountId))
                errors.Add($"Line {n}: that account can't be posted to in this portfolio.");
            if (debit < 0 || credit < 0)
                errors.Add($"Line {n}: amounts can't be negative.");
            else if (debit != 0 && credit != 0)
                errors.Add($"Line {n}: enter either a debit or a credit, not both.");
            else if (debit == 0 && credit == 0)
                errors.Add($"Line {n}: enter a debit or a credit amount.");
            else if (decimal.Round(debit, 2) != debit || decimal.Round(credit, 2) != credit)
                errors.Add($"Line {n}: amounts are in dollars and cents (at most 2 decimal places).");
            else if (debit > 999_999_999_999m || credit > 999_999_999_999m)
                errors.Add($"Line {n}: that amount is too large.");
            if (l.TaxCodeId is { } taxCodeId)
            {
                if (!taxCodes.ContainsKey(taxCodeId))
                    errors.Add($"Line {n}: choose a tax code from the list.");
                else if (l.Tax is { } tax && (tax < 0 || tax > debit + credit))
                    errors.Add($"Line {n}: the tax can't be negative or more than the amount.");
                else if (l.Tax is { } t && decimal.Round(t, 2) != t)
                    errors.Add($"Line {n}: the tax is in dollars and cents (at most 2 decimal places).");
            }
            else if (l.Tax is not null and not 0)
                errors.Add($"Line {n}: choose a tax code for the tax.");
            if ((l.Description?.Trim().Length ?? 0) > 500)
                errors.Add($"Line {n}: the line description can be at most 500 characters.");

            lineErrors |= errors.Count > before;
        }

        if (lines.Count < 2)
            errors.Add("A journal needs at least two lines.");
        else if (!lineErrors && TotalDebit != TotalCredit)
            errors.Add($"The journal is out of balance: debits {TotalDebit:C} and credits {TotalCredit:C} differ by {Math.Abs(TotalDebit - TotalCredit):C}.");
        return errors;
    }

    /// <summary>The form starts with two empty rows, as the legacy page did; more are added in the browser.</summary>
    public static JournalInput New(DateTime today) => new()
    {
        Date = today,
        Lines = [new(), new()],
    };
}

/// <summary>Which journals the journal pages may change.</summary>
public static class JournalRules
{
    public const string ManualJournal = "ManualJournal";

    /// <summary>
    /// Only journals entered by hand can be edited or deleted. The legacy history page offered delete
    /// on every journal, but a journal posted for a trade, payout, cash transaction, bank import or
    /// allocation belongs to that record: deleting it alone would leave the ledger out of step.
    /// </summary>
    public static bool IsManual(string? sourceModule) =>
        string.Equals(sourceModule?.Trim(), ManualJournal, StringComparison.OrdinalIgnoreCase);

    /// <summary>A plain-English name for where a journal came from.</summary>
    public static string SourceLabel(string? sourceModule) => sourceModule?.Trim() switch
    {
        null or "" => "Unknown",
        "ManualJournal" => "Manual journal",
        "ManualAllocation" => "Bank allocation",
        "BankImport" => "Bank import",
        "BankTransaction" => "Cash transaction",
        "ShareTrade" => "Share trade",
        "Payout" => "Payout",
        "Revaluation" => "Market value revaluation",
        "Invoice" => "Customer invoice",
        "InvoicePayment" => "Invoice payment",
        var other => other,
    };
}
