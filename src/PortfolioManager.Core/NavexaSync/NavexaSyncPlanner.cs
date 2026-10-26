using System.Globalization;
using PortfolioManager.Core.Import;
using PortfolioManager.Core.Payouts;

namespace PortfolioManager.Core.NavexaSync;

/// <summary>A tblBankStatement entry already in the portfolio, for spotting cash entered by hand before the sync.</summary>
public sealed record ExistingCash(string Account, DateTime Date, decimal Deposit, decimal Withdrawal);

/// <summary>What the portfolio already holds, so a first sync doesn't double up records added by hand or from a file.</summary>
public sealed record ExistingRecords(IReadOnlyList<ExistingTrade> Trades, IReadOnlyList<ExistingPayout> Payouts, IReadOnlyList<ExistingCash> Cash)
{
    public static readonly ExistingRecords None = new([], [], []);
}

/// <summary>
/// Turns Navexa's transactions into the records this site posts through its procs. Pure: no database or network.
/// Anything the double-entry journals need that Navexa doesn't give (a franking credit, DRP units, a split
/// ratio, which bank or ledger account cash belongs to) is asked for rather than guessed.
/// </summary>
public static class NavexaSyncPlanner
{
    private const decimal Tolerance = 0.01m;
    private const int DrpPairingDays = 10;

    public static SyncPlan Plan(
        IEnumerable<NavexaApiTransaction> transactions,
        SyncSettings settings,
        IReadOnlyDictionary<string, KnownItem> known,
        ExistingRecords existing)
    {
        var list = transactions.Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => Key(t)).Select(g => g.First())
            .OrderBy(t => t.Date).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        var setup = new SetupNeeds();
        var drpTrades = PairDrpTrades(list);
        var items = new List<PlannedItem>();

        foreach (var tx in list)
        {
            known.TryGetValue(Key(tx), out var k);
            var item = new PlannedItem
            {
                NavexaType = tx.Type,
                NavexaId = tx.Id,
                Date = (tx.Dividend?.PaymentDate ?? tx.Date).Date,
                Modified = tx.LastModifiedDate,
                Description = Describe(tx),
            };
            items.Add(item);

            if (k?.Status == SyncStatus.Imported)
            {
                item.Status = SyncStatus.Imported;
                item.Message = "Added earlier.";
                item.ChangedInNavexa = tx.LastModifiedDate is { } m && k.Modified is { } was && m > was.AddSeconds(1);
                if (item.ChangedInNavexa)
                    item.Message = "Changed in Navexa after it was added here. The change isn't copied: update it here by hand if it matters.";
                continue;
            }
            if (settings.ImportFrom is { } from && item.Date < from.Date)
            {
                item.Skip($"Before the start date ({from:d}).");
                continue;
            }

            var input = k?.Input ?? new SyncInput();
            var ctx = new Context(settings, setup, input);
            switch (tx.Type)
            {
                case "trade":
                    PlanTrade(tx, item, ctx, drpTrades);
                    break;
                case "dividend":
                    PlanDividend(tx, item, ctx, drpTrades);
                    break;
                case "cashTransaction":
                    PlanCash(tx, item, ctx);
                    break;
                case "cryptoTrade" or "cryptoIncome":
                    item.Skip("Crypto isn't supported on this site.");
                    break;
                case "merger":
                    item.Skip("Mergers, takeovers and demergers aren't brought across. Record the sale and new shares by hand.");
                    break;
                default:
                    item.Skip($"Navexa \"{tx.Type}\" records aren't brought across.");
                    break;
            }

            if (item.Status == SyncStatus.Ready)
                CheckExisting(item, existing, input);
        }

