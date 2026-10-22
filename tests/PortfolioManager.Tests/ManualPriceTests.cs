using PortfolioManager.Core.Prices;

namespace PortfolioManager.Tests;

public class ManualPriceTests
{
    [Fact]
    public void Reads_code_date_price_with_a_header()
    {
        var p = ManualPriceService.Parse("Code,Date,Price\nmyfund,30/06/2026,1.2345\nHOUSE,2026-06-30,\"850,000\"\n", new DateOnly(2026, 10, 21));
        Assert.Equal(2, p.ValidCount);
        Assert.Equal(new ManualPriceRow("MYFUND", new DateOnly(2026, 6, 30), 1.2345m), p.ValidRows.First());
        Assert.Equal(850000m, p.ValidRows.Last().Price);
    }

    [Fact]
    public void Bad_lines_say_why()
    {
        var p = ManualPriceService.Parse("X,2027-01-01,1\nY,soon,1\nZ,2026-01-01,0\n", new DateOnly(2026, 10, 21));
        Assert.Equal(3, p.ErrorCount);
        Assert.Contains("future", p.Lines[0].Errors[0]);
    }

    [Fact]
    public void Manual_markets_are_left_to_the_user_and_crypto_goes_to_the_provider()
    {
        Assert.True(AssetTypes.IsManual("fund"));
        Assert.True(AssetTypes.IsManual("PROPERTY"));
        Assert.False(AssetTypes.IsManual("CRYPTO"));
        Assert.False(AssetTypes.IsManual("ASX"));
        Assert.Equal("BTC-AUD.CC", EodhdPriceProvider.ToProviderSymbol(new PriceSymbol("CRYPTO", "BTC-AUD")));
    }
}
