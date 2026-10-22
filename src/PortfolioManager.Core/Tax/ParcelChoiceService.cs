using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Trades;

namespace PortfolioManager.Core.Tax;

/// <summary>A sale and the parcels held just before it, for choosing which were sold.</summary>
public sealed record SaleParcels(
    ShareTrade Sale, decimal Units, decimal ProceedsPerUnit, IReadOnlyList<HeldParcel> Held, SaleParcelChoice? Choice,
    IReadOnlyList<CgtEvent> Events, bool ChoiceIsStale);

/// <summary>Saved parcel choices (tblSaleParcelChoice and tblSaleParcelAllocation, 2026-10-21-03-parcel-choice.sql).</summary>
public sealed class ParcelChoiceService(IDbConnectionFactory db, TradesService trades, CorporateActionsService actions)
{
    private IReadOnlyDictionary<int, SaleParcelChoice>? _cached;
    private int _cachedFor;

    /// <summary>The portfolio's choices by sale trade id; none (all FIFO) until the migration is run.</summary>
    public async Task<IReadOnlyDictionary<int, SaleParcelChoice>> GetAsync(int portfolioId)
    {
        if (_cached is not null && _cachedFor == portfolioId)
            return _cached;
        await using var con = db.Create();
        IEnumerable<(int SellTradeId, string Method, int? BuyTradeId, double? Units)> rows;
        try
        {
            rows = await con.QueryAsync<(int, string, int?, double?)>("uspGetSaleParcelChoices", new { iUserID = portfolioId },
                commandType: CommandType.StoredProcedure);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2812)
        {
            rows = []; // the proc isn't there yet
        }
        _cachedFor = portfolioId;
        return _cached = Group(rows);
    }

    public static IReadOnlyDictionary<int, SaleParcelChoice> Group(IEnumerable<(int SellTradeId, string Method, int? BuyTradeId, double? Units)> rows) =>
        rows.GroupBy(r => r.SellTradeId)
            .ToDictionary(g => g.Key, g => new SaleParcelChoice(g.Key, ParcelChoices.Parse(g.First().Method),
                g.Where(r => r.BuyTradeId is > 0 && r.Units is > 0).Select(r => new ParcelPick(r.BuyTradeId!.Value, (decimal)r.Units!.Value)).ToList()));

    /// <summary>The sale, the parcels held just before it (after earlier sales' own choices) and what it takes now. Null if it isn't a sale here.</summary>
    public async Task<SaleParcels?> GetSaleAsync(int portfolioId, int sellTradeId)
    {
        var all = await trades.GetTradesAsync(portfolioId, null, oldestFirst: true);
        var sale = all.FirstOrDefault(t => t.TradeId == sellTradeId && t.Type.Trim().Equals("SELL", StringComparison.OrdinalIgnoreCase));
        if (sale is null)
            return null;

        var choices = await GetAsync(portfolioId);
        var returns = await actions.GetTaxCostBaseLotsAsync(portfolioId);
        var lots = all.Select(ToLot).ToList();
        var saleDate = DateOnly.FromDateTime(sale.Date);
        bool SameStock(string ex, string code) =>
            ex.Trim().Equals(sale.ExchangeCode.Trim(), StringComparison.OrdinalIgnoreCase) && code.Trim().Equals(sale.StockCode.Trim(), StringComparison.OrdinalIgnoreCase);

        // Everything matched before this sale in CgtParcels' order: earlier days, then the same day's buys and earlier sales
        var before = lots.Where(t => SameStock(t.ExchangeCode, t.StockCode)
                && (t.Date < saleDate || (t.Date == saleDate && (t.Type.Trim().Equals("BUY", StringComparison.OrdinalIgnoreCase) || t.TradeId < sale.TradeId))))
            .ToList();
        var held = CgtParcels.Match(before, null, returns.Where(r => r.Date < saleDate), choices).Open
            .Where(p => SameStock(p.ExchangeCode, p.StockCode))
            .Select(p => new HeldParcel(p.BuyTradeId, p.Acquired, p.Units, p.CostPerUnit))
            .ToList();

        var units = (decimal)Math.Abs(sale.Quantity);
        var events = CgtParcels.Match(lots, null, returns, choices).Events.Where(e => e.SellTradeId == sale.TradeId).ToList();
        var choice = choices.GetValueOrDefault(sale.TradeId);
        var stale = choice is not null && ParcelChoices.IsStale(held.Select(h => (h.BuyTradeId, h.Units)).ToList(), units, choice);
        return new SaleParcels(sale, units, (decimal)sale.CostBasisPerUnit, held, choice, events, stale);
    }

    /// <summary>Checks the picks against the parcels held, then saves them with uspSaveSaleParcelChoice.</summary>
    public async Task<ProcResult> SaveAsync(int portfolioId, SaleParcels sale, ParcelMethod method, IReadOnlyList<ParcelPick> picks)
    {
        if (Validate(sale, picks) is { } error)
            return ProcResult.Failed(error);
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspSaveSaleParcelChoice", new
        {
            iUserID = portfolioId,
            iSellTradeID = sale.Sale.TradeId,
            strMethod = method == ParcelMethod.Fifo ? "Specific" : method.ToString(),
            xmlPicks = PicksXml(picks),
        }, commandType: CommandType.StoredProcedure);
        _cached = null;
        return ProcResult.From(result, "Success");
    }

    public async Task<ProcResult> ResetAsync(int portfolioId, int sellTradeId)
    {
        await using var con = db.Create();
        var result = await con.ExecuteScalarAsync<string>("uspDeleteSaleParcelChoice", new { iUserID = portfolioId, iSellTradeID = sellTradeId },
            commandType: CommandType.StoredProcedure);
        _cached = null;
        return ProcResult.From(result, "Success");
    }

    public static string? Validate(SaleParcels sale, IReadOnlyList<ParcelPick> picks)
    {
        var used = picks.Where(p => p.Units != 0).ToList();
        if (used.Count == 0)
            return "Pick at least one parcel.";
        if (used.Any(p => p.Units < 0))
            return "Units picked can't be negative.";
        if (used.GroupBy(p => p.BuyTradeId).Any(g => g.Count() > 1))
            return "Each parcel can be picked once.";
        foreach (var p in used)
        {
            var parcel = sale.Held.FirstOrDefault(h => h.BuyTradeId == p.BuyTradeId);
            if (parcel is null)
                return "A parcel picked wasn't held at the sale.";
            if (p.Units > parcel.Units)
                return $"Only {parcel.Units:#,##0.####} units of the parcel bought {parcel.Acquired:d MMM yyyy} were held.";
        }
        var total = used.Sum(p => p.Units);
        return total == sale.Units ? null : $"The units picked ({total:#,##0.####}) must add up to the units sold ({sale.Units:#,##0.####}).";
    }

    public static string PicksXml(IEnumerable<ParcelPick> picks) =>
        new XElement("picks", picks.Where(p => p.Units > 0).Select(p =>
            new XElement("p", new XAttribute("b", p.BuyTradeId), new XAttribute("u", p.Units.ToString(CultureInfo.InvariantCulture))))).ToString(SaveOptions.DisableFormatting);

    public static TradeLot ToLot(ShareTrade t) =>
        new(t.TradeId, t.ExchangeCode, t.StockCode, t.Type, DateOnly.FromDateTime(t.Date), (decimal)t.Quantity, (decimal)t.CostBasisPerUnit);
}
