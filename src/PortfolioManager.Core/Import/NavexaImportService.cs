using System.Data;
using Dapper;
using PortfolioManager.Core.Crypto;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Import;

public sealed record NavexaPreview(
    string PortfolioCurrency,
    NavexaParseResult<NavexaTrade>? Trades,
    NavexaParseResult<NavexaPayout>? Income)
{
    public int TradesToImport => Trades?.Ready.Count() ?? 0;
    public int PayoutsToImport => Income?.Ready.Count() ?? 0;
}

public sealed record NavexaImportResult(int Trades, int Payouts);

/// <summary>One-off import of a Navexa trades and income export into a portfolio, through the usual procs.</summary>
public sealed class NavexaImportService(IDbConnectionFactory db)
{
    /// <summary>Parses the files and checks them against what the portfolio already has. Writes nothing.</summary>
    public async Task<NavexaPreview> PreviewAsync(int portfolioId, string? tradesCsv, string? incomeCsv, bool ratesAreForeignPerPortfolioUnit)
    {
        await using var con = db.Create();
        var currency = await con.ExecuteScalarAsync<string?>(
            "SELECT strPortfolioCurrency FROM tblUsers WHERE iUserId = @portfolioId", new { portfolioId });
        var options = new NavexaOptions(string.IsNullOrWhiteSpace(currency) ? "AUD" : currency.Trim().ToUpperInvariant(),
            ratesAreForeignPerPortfolioUnit, DateTime.Today);

        NavexaParseResult<NavexaTrade>? trades = null;
        if (!string.IsNullOrWhiteSpace(tradesCsv))
        {
            trades = NavexaParser.ParseTrades(tradesCsv, options);
            var existing = await con.QueryAsync<ExistingTrade>(
                "SELECT ISNULL(strStockExchangeCode, '') AS Exchange, ISNULL(strStockCode, '') AS Code, ISNULL(strType, '') AS Type, " +
                "CAST(dtmDateOfTransaction AS datetime) AS Date, CAST(ROUND(ISNULL(iQuantity, 0), 8) AS decimal(28, 8)) AS Quantity, " +
                "CAST(ISNULL(dblPricePerUnit, 0) AS decimal(19, 6)) AS Price " +
                "FROM tblShareTrades WHERE iUserId = @portfolioId",
                new { portfolioId });
            NavexaDuplicates.MarkTrades(trades.Rows, existing);
        }

        NavexaParseResult<NavexaPayout>? income = null;
        if (!string.IsNullOrWhiteSpace(incomeCsv))
        {
            income = NavexaParser.ParseIncome(incomeCsv, options);
            var existing = await con.QueryAsync<ExistingPayout>(
                "SELECT ISNULL(strStockExchangeCode, '') AS Exchange, ISNULL(strStockCode, '') AS Code, " +
                "CAST(dtmTransactionDate AS datetime) AS Date, CAST(ISNULL(dblTotal, 0) AS decimal(19, 4)) AS Total " +
                "FROM tblPayouts WHERE iUserID = @portfolioId AND dtmTransactionDate IS NOT NULL",
                new { portfolioId });
            NavexaDuplicates.MarkPayouts(income.Rows, existing);
        }

        return new NavexaPreview(options.PortfolioCurrency, trades, income);
    }

    /// <summary>
    /// Adds the preview's ready rows in one database transaction, so a failure part-way leaves nothing behind.
    /// Trades go through uspAddShareTrade, or AddShareTradeWithCashTransaction when withCash is set;
    /// payouts through uspAddPayout. Those procs post the journals for double-entry portfolios.
    /// </summary>
    public async Task<NavexaImportResult> ImportAsync(int portfolioId, NavexaPreview preview, bool withCash)
    {
        var trades = (preview.Trades?.Ready ?? [])
            .OrderBy(t => t.Date).ThenBy(t => t.Type == "Sell").ThenBy(t => t.Line).ToList();
        var payouts = (preview.Income?.Ready ?? []).OrderBy(p => p.Date).ThenBy(p => p.Line).ToList();

        await using var con = db.Create();
        await con.OpenAsync();
        await using var tx = await con.BeginTransactionAsync();

        foreach (var t in trades)
        {
            if (t.Type == "Reward")
            {
                // Coins received as income: other income paid as units bought at their value, with no cash
                var reward = new NewReward(DateOnly.FromDateTime(t.Date), t.Code, t.Quantity, Math.Round(t.Price * t.Quantity / t.ExchangeRate, 2),
                    t.RawType.Contains("airdrop", StringComparison.OrdinalIgnoreCase) ? RewardKinds.Airdrop
                        : t.RawType.Contains("mining", StringComparison.OrdinalIgnoreCase) ? RewardKinds.Mining : RewardKinds.Staking,
                    t.Comment);
                if (await CryptoService.PostRewardAsync(con, tx, portfolioId, reward) is { Ok: false } failed)
                    throw new InvalidOperationException($"Line {t.Line}: {failed.Error}");
            }
            else if (withCash)
            {
                await con.ExecuteAsync("AddShareTradeWithCashTransaction", new
                {
                    iUserId = portfolioId,
                    strMarket = t.Exchange,
                    strCode = t.Code,
                    strName = t.Name,
                    strType = t.Type,
                    dtmDateOfTransaction = t.Date,
                    iQuantity = (double)t.Quantity,
                    dblPricePerUnit = (double)t.Price,
                    dblBrokerage = (double)t.Brokerage,
                    strCurrency = t.Currency,
                    dblExchangeRate = (double)t.ExchangeRate,
                    strShareTradeComment = t.Comment,
                    // Left null so the proc picks the bank account, T+2 settlement and its usual comment
                    strBankAccountName = (string?)null,
                    dtmSettlementDate = (DateTime?)null,
                    strCashTransactionComment = (string?)null,
                }, tx, commandType: CommandType.StoredProcedure);
            }
            else
            {
                await con.ExecuteAsync("uspAddShareTrade", new
                {
                    iUserId = portfolioId,
                    strMarket = t.Exchange,
                    strCode = t.Code,
                    strName = t.Name,
                    strType = t.Type,
                    dtmDateOfTransaction = t.Date,
                    iQuantity = (double)t.Quantity,
                    dblPricePerUnit = (double)t.Price,
                    dblBrokerage = (double)t.Brokerage,
                    strCurrency = t.Currency,
                    dblExchangeRate = (double)t.ExchangeRate,
                    strComment = t.Comment,
                    iBankStatementId = (int?)null,
                }, tx, commandType: CommandType.StoredProcedure);
            }
        }

        foreach (var p in payouts)
        {
            await con.ExecuteAsync("uspAddPayout", new
            {
                iUserId = portfolioId,
                strStockExchangeCode = p.Exchange,
                strStockCode = p.Code,
                dtmTransactionDate = p.Date,
                dblTotal = (double)p.Net,
                dblNetAmount = (double)p.Net,
                dblFrankedAmount = (double)p.Franked,
                dblUnFrankedAmount = (double)p.Unfranked,
                dblInterestAmount = 0d,
                dblTFNWithholdingAmount = (double)p.TfnWithheld,
                dblForeignIncomeTaxAmount = (double)p.ForeignTax,
                dblOtherAmount = 0d,
                strComment = p.Comment,
                strPayoutConfirmed = "Yes",
                dblFrankingCredit = (double)p.FrankingCredit,
                dblTrustIncome = 0d,
            }, tx, commandType: CommandType.StoredProcedure);
        }

        await tx.CommitAsync();
        return new NavexaImportResult(trades.Count, payouts.Count);
    }
}
