namespace PortfolioManager.Core.NavexaSync;

/// <summary>Stored proc parameters for each kind of record the Navexa sync adds.</summary>
public static class NavexaSyncParameters
{
    /// <summary>
    /// uspAddShareTrade, or AddShareTradeWithCashTransaction when the trade settles in a bank account
    /// (the proc picks T+2 settlement and its usual cash comment).
    /// </summary>
    public static Dictionary<string, object?> Trade(int portfolioId, SyncTrade t)
    {
        var p = new Dictionary<string, object?>
        {
            ["iUserId"] = portfolioId,
            ["strMarket"] = t.Exchange,
            ["strCode"] = t.Code,
            ["strName"] = t.Name,
            ["strType"] = t.Type,
            ["dtmDateOfTransaction"] = t.Date.Date,
            ["iQuantity"] = t.Quantity,
            ["dblPricePerUnit"] = (double)t.Price,
            ["dblBrokerage"] = (double)t.Brokerage,
            ["strCurrency"] = t.Currency,
            ["dblExchangeRate"] = (double)t.ExchangeRate,
        };
        if (t.BankAccount is null)
        {
            p["strComment"] = t.Comment;
            p["iBankStatementId"] = null;
        }
        else
        {
            p["strShareTradeComment"] = t.Comment;
            p["strBankAccountName"] = t.BankAccount;
            p["dtmSettlementDate"] = null;
            p["strCashTransactionComment"] = null;
        }
        return p;
    }

    public static Dictionary<string, object?> Payout(int portfolioId, SyncPayout p) => new()
    {
        ["iUserId"] = portfolioId,
        ["strStockExchangeCode"] = p.Exchange,
        ["strStockCode"] = p.Code,
        ["dtmTransactionDate"] = p.Date.Date,
        ["dblTotal"] = (double)p.Net,
        ["dblNetAmount"] = (double)p.Net,
        ["dblFrankedAmount"] = (double)p.Franked,
        ["dblUnFrankedAmount"] = (double)p.Unfranked,
        ["dblInterestAmount"] = 0d,
        ["dblTFNWithholdingAmount"] = 0d,
        ["dblForeignIncomeTaxAmount"] = (double)p.ForeignTax,
        ["dblOtherAmount"] = 0d,
        ["strComment"] = p.Comment,
        ["strPayoutConfirmed"] = "Yes",
        ["dblFrankingCredit"] = (double)p.FrankingCredit,
        ["dblTrustIncome"] = 0d,
    };

    /// <summary>uspAddDRPTransaction: the payout and the buy of the new units, as on the DRP page.</summary>
    public static Dictionary<string, object?> Drp(int portfolioId, SyncDrp d) => new()
    {
        ["iUserId"] = portfolioId,
        ["strStockExchangeCode"] = d.Exchange,
        ["strStockCode"] = d.Code,
        ["dtmTransactionDate"] = d.Date.Date,
        ["dblTotalPayout"] = (double)(d.Franked + d.Unfranked),
        ["dblFrankedAmount"] = (double)d.Franked,
        ["dblUnFrankedAmount"] = (double)d.Unfranked,
        ["dblFrankingCredit"] = (double)d.FrankingCredit,
        ["iQuantity"] = d.Units,
        ["dblPricePerUnit"] = (double)d.Price,
        ["strComment"] = d.Comment,
        ["dblTFNWithholdingAmount"] = 0d,
    };

    public static Dictionary<string, object?> Cash(int portfolioId, SyncCash c) => new()
    {
        ["iUserId"] = portfolioId,
        ["strBankAccountName"] = c.BankAccount,
        ["dtmDateOfTransaction"] = c.Date.Date,
        ["dblWithdrawalAmount"] = c.Withdrawal,
        ["dblDepositAmount"] = c.Deposit,
        ["strComment"] = c.Comment,
        ["strCurrency"] = c.Currency,
        ["iContraAccountID"] = c.ContraAccountId,
    };

    public static Dictionary<string, object?> Split(int portfolioId, SyncSplit s) => new()
    {
        ["iUserId"] = portfolioId,
        ["strStockExchangeCode"] = s.Exchange,
        ["strStockCode"] = s.Code,
        ["dtmSplitDate"] = s.Date.Date,
        ["dblSplitRatio"] = (double)s.NewUnits / s.OldUnits,
        ["iNewUnits"] = s.NewUnits,
        ["iOldUnits"] = s.OldUnits,
    };

    public static Dictionary<string, object?> CapitalReturn(int portfolioId, SyncCapitalReturn r) => new()
    {
        ["iUserId"] = portfolioId,
        ["strStockExchangeCode"] = r.Exchange,
        ["strStockCode"] = r.Code,
        ["dtmPaymentDate"] = r.Date.Date,
        ["dblTotalAmount"] = r.Total,
        ["dblAmountPerUnit"] = null,
        ["strComment"] = r.Comment,
    };
}
