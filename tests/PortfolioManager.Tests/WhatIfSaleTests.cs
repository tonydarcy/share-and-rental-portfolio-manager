using PortfolioManager.Core.Tax;

namespace PortfolioManager.Tests;

public class WhatIfSaleTests
{
    private static readonly CgtDiscountRate Individual = new(CgtEntityKind.Individual, 0.5m, false);

    private static readonly TradeLot[] Trades =
    [
        new(1, "ASX", "CBA", "Buy", new DateOnly(2020, 1, 1), 100, 50m),
        new(2, "ASX", "CBA", "Buy", new DateOnly(2026, 6, 1), 100, 120m),
        // An earlier sale this year with a $1,000 gain on a parcel held under a year
        new(3, "ASX", "BHP", "Buy", new DateOnly(2026, 1, 1), 100, 40m),
        new(4, "ASX", "BHP", "Sell", new DateOnly(2026, 8, 1), 100, 50m),
    ];

    private static WhatIfResult Run(decimal units, decimal price, ParcelMethod method = ParcelMethod.Fifo, decimal brokerage = 0) =>
        WhatIf.Run(new PlannedSale("ASX", "CBA", new DateOnly(2026, 10, 1), units, price, brokerage, method), Trades, new Dictionary<int, SaleParcelChoice>(), [], Individual, 0.32m);

    [Fact]
    public void Fifo_sale_of_the_old_parcel_is_discounted()
    {
        var r = Run(100, 100);

        Assert.Null(r.Error);
        var e = Assert.Single(r.Events);
        Assert.Equal((new DateOnly(2020, 1, 1), 5000m), (e.Acquired, e.Gain));
        Assert.Equal(1000m, r.Before.NetCapitalGain);
        Assert.Equal(1000m + 2500m, r.After.NetCapitalGain);
        Assert.Equal(2500m, r.NetGainChange);
        Assert.Equal(800m, r.TaxChange);
        Assert.Equal(2027, r.Year.FinancialYear);
    }

    [Fact]
    public void Smallest_gain_takes_the_loss_which_offsets_this_years_gain()
    {
        var r = Run(100, 100, ParcelMethod.MinGain);

        Assert.Equal(-2000m, Assert.Single(r.Events).Gain);
        Assert.Equal(0m, r.After.NetCapitalGain);
        Assert.Equal(-1000m, r.NetGainChange);
        Assert.Equal(-320m, r.TaxChange);
        Assert.Equal(1000m, r.LossCarriedForwardChange);
    }

    [Fact]
    public void Brokerage_comes_off_the_proceeds()
    {
        var r = Run(100, 100, brokerage: 20);
        Assert.Equal(9980m, r.Sale.Proceeds);
        Assert.Equal(4980m, r.Gain);
    }

    [Fact]
    public void Selling_more_than_is_held_says_so()
    {
        Assert.Contains("Only 200 units", Run(250, 100).Error);
        Assert.NotNull(Run(0, 100).Error);
    }

    [Fact]
    public void Default_rates_by_entity()
    {
        Assert.Equal(0.30m, WhatIf.DefaultTaxRate(new(CgtEntityKind.Company, 0, false)));
        Assert.Equal(0.15m, WhatIf.DefaultTaxRate(new(CgtEntityKind.SuperFund, 1m / 3, false)));
        Assert.Equal(0.32m, WhatIf.DefaultTaxRate(Individual));
    }
}
