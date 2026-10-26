using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Core.CorporateActions;

/// <summary>A demerger, takeover or code change from uspGetParcelTransfers.</summary>
public sealed record ParcelTransferRecord(
    int Id,
    string Kind,
    DateTime Date,
    string FromExchangeCode,
    string FromCode,
    string ToExchangeCode,
    string ToCode,
    double NewPerOld,
    double CostShare,
    double ScripValue,
    int? BuyTradeId,
    int? SellTradeId,
    string? Comment)
{
    public string KindText => TransferKinds.Text(Kind);

    public bool CanDelete => !Kind.Equals(ParcelTransfer.CodeChange, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the CGT matching needs: a demerger, a takeover with rollover or an option exercise; null for the others, which are plain trades.</summary>
    public CapitalReturn? ToLot()
    {
        var t = new ParcelTransfer(Kind, ToExchangeCode, ToCode, (decimal)NewPerOld, (decimal)CostShare, (decimal)ScripValue, BuyTradeId, SellTradeId);
        return t.IsDemerger || t.IsRollover || t.IsExercise
            ? new CapitalReturn(FromExchangeCode, FromCode, DateOnly.FromDateTime(Date), 0m, Transfer: t)
            : null;
    }
}

public static class TransferKinds
{
    /// <summary>Shares that became worthless or were delisted: sold for nil, which realises the capital loss.</summary>
    public const string Worthless = "Worthless";

    public static readonly string[] All = [ParcelTransfer.Demerger, ParcelTransfer.Takeover, ParcelTransfer.CodeChange, ParcelTransfer.OptionExercise, Worthless];

    public static string Text(string kind) => kind switch
    {
        _ when kind.Equals(ParcelTransfer.Demerger, StringComparison.OrdinalIgnoreCase) => "Demerger",
        _ when kind.Equals(ParcelTransfer.Takeover, StringComparison.OrdinalIgnoreCase) => "Takeover (rollover)",
        _ when kind.Equals(ParcelTransfer.TakeoverTaxed, StringComparison.OrdinalIgnoreCase) => "Takeover",
        _ when kind.Equals(ParcelTransfer.CodeChange, StringComparison.OrdinalIgnoreCase) => "Code or name change",
        _ when kind.Equals(ParcelTransfer.OptionExercise, StringComparison.OrdinalIgnoreCase) => "Options exercised",
        _ when kind.Equals(Worthless, StringComparison.OrdinalIgnoreCase) => "Worthless, delisted or lapsed",
        _ => kind,
    };
}

/// <summary>What the record-a-corporate-action form posts. Which fields count depends on the kind.</summary>
public sealed class TransferForm
{
    public string? Kind { get; set; }
    public string? Market { get; set; }
    public string? Code { get; set; }
    public DateOnly? Date { get; set; }
    public string? NewMarket { get; set; }
    public string? NewCode { get; set; }
    public string? NewName { get; set; }
    public decimal? NewUnits { get; set; }
    /// <summary>Options exercised, or old shares taken up in a takeover: how many (all held when blank).</summary>
    public decimal? Units { get; set; }
    /// <summary>Options exercised: the price paid for each new share.</summary>
    public decimal? ExercisePrice { get; set; }
    /// <summary>Demerger: the ATO's (or the company's) percentage of the cost base that goes to the new company.</summary>
    public decimal? CostSharePercent { get; set; }
    /// <summary>Takeover: cash received for each old share.</summary>
    public decimal? CashPerShare { get; set; }
    /// <summary>Takeover: market value of each new share on the day.</summary>
    public decimal? NewSharePrice { get; set; }
    /// <summary>Takeover: choose scrip-for-scrip rollover for the new shares.</summary>
    public bool Rollover { get; set; }
    /// <summary>Demerger: the part paid as a dividend, per old share (blank or 0 when none).</summary>
    public decimal? DividendPerShare { get; set; }
    /// <summary>Demerger: how much of that dividend is franked, %.</summary>
    public decimal? DividendFrankedPercent { get; set; }
    public string? Comment { get; set; }
}

/// <summary>A checked corporate action. CostShare is a fraction; for a takeover with rollover null means work it out from the values.</summary>
public sealed record NewParcelTransfer(
    string Kind, string Market, string Code, DateOnly Date, string NewMarket, string NewCode, string? NewName,
    decimal NewUnits, decimal? CostShare, decimal CashPerShare, decimal NewSharePrice, bool Rollover, string? Comment,
    decimal? Units = null, decimal ExercisePrice = 0, decimal DividendPerShare = 0, decimal FrankedShare = 0)
{
    /// <summary>The company tax rate assumed for the franking credit on a demerger's dividend part.</summary>
    public const decimal CompanyTaxRate = 0.30m;

    public bool IsExercise => Kind == ParcelTransfer.OptionExercise;
    public bool IsDemerger => Kind == ParcelTransfer.Demerger;
    public bool IsTakeover => Kind == ParcelTransfer.Takeover;
    public bool IsCodeChange => Kind == ParcelTransfer.CodeChange;
    public bool IsWorthless => Kind == TransferKinds.Worthless;
}

/// <summary>One parcel held on the day: what it keeps and what goes with the new shares.</summary>
public sealed record TransferParcel(DateOnly Acquired, decimal Units, decimal CostBefore, decimal CostMoved, decimal NewUnits)
{
    public decimal CostKept => CostBefore - CostMoved;
}

/// <summary>
/// What a corporate action will do to the parcels held on its date. For a takeover, Consideration is the cash plus the
/// new shares' value; with rollover only the cash part is taxed now (CashCost is the cost base it uses), without it
/// the whole holding is sold at the consideration.
/// </summary>
public sealed record TransferPreview(
    decimal UnitsHeld, decimal CostBase, decimal CostShare, IReadOnlyList<TransferParcel> Parcels,
    decimal Cash = 0, decimal ScripValue = 0, decimal Dividend = 0, decimal DividendFranked = 0)
{
    public decimal CostMoved => Parcels.Sum(p => p.CostMoved);

    /// <summary>A demerger's dividend part that isn't franked.</summary>
    public decimal DividendUnfranked => Dividend - DividendFranked;

    /// <summary>The franking credit on the franked part, at the 30% company rate.</summary>
    public decimal DividendFrankingCredit =>
        Math.Round(DividendFranked * NewParcelTransfer.CompanyTaxRate / (1 - NewParcelTransfer.CompanyTaxRate), 2, MidpointRounding.AwayFromZero);

    public decimal Consideration => Cash + ScripValue;

    public decimal NewPerOld(decimal newUnits) => UnitsHeld == 0 ? 0 : newUnits / UnitsHeld;

    /// <summary>The gain or loss counted now: on the cash part with rollover, else on the whole consideration.</summary>
    public decimal GainNow(bool rollover) => rollover ? Cash - (CostBase - CostMoved) : Consideration - CostBase;
}

public static class ParcelTransferEntry
{
    public static Validated<NewParcelTransfer> Validate(TransferForm f, DateOnly today)
    {
        var errors = new List<FieldError>();
        var kind = TransferKinds.All.FirstOrDefault(k => k.Equals(f.Kind?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (kind is null)
            errors.Add(new(nameof(f.Kind), "Choose what happened."));

        var market = Code(errors, nameof(f.Market), "stock exchange", f.Market);
        var code = Code(errors, nameof(f.Code), "stock code", f.Code);

        if (f.Date is not { } date)
            errors.Add(new(nameof(f.Date), "Enter the date it happened."));
        else if (date > today)
            errors.Add(new(nameof(f.Date), "The date can't be in the future."));
        else if (date < FormRules.EarliestDate)
            errors.Add(new(nameof(f.Date), "Check the date."));

        var comment = FormRules.Clean(f.Comment);
        FormRules.MaxLength(errors, nameof(f.Comment), "Comment", comment, 500);
        var name = FormRules.Clean(f.NewName);
        FormRules.MaxLength(errors, nameof(f.NewName), "New name", name, 500);

        string? newMarket = null, newCode = null;
        decimal newUnits = 0;
        decimal? share = null;
        decimal dividend = 0, franked = 0;
        decimal cash = 0, price = 0;
        var newShares = kind is ParcelTransfer.Demerger or ParcelTransfer.CodeChange or ParcelTransfer.OptionExercise
            || (kind == ParcelTransfer.Takeover && (f.NewUnits is > 0 || !string.IsNullOrWhiteSpace(f.NewCode)));
        if (newShares)
        {
            newMarket = Code(errors, nameof(f.NewMarket), "new company's stock exchange", f.NewMarket ?? f.Market);
            newCode = Code(errors, nameof(f.NewCode), "new code", f.NewCode);
            if (newMarket == market && newCode == code && newCode is not null)
                errors.Add(new(nameof(f.NewCode), "The new code is the same as the old one."));
        }

        decimal? units = null;
        decimal exercise = 0;
        switch (kind)
        {
            case ParcelTransfer.OptionExercise:
                newUnits = Units(errors, f.NewUnits, "Enter how many shares you received.");
                if (f.ExercisePrice is not { } ep || ep < 0 || ep > FormRules.MaxMoney)
                    errors.Add(new(nameof(f.ExercisePrice), "Enter the exercise price paid for each share."));
                else
                    exercise = ep;
                if (f.Units is { } u)
                {
                    if (u <= 0)
                        errors.Add(new(nameof(f.Units), "Enter how many options were exercised, or leave it blank for all of them."));
                    else
                        units = u;
                }
                share = 1;
                break;

            case ParcelTransfer.Demerger:
                newUnits = Units(errors, f.NewUnits, "Enter how many shares in the new company you received.");
                if (f.CostSharePercent is not { } pct || pct <= 0 || pct >= 100)
                    errors.Add(new(nameof(f.CostSharePercent), "Enter the percentage of the cost base that goes to the new company, between 0 and 100 (the company's or the ATO's figure)."));
                else
                    share = pct / 100;
                dividend = f.DividendPerShare ?? 0;
                if (dividend < 0 || dividend > FormRules.MaxMoney)
                    errors.Add(new(nameof(f.DividendPerShare), "Check the dividend per share."));
                if (dividend > 0)
                {
                    var fp = f.DividendFrankedPercent ?? 0;
                    if (fp is < 0 or > 100)
                        errors.Add(new(nameof(f.DividendFrankedPercent), "The franked part must be between 0% and 100%."));
                    else
                        franked = fp / 100;
                }
                break;

            case ParcelTransfer.Takeover:
                if (f.Units is { } taken)
                {
                    if (taken <= 0)
                        errors.Add(new(nameof(f.Units), "Enter how many shares were taken up, or leave it blank for all of them."));
                    else if (FormRules.UnitsProblem(taken) is { } problem)
                        errors.Add(new(nameof(f.Units), problem));
                    else
                        units = taken;
                }
                cash = f.CashPerShare ?? 0;
                if (cash < 0 || cash > FormRules.MaxMoney)
                    errors.Add(new(nameof(f.CashPerShare), "Check the cash per share."));
                if (newShares)
                {
                    newUnits = Units(errors, f.NewUnits, "Enter how many new shares you received.");
                    if (f.NewSharePrice is not { } p || p <= 0 || p > FormRules.MaxMoney)
                        errors.Add(new(nameof(f.NewSharePrice), "Enter the market value of each new share on the day."));
                    else
                        price = p;
                }
                else if (cash == 0)
                    errors.Add(new(nameof(f.CashPerShare), "Enter the cash per share, the new shares, or both."));
                if (f.Rollover && f.CostSharePercent is { } c)
                {
                    if (c <= 0 || c > 100)
                        errors.Add(new(nameof(f.CostSharePercent), "The cost base share for the new shares must be more than 0% and no more than 100%."));
                    else
                        share = c / 100;
                }
                break;
        }

        if (errors.Count > 0)
            return new(null, errors);
        return new(new NewParcelTransfer(kind!, market!, code!, f.Date!.Value, newMarket ?? "", newCode ?? "", name, newUnits, share,
            cash, price, kind == ParcelTransfer.Takeover && f.Rollover && newUnits > 0, comment, units, exercise, dividend, franked), errors);
    }

    /// <summary>
    /// The parcels of the holding open at the end of the date (CGT figures, after returns of capital and statements),
    /// and each one's part. The new units are spread by units held, the last parcel taking what rounding leaves.
    /// </summary>
    public static TransferPreview Preview(IEnumerable<OpenParcel> open, NewParcelTransfer t)
    {
        var parcels = open
            .Where(p => p.ExchangeCode.Trim().Equals(t.Market, StringComparison.OrdinalIgnoreCase)
                     && p.StockCode.Trim().Equals(t.Code, StringComparison.OrdinalIgnoreCase) && p.Units > 0)
            .OrderBy(p => p.Acquired)
            .ToList();
        // Options exercised, or a takeover where only some shares were taken up: the oldest parcels go first, as the sale will
        if ((t.IsExercise || t.IsTakeover) && t.Units is decimal take)
            parcels = FirstUnits(parcels, take);
        var units = parcels.Sum(p => p.Units);
        var cost = parcels.Sum(p => p.TotalCost);
        var cash = t.IsTakeover ? Cents(t.CashPerShare * units) : 0;
        var scrip = t.IsTakeover ? Cents(t.NewSharePrice * t.NewUnits) : 0;
        var share = t switch
        {
            { IsDemerger: true } => t.CostShare ?? 0,
            { Rollover: true } => t.CostShare ?? (cash + scrip == 0 ? 0 : Math.Round(scrip / (cash + scrip), 6)),
            { IsExercise: true } => 1,
            _ => 0,
        };

        var rows = new List<TransferParcel>(parcels.Count);
        var unitsLeft = (decimal)t.NewUnits;
        for (var i = 0; i < parcels.Count; i++)
        {
            var p = parcels[i];
            var newUnits = i == parcels.Count - 1 ? unitsLeft : Math.Round(t.NewUnits * p.Units / units, 4, MidpointRounding.AwayFromZero);
            unitsLeft -= newUnits;
            rows.Add(new TransferParcel(p.Acquired, p.Units, p.TotalCost, Cents(p.TotalCost * share), newUnits));
        }
        var dividend = t.IsDemerger ? Cents(t.DividendPerShare * units) : 0;
        return new TransferPreview(units, cost, share, rows, cash, scrip, dividend, Cents(dividend * t.FrankedShare));
    }

    /// <summary>The oldest parcels up to a number of units (first in, first out), the last one cut down to fit.</summary>
    private static List<OpenParcel> FirstUnits(List<OpenParcel> parcels, decimal units)
    {
        var taken = new List<OpenParcel>();
        foreach (var p in parcels)
        {
            if (units <= 0)
                break;
            var u = Math.Min(p.Units, units);
            taken.Add(p with { Units = u });
            units -= u;
        }
        return taken;
    }

    private static string? Code(List<FieldError> errors, string field, string label, string? value)
    {
        var v = FormRules.Clean(value)?.ToUpperInvariant();
        if (v is null)
            errors.Add(new(field, $"Enter the {label}."));
        FormRules.MaxLength(errors, field, label, v, 50);
        return v;
    }

    private static decimal Units(List<FieldError> errors, decimal? value, string message)
    {
        if (value is not > 0)
        {
            errors.Add(new(nameof(TransferForm.NewUnits), message));
            return 0;
        }
        if (FormRules.UnitsProblem(value.Value) is { } problem)
        {
            errors.Add(new(nameof(TransferForm.NewUnits), problem));
            return 0;
        }
        return value.Value;
    }

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Records demergers, takeovers, code changes and worthless shares through the procs in the complex corporate actions migration.</summary>
public sealed class ParcelTransferService(IDbConnectionFactory db, CgtDiscountService cgt)
{
    public async Task<TransferPreview> PreviewAsync(int portfolioId, NewParcelTransfer t) =>
        ParcelTransferEntry.Preview((await cgt.GetOpenParcelsAsync(portfolioId, t.Date)).Open, t);

    public async Task<ProcResult> ApplyAsync(int portfolioId, NewParcelTransfer t, TransferPreview p)
    {
        await using var con = db.Create();
        if (t.IsWorthless)
            return await SellForNilAsync(con, portfolioId, t, p);

        var (proc, args) = t switch
        {
            { IsDemerger: true } => ("uspApplyDemerger", (object)new
            {
                iUserID = portfolioId, strMarket = t.Market, strCode = t.Code, strNewMarket = t.NewMarket, strNewCode = t.NewCode,
                strNewName = t.NewName, dtmDate = Day(t.Date), iNewUnits = (double)t.NewUnits, dblNewPerOld = (double)p.NewPerOld(t.NewUnits),
                dblCostShare = (double)p.CostShare, dblCostMoved = (double)p.CostMoved, strComment = t.Comment,
            }),
            { IsExercise: true } => ("uspExerciseOptions", new
            {
                iUserID = portfolioId, strMarket = t.Market, strCode = t.Code, strNewMarket = t.NewMarket, strNewCode = t.NewCode,
                strNewName = t.NewName, dtmDate = Day(t.Date), iOptions = (double)p.UnitsHeld, iShares = (double)t.NewUnits,
                dblExercisePrice = (double)t.ExercisePrice, dblOptionCost = (double)p.CostMoved, strComment = t.Comment,
            }),
            { IsTakeover: true } => ("uspApplyTakeover", (object)new
            {
                iUserID = portfolioId, strMarket = t.Market, strCode = t.Code, strNewMarket = t.NewMarket, strNewCode = t.NewCode,
                strNewName = t.NewName, dtmDate = Day(t.Date), iUnitsSold = (double)p.UnitsHeld, dblConsideration = (double)p.Consideration,
                iNewUnits = (double)t.NewUnits, dblScripValue = (double)p.ScripValue, bRollover = t.Rollover, dblCostShare = (double)p.CostShare,
                strComment = t.Comment,
            }),
            _ => ("uspChangeStockCode", new
            {
                iUserID = portfolioId, strMarket = t.Market, strCode = t.Code, strNewMarket = t.NewMarket, strNewCode = t.NewCode,
                strNewName = t.NewName, dtmDate = Day(t.Date), strComment = t.Comment,
            }),
        };
        if (t.IsDemerger && p.Dividend > 0)
            return await DemergerWithDividendAsync(con, portfolioId, t, p, proc, args);
        var row = (IDictionary<string, object?>)await con.QuerySingleAsync(proc, args, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row["Result"] as string, "Success", row["Id"] is { } id and not DBNull ? Convert.ToInt32(id) : null);
    }

    /// <summary>
    /// A demerger paid partly as a dividend: the demerger through its proc, then the dividend part as a payout of the old
    /// company (uspAddPayout), in one transaction. No cash arrives, since the dividend was paid in the new shares, so the
    /// payout's journal goes to the new company's cost base account instead of the bank.
    /// </summary>
    private static async Task<ProcResult> DemergerWithDividendAsync(System.Data.Common.DbConnection con, int portfolioId, NewParcelTransfer t,
        TransferPreview p, string proc, object args)
    {
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        var row = (IDictionary<string, object?>)await con.QuerySingleAsync(proc, args, tx, commandType: CommandType.StoredProcedure);
        var result = ProcResult.From(row["Result"] as string, "Success", row["Id"] is { } id and not DBNull ? Convert.ToInt32(id) : null);
        if (!result.Ok)
            return result;
        var account = await con.ExecuteScalarAsync<int?>(
            "SELECT TOP 1 iAccountID FROM tblChartOfAccounts WHERE iUserID = @portfolioId AND strAccountName = @name",
            new { portfolioId, name = $"{t.NewCode}.{t.NewMarket} - Cost Base" }, tx);
        await con.ExecuteAsync("uspAddPayout", DemergerDividendParameters(portfolioId, t, p, account), tx, commandType: CommandType.StoredProcedure);
        await tx.CommitAsync();
        return result;
    }

    /// <summary>uspAddPayout's parameters for a demerger's dividend part.</summary>
    public static object DemergerDividendParameters(int portfolioId, NewParcelTransfer t, TransferPreview p, int? costBaseAccount) => new
    {
        iUserId = portfolioId,
        strStockExchangeCode = t.Market,
        strStockCode = t.Code,
        dtmTransactionDate = Day(t.Date),
        dblTotal = (double)p.Dividend,
        dblNetAmount = (double)p.Dividend,
        dblFrankedAmount = (double)p.DividendFranked,
        dblUnFrankedAmount = (double)p.DividendUnfranked,
        dblInterestAmount = 0d,
        dblTFNWithholdingAmount = 0d,
        dblForeignIncomeTaxAmount = 0d,
        dblOtherAmount = 0d,
        strComment = $"Demerger of {t.NewCode}: dividend part, paid in shares",
        strPayoutConfirmed = "Yes",
        dblFrankingCredit = (double)p.DividendFrankingCredit,
        dblTrustIncome = 0d,
        iTargetAccountID = costBaseAccount,
    };

    /// <summary>Removes a demerger or takeover and the trades it added. Null when it isn't one of this portfolio's.</summary>
    public async Task<ProcResult?> DeleteAsync(int portfolioId, int transferId)
    {
        await using var con = db.Create();
        var owned = await con.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tblParcelTransfers WHERE iParcelTransferID = @transferId AND iUserID = @portfolioId", new { portfolioId, transferId });
        if (owned == 0)
            return null;
        var result = await con.ExecuteScalarAsync<string>("uspDeleteParcelTransfer",
            new { iUserID = portfolioId, iParcelTransferID = transferId }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(result, "Success");
    }

    /// <summary>A sale of every unit for nil through uspAddShareTrade (in a transaction, as TradeService does): the capital loss is the cost base.</summary>
    private static async Task<ProcResult> SellForNilAsync(System.Data.Common.DbConnection con, int portfolioId, NewParcelTransfer t, TransferPreview p)
    {
        if (p.UnitsHeld <= 0)
            return ProcResult.Failed("No units were held on that date.");
        var currency = await con.ExecuteScalarAsync<string?>("SELECT TOP 1 strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId", new { portfolioId }) ?? "AUD";
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();
        await con.ExecuteAsync("uspAddShareTrade", new
        {
            iUserId = portfolioId, strMarket = t.Market, strCode = t.Code, strName = (string?)null, strType = "Sell",
            dtmDateOfTransaction = Day(t.Date), iQuantity = (double)p.UnitsHeld, dblPricePerUnit = 0d, dblBrokerage = 0d,
            strCurrency = currency, dblExchangeRate = 1d, strComment = "Worthless or delisted" + (t.Comment is null ? "" : ": " + t.Comment),
            iBankStatementId = (int?)null,
        }, tx, commandType: CommandType.StoredProcedure);
        await tx.CommitAsync();
        return ProcResult.Success();
    }

    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}
