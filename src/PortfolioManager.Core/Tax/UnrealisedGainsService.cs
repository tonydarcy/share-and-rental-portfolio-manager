using System.Data;
using Dapper;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.Trades;

namespace PortfolioManager.Core.Tax;

/// <summary>One open parcel, or one holding when grouped. MarketPrice is null when no price is on file.</summary>
public sealed record UnrealisedGainLine(
    string ExchangeCode,
    string StockCode,
    decimal Units,
    decimal CostPerUnit,
    decimal TotalCost,
    decimal? MarketPrice,
    decimal MarketValue,
    decimal Gain);

public sealed record UnrealisedGainsTotal(decimal TotalCost, decimal MarketValue, decimal Gain);

public sealed record UnrealisedGainsReport(IReadOnlyList<UnrealisedGainLine> Lines, UnrealisedGainsTotal Total)
{
    public bool AnyMissingPrices => Lines.Any(l => l.MarketPrice is null);
}

/// <summary>
/// Gains and losses on open parcels if sold at the as-at date's prices. The parcels are the tax figures' parcels
/// (<see cref="CgtDiscountService"/>: parcel choices on earlier sales and returns of capital count); the prices are the
/// last close in the year before the date, from GetUnrealizedCapitalGains. A holding in another currency is converted
/// at the exchange rate on the date (<see cref="FxRateService"/>).
/// </summary>
public sealed class UnrealisedGainsService(IDbConnectionFactory db, CgtDiscountService cgt, TradesService trades, FxRateService fx)
{
    public async Task<UnrealisedGainsReport> GetAsync(int portfolioId, DateOnly asAt, bool groupByStock)
    {
        var open = (await cgt.GetOpenParcelsAsync(portfolioId, asAt)).Open;
        IReadOnlyList<UnrealisedGainLine> priced;
        await using (var con = db.Create())
        {
            // Today or later goes as null (the proc's "latest"), so it reads the small latest-price cache:
            // before the 2026-10-27-02 migration it compared today with GETDATE() and read the whole price history.
            DateTime? priceDate = asAt >= DateOnly.FromDateTime(DateTime.Today) ? null : asAt.ToDateTime(TimeOnly.MinValue);
            var rows = await con.QueryAsync("GetUnrealizedCapitalGains",
                new { iUserID = portfolioId, strGroupByStockCode = "Yes", dtmDetailsAsAtDate = priceDate },
                commandType: CommandType.StoredProcedure);
            priced = Map(rows.Cast<IDictionary<string, object?>>()).Lines;
        }

        // Each holding's trade currency is its latest trade's
        var portfolioCurrency = RegionSettings.Current.Currency;
        var currencies = (await trades.GetTradesAsync(portfolioId, null, oldestFirst: true))
            .Where(t => DateOnly.FromDateTime(t.Date) <= asAt)
            .GroupBy(t => Key(t.ExchangeCode, t.StockCode))
            .ToDictionary(g => g.Key, g => string.IsNullOrWhiteSpace(g.Last().Currency) ? portfolioCurrency : g.Last().Currency.Trim().ToUpperInvariant());
        var rates = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in currencies.Values.Distinct().Where(c => !c.Equals(portfolioCurrency, StringComparison.OrdinalIgnoreCase)))
            rates[c] = (await fx.GetAsync(c, portfolioCurrency, asAt))?.Rate;

        var prices = new Dictionary<string, decimal>();
        foreach (var l in priced.Where(l => l.MarketPrice is not null))
        {
            var key = Key(l.ExchangeCode, l.StockCode);
            var currency = currencies.GetValueOrDefault(key, portfolioCurrency);
            if (currency.Equals(portfolioCurrency, StringComparison.OrdinalIgnoreCase))
                prices.TryAdd(key, l.MarketPrice!.Value);
            else if (rates.GetValueOrDefault(currency) is { } rate and > 0)
                prices.TryAdd(key, l.MarketPrice!.Value / rate);
        }
        return Build(open, prices, groupByStock);
    }

    /// <summary>
    /// A line per open parcel, or per holding when grouped, valued at the holding's price (in the portfolio's currency).
    /// A holding with no price (or no exchange rate) shows as missing with a value of nil.
    /// </summary>
    public static UnrealisedGainsReport Build(IEnumerable<OpenParcel> open, IReadOnlyDictionary<string, decimal> prices, bool groupByStock)
    {
        UnrealisedGainLine Line(string exchange, string code, decimal units, decimal cost)
        {
            decimal? price = prices.TryGetValue(Key(exchange, code), out var p) ? p : null;
            var value = price is { } x ? Math.Round(units * x, 2, MidpointRounding.AwayFromZero) : 0;
            return new UnrealisedGainLine(exchange, code, units, units == 0 ? 0 : Math.Round(cost / units, 4, MidpointRounding.AwayFromZero),
                cost, price is null ? null : Math.Round(price.Value, 4, MidpointRounding.AwayFromZero), value, value - cost);
        }

        var parcels = open.Where(p => p.Units > 0).ToList();
        var lines = groupByStock
            ? parcels.GroupBy(p => Key(p.ExchangeCode, p.StockCode))
                .Select(g => Line(g.First().ExchangeCode.Trim(), g.First().StockCode.Trim(), g.Sum(p => p.Units), g.Sum(p => p.TotalCost)))
                .ToList()
            : parcels.Select(p => Line(p.ExchangeCode.Trim(), p.StockCode.Trim(), p.Units, p.TotalCost)).ToList();
        lines = lines
            .OrderBy(l => l.StockCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.ExchangeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new UnrealisedGainsReport(lines, new UnrealisedGainsTotal(lines.Sum(l => l.TotalCost), lines.Sum(l => l.MarketValue), lines.Sum(l => l.Gain)));
    }

    public static string Key(string exchange, string code) => $"{exchange.Trim()}:{code.Trim()}".ToUpperInvariant();

    /// <summary>
    /// Turns the proc's rows (used here for its prices) into lines plus a total. As with the CGT proc, the "Total:" row is
    /// added with a UNION and can come back anywhere; lines are sorted by code then exchange.
    /// The proc leaves the price NULL (or 0 when grouped) and the value 0 when it has no price,
    /// so a zero price is shown as missing.
    /// </summary>
    public static UnrealisedGainsReport Map(IEnumerable<IDictionary<string, object?>> rows)
    {
        var lines = new List<UnrealisedGainLine>();
        UnrealisedGainsTotal? total = null;

        foreach (var r in rows)
        {
            var exchange = Convert.ToString(r["Stock Exchange"])?.Trim() ?? "";
            if (exchange == "Total:")
            {
                total = new UnrealisedGainsTotal(Num.Dec(r, "Total Cost Basis"), Num.Dec(r, "Market Value"), Num.Dec(r, "Total Gain / Loss"));
                continue;
            }

            var price = Num.DecOrNull(r, "Market Price");
            lines.Add(new UnrealisedGainLine(
                exchange,
                Convert.ToString(r["Stock Code"])?.Trim() ?? "",
                Num.Dec(r, "Current Holding"),
                Num.Dec(r, "Cost Basis Per Unit"),
                Num.Dec(r, "Total Cost Basis"),
                price is null or 0m ? null : price,
                Num.Dec(r, "Market Value"),
                Num.Dec(r, "Total Gain / Loss")));
        }

        lines = lines
            .OrderBy(l => l.StockCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.ExchangeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        total ??= new UnrealisedGainsTotal(lines.Sum(l => l.TotalCost), lines.Sum(l => l.MarketValue), lines.Sum(l => l.Gain));
        return new UnrealisedGainsReport(lines, total);
    }
}