        return new SyncPlan(items, setup);
    }

    public static string Key(NavexaApiTransaction tx) => $"{tx.Type}:{tx.Id}";

    private sealed record Context(SyncSettings Settings, SetupNeeds Setup, SyncInput Input);

    // ---------- Trades ----------

    private static void PlanTrade(NavexaApiTransaction tx, PlannedItem item, Context ctx, DrpPairs drp)
    {
        var leg = HoldingLeg(tx);
        if (leg?.Holding is not { } holding)
        {
            item.Skip("This trade has no holding.");
            return;
        }

        if (tx.TradeTypeId == 8)
        {
            item.Skip(drp.PairedTrades.Contains(tx.Id)
                ? "Added with its dividend as a dividend reinvestment."
                : "Dividend reinvestment units with no matching reinvested dividend. Add them as a Buy if you want them.");
            return;
        }

        string? type = tx.TradeTypeId switch
        {
            1 => "Buy",
            2 => "Sell",
            12 => "Buy",
            _ => null,
        };
        var isCorporateAction = tx.TradeTypeId is 3 or 5 or 11;
        if (type is null && !isCorporateAction)
        {
            item.Skip(UnsupportedTradeMessage(tx));
            return;
        }

        if (ResolveHolding(holding, item, ctx) is not { } where)
            return;

        var currency = Upper(leg.Currency ?? tx.Currency) ?? NavexaExchanges.Map(where.Exchange).Currency ?? ctx.Settings.PortfolioCurrency;
        var portfolioPerForeign = PortfolioPerForeign(tx, currency, ctx.Settings.PortfolioCurrency);
        var comment = Comment(tx.Notes);

        if (tx.TradeTypeId == 11)
        {
            // Navexa's return of capital carries the total cash received
            var total = Math.Abs(tx.Value ?? leg.Price ?? 0m);
            if (portfolioPerForeign <= 0)
            {
                item.Skip($"No exchange rate from {currency} to {ctx.Settings.PortfolioCurrency}. Record it under Corporate actions.");
                return;
            }
            total = Math.Round(total * portfolioPerForeign, 2);
            if (total <= 0)
            {
                item.Skip("Return of capital with no amount.");
                return;
            }
            item.Kind = SyncKind.CapitalReturn;
            item.CapitalReturn = new SyncCapitalReturn(where.Exchange, where.Code, item.Date, total, comment);
            return;
        }

        if (tx.TradeTypeId is 3 or 5)
        {
            var label = tx.TradeTypeId == 3 ? "split" : "consolidation";
            if (ctx.Input is { SplitNewUnits: > 0 and var n, SplitOldUnits: > 0 and var o } && (tx.TradeTypeId == 3 ? n > o : n < o))
            {
                item.Kind = SyncKind.Split;
                item.Split = new SyncSplit(where.Exchange, where.Code, item.Date, n, o);
            }
            else
            {
                item.Kind = SyncKind.Split;
                item.Ask(InputNeed.SplitRatio, $"Navexa only gives the units {(tx.TradeTypeId == 3 ? "added" : "removed")} " +
                    $"({Math.Abs(leg.Quantity ?? 0):#,##0.########}). Enter the {label} ratio, e.g. 2 new for 1 old" +
                    (tx.TradeTypeId == 5 ? " or 1 new for 10 old." : "."));
            }
            return;
        }

        if (tx.TradeTypeId == 12)
            item.Warnings.Add("Navexa opening balance: added as a Buy on this date at this price.");

        var qty = Math.Abs(leg.Quantity ?? 0);
        if (qty == 0)
        {
            item.Skip("The trade has no units.");
            return;
        }
        if (Validation.FormRules.UnitsProblem(qty) is { } problem)
        {
            item.Skip($"{problem} ({qty:0.##########})");
            return;
        }
        var price = leg.Price ?? 0;
        if (price < 0)
        {
            item.Skip("The price is negative.");
            return;
        }
        if (portfolioPerForeign <= 0)
        {
            item.Skip($"No exchange rate from {currency} to {ctx.Settings.PortfolioCurrency}. Add the trade by hand.");
            return;
        }

        var brokerage = Math.Abs(leg.Brokerage ?? (tx.Fees ?? []).Sum(f => f.Amount ?? 0));
        var brokerageCurrency = Upper(leg.BrokerageCurrency);
        if (brokerage != 0 && brokerageCurrency is not null && brokerageCurrency != currency)
        {
            if (brokerageCurrency == Upper(ctx.Settings.PortfolioCurrency))
            {
                brokerage = Math.Round(brokerage / portfolioPerForeign, 4);
                item.Warnings.Add($"Brokerage was paid in {brokerageCurrency}; converted to {currency} {brokerage:0.00}.");
            }
            else
            {
                item.Skip($"Brokerage is in {brokerageCurrency}, which can't be converted to {currency}. Add the trade by hand.");
                return;
            }
        }

        // This site stores the rate the other way round: trade currency per 1 unit of portfolio currency
        var siteRate = currency == Upper(ctx.Settings.PortfolioCurrency) ? 1m : Math.Round(1m / portfolioPerForeign, 6);

        string? bank;
        var cashLeg = tx.Legs.FirstOrDefault(l => l.Holding is { } h && IsCash(h));
        if (cashLeg?.Holding is { } cashHolding)
        {
            if (!TryCashAccount(cashHolding, item, ctx, out bank))
                return;
        }
        else
            bank = ctx.Settings.TradeBankAccount;

        item.Kind = SyncKind.Trade;
        item.Trade = new SyncTrade(where.Exchange, where.Code, Truncate(holding.Name ?? holding.Symbol, 500), type!, item.Date,
            qty, price, brokerage, currency, siteRate, comment, bank);
    }

    private static string UnsupportedTradeMessage(NavexaApiTransaction tx) => tx.TradeTypeId switch
    {
        4 => "Bonus issues aren't brought across yet. Add the bonus units by hand.",
        6 => "Cancellations aren't brought across. Adjust the holding by hand.",
        13 or 14 => "Merger legs aren't brought across. Record the sale and the new shares by hand.",
        16 or 17 => "Cost base adjustments aren't brought across. Adjust the cost base by hand.",
        100 or 101 or 200 or 201 => "Transfers carry no price, so they aren't brought across. Enter a Buy or Sell if it changes the holding.",
        _ => $"Navexa \"{tx.TradeType ?? tx.TradeTypeId?.ToString(CultureInfo.InvariantCulture)}\" trades aren't brought across.",
    };

    // ---------- Dividends ----------

    private static void PlanDividend(NavexaApiTransaction tx, PlannedItem item, Context ctx, DrpPairs drp)
    {
        if (tx.AutoAdded && !tx.IsConfirmed)
        {
            item.Status = SyncStatus.Waiting;
            item.Message = "Navexa added this dividend by itself and it isn't confirmed yet. Confirm it in Navexa and it comes across on the next sync.";
            return;
        }
        var d = tx.Dividend;
        var leg = HoldingLeg(tx);
        if (d is null || leg?.Holding is not { } holding)
        {
            item.Skip("This income record has no dividend details.");
            return;
        }
        if (ResolveHolding(holding, item, ctx) is not { } where)
            return;

        var currency = Upper(tx.Currency ?? leg.Currency) ?? ctx.Settings.PortfolioCurrency;
        var rate = PortfolioPerForeign(tx, currency, ctx.Settings.PortfolioCurrency);
        if (rate <= 0)
        {
            item.Skip($"No exchange rate from {currency} to {ctx.Settings.PortfolioCurrency}. Add the payout by hand.");
            return;
        }
        decimal Aud(decimal amount) => Math.Round(amount * rate, 2);

        var franked = d.Franked ?? 0;
        var unfranked = d.Unfranked ?? 0;
        if (franked == 0 && unfranked == 0)
            unfranked = d.GrossAmount ?? 0;
        var foreign = Math.Abs(d.ForeignTaxWithheld ?? 0);
        var net = d.NetAmount ?? franked + unfranked - foreign;

        franked = Aud(franked);
        unfranked = Aud(unfranked);
        foreign = Aud(foreign);
        net = Aud(net);
        var credit = Aud(d.FrankingCredits ?? 0);

        if (franked < 0 || unfranked < 0 || net < 0 || credit < 0)
        {
            item.Skip("Negative amounts (reversals) aren't brought across. Adjust the payout by hand.");
            return;
        }
        if (franked + unfranked == 0 && net == 0)
        {
            item.Skip("The dividend has no amount.");
            return;
        }
        if (Math.Abs(franked + unfranked - foreign - net) > Tolerance)
            item.Warnings.Add($"Navexa's net amount ({net:0.00}) isn't the dividend less tax withheld ({franked + unfranked - foreign:0.00}). The net amount is used as the cash received.");

        var comment = Comment(tx.Notes);
        var needs = new List<string>();
        if (franked > 0 && credit == 0)
        {
            item.Suggested.FrankingCredit = Math.Round(franked * 30m / 70m, 2);
            if (ctx.Input.FrankingCredit is { } given && given >= 0)
                credit = given;
            else
                needs.Add($"Navexa has a franked amount of {franked:0.00} but no franking credit. Enter the franking credit " +
                    $"({item.Suggested.FrankingCredit:0.00} at the 30% company tax rate, less if the company pays 25%).");
        }

        if (d.IsReinvested)
        {
            item.Kind = SyncKind.Drp;
            if (foreign > 0)
            {
                item.Skip("A reinvested dividend with foreign tax withheld isn't brought across. Record it on the DRP page by hand.");
                return;
            }
            if (drp.ByDividend.TryGetValue(tx.Id, out var trade) && HoldingLeg(trade) is { } unitsLeg)
            {
                var q = Math.Abs(unitsLeg.Quantity ?? 0);
                if (q > 0 && Validation.FormRules.UnitsProblem(q) is null)
                    item.Suggested.DrpUnits = q;
                item.Suggested.DrpPrice = Math.Round((unitsLeg.Price ?? 0) * rate, 4);
            }
            var units = ctx.Input.DrpUnits ?? item.Suggested.DrpUnits;
            var price = ctx.Input.DrpPrice ?? item.Suggested.DrpPrice;
            if (units is not > 0 || price is not > 0)
                needs.Add("Navexa says this dividend was reinvested. Enter the units allotted and the DRP price per unit.");
            else if (needs.Count == 0)
            {
                var check = new DrpInput
                {
                    StockExchangeCode = where.Exchange, StockCode = where.Code, Date = item.Date,
                    Franked = franked, Unfranked = unfranked, FrankingCredit = credit, Units = units.Value, Price = price.Value,
                };
                var errors = check.Validate();
                if (errors.Count > 0)
                    needs.Add(string.Join(" ", errors));
                else
                    item.Drp = new SyncDrp(where.Exchange, where.Code, item.Date, franked, unfranked, credit, units.Value, price.Value, comment);
            }
            if (needs.Count > 0)
                item.Ask(InputNeed.Drp, string.Join(" ", needs));
            return;
        }

        item.Kind = SyncKind.Payout;
        if (needs.Count > 0)
        {
            item.Ask(InputNeed.FrankingCredit, string.Join(" ", needs));
            return;
        }
        item.Payout = new SyncPayout(where.Exchange, where.Code, item.Date, franked, unfranked, credit, foreign, net, comment);
    }

    private sealed record DrpPairs(Dictionary<string, NavexaApiTransaction> ByDividend, HashSet<string> PairedTrades);

    /// <summary>Navexa makes a Dividend Reinvestment trade (type 8) for each reinvested dividend; it carries the units and price.</summary>
    private static DrpPairs PairDrpTrades(IReadOnlyList<NavexaApiTransaction> list)
    {
        var byDividend = new Dictionary<string, NavexaApiTransaction>();
        var paired = new HashSet<string>();
        var trades = list.Where(t => t.Type == "trade" && t.TradeTypeId == 8 && HoldingLeg(t)?.Holding is not null).ToList();
        foreach (var div in list.Where(t => t.Type == "dividend" && t.Dividend?.IsReinvested == true))
        {
            var holdingId = HoldingLeg(div)?.Holding?.Id;
            var date = (div.Dividend!.PaymentDate ?? div.Date).Date;
            var match = trades
                .Where(t => !paired.Contains(t.Id) && HoldingLeg(t)!.Holding!.Id == holdingId && Math.Abs((t.Date.Date - date).TotalDays) <= DrpPairingDays)
                .OrderBy(t => Math.Abs((t.Date.Date - date).TotalDays))
                .FirstOrDefault();
            if (match is null)
                continue;
            paired.Add(match.Id);
            byDividend[div.Id] = match;
        }
        return new DrpPairs(byDividend, paired);
    }

    // ---------- Cash ----------

    private static readonly HashSet<string> OwnerTypes = ["deposit", "withdrawal"];

    private static void PlanCash(NavexaApiTransaction tx, PlannedItem item, Context ctx)
    {
        if (tx.LinkedTo is not null)
        {
            item.Skip("This is the cash side of a Navexa trade or dividend, which brings its own cash and journal across.");
            return;
        }
        var inLeg = (tx.Incoming ?? []).FirstOrDefault(l => l.Holding is { } h && IsCash(h));
        var outLeg = (tx.Outgoing ?? []).FirstOrDefault(l => l.Holding is { } h && IsCash(h));
        var leg = inLeg ?? outLeg;
        if (leg?.Holding is not { } account)
        {
            item.Skip("This cash transaction has no cash account.");
            return;
        }
        if (!TryCashAccount(account, item, ctx, out var bank))
            return;
        if (bank is null)
        {
            item.Skip("You chose not to bring this cash account across.");
            return;
        }

        var type = CashType(tx);
        var isDeposit = tx.TradeTypeId switch
        {
            100 or 102 or 107 => true,
            101 or 103 or 104 => false,
            _ => inLeg is not null,
        };
        var amount = Math.Round(Math.Abs(leg.Amount ?? tx.Value ?? 0), 2);
        if (amount == 0)
        {
            item.Skip("The cash transaction has no amount.");
            return;
        }

        int? contra = null;
        if (ctx.Settings.CashTypes.TryGetValue(type, out var mapped))
            contra = mapped;
        else if (!OwnerTypes.Contains(type))
        {
            AddNeed(ctx.Setup.CashTypes, new MappingNeed(type, CashTypeLabel(type)));
            item.Kind = SyncKind.Cash;
            item.Ask(InputNeed.Mapping, $"Choose the ledger account for Navexa \"{CashTypeLabel(type)}\" entries (above).");
            return;
        }

        var comment = Comment(tx.Notes is { Length: > 0 } ? tx.Notes : CashTypeLabel(type));
        item.Kind = SyncKind.Cash;
        item.Cash = new SyncCash(bank, item.Date, isDeposit ? amount : 0, isDeposit ? 0 : amount,
            Upper(leg.Currency ?? tx.Currency) ?? ctx.Settings.PortfolioCurrency, comment, contra);
    }

    public static string CashType(NavexaApiTransaction tx) =>
        (tx.TradeType?.Trim().ToLowerInvariant() is { Length: > 0 } t ? t : null) ?? tx.TradeTypeId switch
        {
            100 => "deposit",
            101 => "withdrawal",
            102 => "interest_payment",
            103 => "interest_charged",
            104 => "fee",
            107 => "income_payout",
            { } id => $"type_{id}",
            null => "other",
        };

    public static string CashTypeLabel(string type) => type switch
    {
        "deposit" => "Deposit",
        "withdrawal" => "Withdrawal",
        "interest_payment" => "Interest received",
        "interest_charged" => "Interest charged",
        "fee" => "Fee",
        "income_payout" => "Income paid in (not linked to a dividend)",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(type.Replace('_', ' ')),
    };

    private static bool TryCashAccount(NavexaApiHolding account, PlannedItem item, Context ctx, out string? bank)
    {
        if (ctx.Settings.CashAccounts.TryGetValue(account.Id, out bank))
            return true;
        AddNeed(ctx.Setup.CashAccounts, new MappingNeed(account.Id, account.Name ?? account.Symbol));
        item.Ask(InputNeed.Mapping, $"Choose which bank account Navexa's \"{account.Name ?? account.Symbol}\" cash account is (above).");
        return false;
    }

    // ---------- Holdings ----------

    private static HoldingMapping? ResolveHolding(NavexaApiHolding h, PlannedItem item, Context ctx)
    {
        if (ctx.Settings.Holdings.TryGetValue(h.Id, out var mapped))
        {
            if (mapped is null)
                item.Skip("You chose not to bring this holding across.");
            return mapped;
        }
        var kind = h.HoldingType?.ToUpperInvariant();
        if (kind == "CRYPTO")
        {
            item.Skip("Crypto isn't supported on this site.");
            return null;
        }

        var (exchangeInSymbol, code) = NavexaExchanges.SplitSymbol(h.Symbol);
        var exchange = NavexaExchanges.Map(exchangeInSymbol ?? h.Exchange ?? "ASX");
        code = code.Trim().ToUpperInvariant();
        if (kind is null or "STOCK" && exchange.Recognised && code.Length is > 0 and <= 20 && !code.Contains(' '))
            return new HoldingMapping(exchange.Code, code);

        AddNeed(ctx.Setup.Holdings, new MappingNeed(h.Id, h.Symbol,
            exchange.Recognised ? exchange.Code : (kind == "CUSTOM" ? "CUSTOM" : exchange.Code), SuggestCode(h.Symbol)));
        item.Ask(InputNeed.Mapping, $"Choose the code to record \"{h.Symbol}\" under on this site (above).");
        return null;
    }

    /// <summary>"Brickx Managed Fund: ARN01 - 213 Princes Hwy" suggests ARN01; anything else its first word.</summary>
    public static string SuggestCode(string symbol)
    {
        var s = symbol.Trim();
        var colon = s.IndexOf(':');
        if (colon >= 0)
            s = s[(colon + 1)..].Trim();
        var dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
            s = s[..dash];
        var word = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Truncate(word.ToUpperInvariant(), 20);
    }

    private static void AddNeed(List<MappingNeed> needs, MappingNeed need)
    {
        if (!needs.Any(n => n.Key == need.Key))
            needs.Add(need);
    }

    // ---------- Duplicates of records added by hand or from a file ----------

    private static void CheckExisting(PlannedItem item, ExistingRecords existing, SyncInput input)
    {
        if (input.AddAnyway)
            return;

        string? found = null;
        if (item.Trade is { } t)
        {
            var match = existing.Trades.FirstOrDefault(e => Same(e.Exchange, t.Exchange) && Same(e.Code, t.Code) && Same(e.Type, t.Type)
                && e.Date.Date == t.Date.Date && Math.Abs(e.Quantity) == t.Quantity && Math.Abs(e.Price - t.Price) <= 0.0001m);
            if (match is not null)
                found = $"This portfolio already has a {match.Type} of {Math.Abs(match.Quantity):#,##0.########} {match.Code} on {match.Date:d} at {match.Price:0.####}.";
        }
        else if ((item.Payout?.Exchange ?? item.Drp?.Exchange) is { } ex)
        {
            var code = item.Payout?.Code ?? item.Drp!.Code;
            var cash = item.Payout?.Net ?? item.Drp!.Franked + item.Drp.Unfranked;
            var match = existing.Payouts.FirstOrDefault(e => Same(e.Exchange, ex) && Same(e.Code, code)
                && e.Date.Date == item.Date && Math.Abs(e.Total - cash) <= Tolerance);
            if (match is not null)
                found = $"This portfolio already has a {match.Code} payout of {match.Total:0.00} on {match.Date:d}.";
        }
        else if (item.Cash is { } c)
        {
            var match = existing.Cash.FirstOrDefault(e => Same(e.Account, c.BankAccount) && e.Date.Date == c.Date.Date
                && Math.Abs(e.Deposit - c.Deposit) <= Tolerance && Math.Abs(e.Withdrawal - c.Withdrawal) <= Tolerance);
            if (match is not null)
                found = $"{c.BankAccount} already has a {(c.Deposit > 0 ? "deposit" : "withdrawal")} of {c.Deposit + c.Withdrawal:0.00} on {c.Date:d}.";
        }
        if (found is null)
            return;

        if (input.AlreadyHere)
        {
            item.Status = SyncStatus.Imported;
            item.Message = "You marked this as already in the portfolio.";
            return;
        }
        item.Ask(InputNeed.ConfirmNotDuplicate, found + " Is this the same one?");
    }

    // ---------- Helpers ----------

    private const string NavexaComment = "From Navexa";

    private static string Comment(string? notes) =>
        Truncate(string.IsNullOrWhiteSpace(notes) ? NavexaComment : $"{NavexaComment}: {notes.Trim()}", 500);

    private static NavexaApiLeg? HoldingLeg(NavexaApiTransaction tx) =>
        tx.Legs.FirstOrDefault(l => l.Holding is { } h && !IsCash(h));

    private static bool IsCash(NavexaApiHolding h) =>
        string.Equals(h.HoldingType, "CASH", StringComparison.OrdinalIgnoreCase)
        || (h.HoldingType is null && string.Equals(h.Exchange, "Cash", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Portfolio currency per 1 unit of the record's currency (1.54 for USD in an AUD portfolio).
    /// Worked out from Navexa's own two totals where it can, so the direction of its rate field doesn't matter.
    /// </summary>
    public static decimal PortfolioPerForeign(NavexaApiTransaction tx, string currency, string portfolioCurrency)
    {
        if (string.Equals(currency, portfolioCurrency, StringComparison.OrdinalIgnoreCase))
            return 1m;
        if (tx.Value is { } v && v != 0 && tx.BaseValue is { } b && b != 0
            && string.Equals(tx.Currency ?? currency, currency, StringComparison.OrdinalIgnoreCase))
            return Math.Abs(b / v);
        return tx.ExchangeRate is > 0 and var r ? r : 0m;
    }

    private static string Describe(NavexaApiTransaction tx)
    {
        var holding = HoldingLeg(tx)?.Holding ?? tx.Legs.FirstOrDefault()?.Holding;
        var what = tx.Type switch
        {
            "trade" => tx.TradeType ?? "trade",
            "dividend" => tx.Dividend?.IsReinvested == true ? "dividend (reinvested)" : "dividend",
            "cashTransaction" => CashTypeLabel(CashType(tx)).ToLowerInvariant(),
            _ => tx.Type,
        };
        var amount = tx.Value is { } v ? $" {Math.Abs(v):#,##0.00} {tx.Currency}".TrimEnd() : "";
        return Truncate($"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(what.Replace('_', ' '))} {holding?.Symbol}{amount}".Replace("  ", " "), 300);
    }

    private static string? Upper(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
