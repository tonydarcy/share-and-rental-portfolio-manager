namespace PortfolioManager.Core.Payouts;

/// <summary>
/// A dividend paid as new units under a dividend reinvestment plan (DRP), as entered on the Add DRP page.
/// The dividend is recorded as a payout and the units as a buy at the DRP price, together.
/// </summary>
public sealed class DrpInput
{
    public string StockExchangeCode { get; set; } = "ASX";
    public string StockCode { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal Franked { get; set; }
    public decimal Unfranked { get; set; }
    public decimal FrankingCredit { get; set; }
    public decimal TaxWithheld { get; set; }
    public int Units { get; set; }
    public decimal Price { get; set; }
    public string? Comment { get; set; }

    /// <summary>The dividend after TFN withholding: what the plan had to reinvest.</summary>
    public decimal Cash => Franked + Unfranked - TaxWithheld;

    /// <summary>What the new units cost at the DRP price.</summary>
    public decimal Reinvested => Math.Round(Units * Price, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Left over (or, if negative, taken from a balance carried forward from the last DRP). Registries only
    /// issue whole units, so this is less than one unit's price; in the ledger it stays in the bank account.
    /// </summary>
    public decimal Residual => Cash - Reinvested;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(StockCode))
            errors.Add("Enter the stock code.");
        else if (StockCode.Trim().Length > 50)
            errors.Add("The stock code can be at most 50 characters.");
        if (string.IsNullOrWhiteSpace(StockExchangeCode))
            errors.Add("Enter the exchange (e.g. ASX).");
        else if (StockExchangeCode.Trim().Length > 50)
            errors.Add("The exchange can be at most 50 characters.");
        if (Date.Year < 1900 || Date.Year > 2078)
            errors.Add("Enter a valid payment date.");
        if ((Comment?.Length ?? 0) > 500)
            errors.Add("The comment can be at most 500 characters.");

        if (new[] { Franked, Unfranked, FrankingCredit, TaxWithheld, Price }.Any(v => v < 0))
        {
            errors.Add("Amounts can't be negative.");
            return errors;
        }
        if (Franked + Unfranked <= 0)
            errors.Add("Enter the dividend: the franked and unfranked amounts.");
        else if (Cash <= 0)
            errors.Add("The tax withheld can't be the whole dividend.");
        if (Units <= 0)
            errors.Add("Enter the number of new units allotted.");
        if (Price <= 0)
            errors.Add("Enter the DRP price per unit.");

        if (errors.Count == 0 && Math.Abs(Residual) >= Price)
            errors.Add($"{Units:N0} units at {Price:C4} come to {Reinvested:C}, but the dividend was {Cash:C}. "
                + "The difference should be less than one unit's price; check the units, the price and the amounts.");
        return errors;
    }
}
