namespace PortfolioManager.Core.NavexaSync;

public enum SyncStatus
{
    /// <summary>Will be added on the next "Add" or scheduled sync.</summary>
    Ready,
    /// <summary>Needs something from the user first (a franking credit, DRP units, a split ratio, a mapping).</summary>
    NeedsInput,
    /// <summary>An estimated dividend Navexa added by itself and hasn't confirmed yet; it comes across once confirmed.</summary>
    Waiting,
    /// <summary>Not brought across, with the reason.</summary>
    Skipped,
    /// <summary>Already added to this portfolio (or confirmed by the user as already here). Never added again.</summary>
    Imported,
    /// <summary>The database refused it; the message says why. Tried again on the next add.</summary>
    Failed,
}

public enum SyncKind { None, Trade, Payout, Drp, Cash, Split, CapitalReturn }

/// <summary>What the user still has to give before an item can be posted.</summary>
public enum InputNeed { None, FrankingCredit, Drp, SplitRatio, ConfirmNotDuplicate, Mapping }

/// <summary>Answers the user typed in on the review page for one item.</summary>
public sealed class SyncInput
{
    public decimal? FrankingCredit { get; set; }
    public int? DrpUnits { get; set; }
    public decimal? DrpPrice { get; set; }
    public int? SplitNewUnits { get; set; }
    public int? SplitOldUnits { get; set; }
    /// <summary>The user says a possible duplicate is a different trade or payout and should be added.</summary>
    public bool AddAnyway { get; set; }
    /// <summary>The user says a possible duplicate is already in the portfolio; it is then marked as imported.</summary>
    public bool AlreadyHere { get; set; }
}

public sealed record SyncTrade(string Exchange, string Code, string Name, string Type, DateTime Date, int Quantity, decimal Price,
    decimal Brokerage, string Currency, decimal ExchangeRate, string Comment, string? BankAccount);

/// <summary>A dividend or distribution with every amount in the portfolio currency.</summary>
public sealed record SyncPayout(string Exchange, string Code, DateTime Date, decimal Franked, decimal Unfranked, decimal FrankingCredit,
    decimal ForeignTax, decimal Net, string Comment);

public sealed record SyncDrp(string Exchange, string Code, DateTime Date, decimal Franked, decimal Unfranked, decimal FrankingCredit,
    int Units, decimal Price, string Comment);

/// <summary>A cash account entry. A null contra account posts against Owner's Capital, as a manual deposit or withdrawal does.</summary>
public sealed record SyncCash(string BankAccount, DateTime Date, decimal Deposit, decimal Withdrawal, string Currency, string Comment,
    int? ContraAccountId);

public sealed record SyncSplit(string Exchange, string Code, DateTime Date, int NewUnits, int OldUnits);

public sealed record SyncCapitalReturn(string Exchange, string Code, DateTime Date, decimal Total, string Comment);

/// <summary>One Navexa record and what the sync will do with it.</summary>
public sealed class PlannedItem
{
    public required string NavexaType { get; init; }
    public required string NavexaId { get; init; }
    public DateTime Date { get; init; }
    public DateTime? Modified { get; init; }
    public string Description { get; set; } = "";
    public SyncKind Kind { get; set; }
    public SyncStatus Status { get; set; } = SyncStatus.Ready;
    public InputNeed Need { get; set; }
    public string? Message { get; set; }
    public List<string> Warnings { get; } = [];
    /// <summary>Set on an imported item that was edited in Navexa afterwards; the change is not copied.</summary>
    public bool ChangedInNavexa { get; set; }

    /// <summary>Suggested answers for the review page (e.g. a franking credit worked out at 30%).</summary>
    public SyncInput Suggested { get; } = new();

    public SyncTrade? Trade { get; set; }
    public SyncPayout? Payout { get; set; }
    public SyncDrp? Drp { get; set; }
    public SyncCash? Cash { get; set; }
    public SyncSplit? Split { get; set; }
    public SyncCapitalReturn? CapitalReturn { get; set; }

    public string Key => $"{NavexaType}:{NavexaId}";

    /// <summary>Buys before sells on the same day, so a same-day round trip has units to sell.</summary>
    public int PostingOrder => Kind switch
    {
        SyncKind.Cash => 0,
        SyncKind.Split => 1,
        SyncKind.Trade when Trade?.Type == "Buy" => 2,
        SyncKind.Trade => 4,
        _ => 3,
    };

    internal void Skip(string reason)
    {
        Status = SyncStatus.Skipped;
        Message = reason;
    }

    internal void Ask(InputNeed need, string message)
    {
        Status = SyncStatus.NeedsInput;
        Need = need;
        Message = message;
    }
}

/// <summary>How a Navexa holding is recorded on this site.</summary>
public sealed record HoldingMapping(string Exchange, string Code);

/// <summary>A Navexa holding, cash account or cash transaction type the user still has to map before its records can come across.</summary>
public sealed record MappingNeed(string Key, string Label, string? SuggestedExchange = null, string? SuggestedCode = null);

public sealed class SetupNeeds
{
    public List<MappingNeed> Holdings { get; } = [];
    public List<MappingNeed> CashAccounts { get; } = [];
    public List<MappingNeed> CashTypes { get; } = [];
    public bool Any => Holdings.Count + CashAccounts.Count + CashTypes.Count > 0;
}

/// <param name="TradeBankAccount">Bank account that trades without a Navexa cash account settle in; null records trades without cash.</param>
/// <param name="CashAccounts">Navexa cash account (its holding id) to bank account name; a null name means don't bring it across.</param>
/// <param name="CashTypes">Navexa cash transaction type (deposit, fee, ...) to the ledger account its other side is posted to; null is Owner's Capital.</param>
public sealed record SyncSettings(
    string PortfolioCurrency,
    string? TradeBankAccount,
    DateTime? ImportFrom,
    IReadOnlyDictionary<string, HoldingMapping?> Holdings,
    IReadOnlyDictionary<string, string?> CashAccounts,
    IReadOnlyDictionary<string, int?> CashTypes);

/// <summary>What the sync already knows about one Navexa record from earlier runs.</summary>
public sealed record KnownItem(SyncStatus Status, DateTime? Modified, SyncInput? Input);

public sealed record SyncPlan(IReadOnlyList<PlannedItem> Items, SetupNeeds Setup)
{
    public int Count(SyncStatus status) => Items.Count(i => i.Status == status);
    public IEnumerable<PlannedItem> Ready => Items.Where(i => i.Status == SyncStatus.Ready);
}
