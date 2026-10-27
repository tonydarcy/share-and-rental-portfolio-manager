-- =============================================
-- Holdings: also return each holding's cost (buys less sales and returns of capital),
-- so the API/MCP get_holdings tool can show it. Safe to run twice.
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[GetProfitSummaryv2]
(
    @iUserID INT,
    @iGroupID INT = NULL,
    @bShowOpenPositionsOnly BIT = 1,
    @bShowTotalRows BIT = 1
)
AS
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        27-Oct-2026
-- Description:	Returns of capital (tblReturnsOfCapital) come off the cost,
--              like sale proceeds. Also returns the cost as its own column.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    -- 1. Get User's Portfolio Currency (e.g. AUD)
    DECLARE @PortCurrency VARCHAR(10);
    SELECT TOP 1 @PortCurrency = strPortfolioCurrency FROM tblUsers WHERE iUserId = @iUserID;

    -- 2. Create Temp Table
    CREATE TABLE #PortfolioCalc (
        strStockExchangeCode VARCHAR(50),
        strStockCode VARCHAR(50),
        strCurrency VARCHAR(10),
        iQuantity DECIMAL(19,4),
        dblExchangeRate DECIMAL(19,8) DEFAULT 1,

        -- Prices
        dblMarketPriceLocal DECIMAL(19,4),
        dblMarketPricePort DECIMAL(19,4),

        -- Values
        dblMarketValue DECIMAL(19,4),
        dblTotalCost DECIMAL(19,4),
        dblCapitalGain DECIMAL(19,4),
        dblTotalPayouts DECIMAL(19,4),
        dblTotalProfit DECIMAL(19,4),
        strTrend VARCHAR(50)
    );

    -- 3. Populate Trades (Grouped by Stock AND Currency)
    INSERT INTO #PortfolioCalc (strStockExchangeCode, strStockCode, strCurrency, iQuantity, dblTotalCost)
    SELECT
        strStockExchangeCode,
        strStockCode,
        MAX(strCurrency), -- Assuming a stock doesn't change currency
        SUM(CASE WHEN strType = 'BUY' THEN iQuantity ELSE -iQuantity END),
        SUM(CASE WHEN strType = 'BUY' THEN dblOrderTotal ELSE -dblOrderTotal END) -- Cost in Portfolio Currency (from trade history)
    FROM tblShareTrades
    WHERE iUserId = @iUserID
    GROUP BY strStockExchangeCode, strStockCode;

    -- 3b. Returns of capital: cash back out of the holding
    UPDATE T
    SET T.dblTotalCost = T.dblTotalCost - R.TotalReturned
    FROM #PortfolioCalc T
    JOIN (
        SELECT strStockExchangeCode, strStockCode, SUM(dblTotalAmount) AS TotalReturned
        FROM tblReturnsOfCapital
        WHERE iUserID = @iUserID
        GROUP BY strStockExchangeCode, strStockCode
    ) R ON R.strStockExchangeCode = T.strStockExchangeCode AND R.strStockCode = T.strStockCode;

    -- 4. Group Filter
    IF @iGroupID IS NOT NULL
    BEGIN
        DELETE T FROM #PortfolioCalc T
        WHERE NOT EXISTS (
            SELECT 1 FROM tblShareTradeGroups G
            JOIN tblShareTrades ST ON G.iShareTradeID = ST.iShareTradeId
            WHERE ST.strStockCode = T.strStockCode
              AND G.iGroupID = @iGroupID
        );
    END

    -- 5. Update Current Market Prices (Local Currency)
    UPDATE T
    SET T.dblMarketPriceLocal = P.dblClosePrice,
        T.strTrend = CASE
            WHEN P.dblClosePrice > P.dblClosePrice200DayAverage THEN 'Bullish'
            WHEN P.dblClosePrice < P.dblClosePrice200DayAverage THEN 'Bearish'
            ELSE 'Neutral'
        END
    FROM #PortfolioCalc T
    CROSS APPLY (
        SELECT TOP 1 dblClosePrice, dblClosePrice200DayAverage
        FROM tblSharePriceCache
        WHERE strStockCode = T.strStockCode AND strStockExchangeCode = T.strStockExchangeCode
        ORDER BY dtmDetailsAsAtDate DESC
    ) P;

    -- 6. Update Exchange Rates (Current FX)
    -- If Stock Currency != Portfolio Currency, find the FX rate
    UPDATE T
    SET dblExchangeRate = FX.dblClosePrice
    FROM #PortfolioCalc T
    CROSS APPLY (
        SELECT TOP 1 dblClosePrice
        FROM tblSharePriceCache
        WHERE strStockExchangeCode = 'FOREX'
          AND REPLACE(strStockCode, '/', '') = T.strCurrency + @PortCurrency -- e.g. USDAUD
        ORDER BY dtmDetailsAsAtDate DESC
    ) FX
    WHERE T.strCurrency <> @PortCurrency;

    -- 7. Calculate Portfolio Values
    -- Market Value = Qty * LocalPrice * FX Rate
    UPDATE #PortfolioCalc
    SET dblMarketPricePort = dblMarketPriceLocal * dblExchangeRate,
        dblMarketValue = (iQuantity * dblMarketPriceLocal) * dblExchangeRate;

    -- 8. Add Dividends
    UPDATE T
    SET T.dblTotalPayouts = ISNULL(Div.TotalDivs, 0)
    FROM #PortfolioCalc T
    LEFT JOIN (
        SELECT strStockCode, strStockExchangeCode, SUM(dblTotal) as TotalDivs
        FROM tblPayouts
        WHERE iUserID = @iUserID
        GROUP BY strStockCode, strStockExchangeCode
    ) Div ON T.strStockCode = Div.strStockCode AND T.strStockExchangeCode = Div.strStockExchangeCode;

    -- 9. Final Math
    UPDATE #PortfolioCalc
    SET dblCapitalGain = dblMarketValue - dblTotalCost,
        dblTotalProfit = (dblMarketValue - dblTotalCost) + dblTotalPayouts;

    -- 10. Final Select (Using the Column Names your GridView expects)
    SELECT
        strStockCode,
        strStockExchangeCode,
        iQuantity AS [Current Holding],
        ISNULL(dblMarketPricePort, 0) AS [Market Price (Portfolio Currency)],
        ISNULL(dblMarketValue, 0) AS [Market Value (Portfolio Currency)],
        ISNULL(dblTotalCost, 0) AS [Total Cost (Portfolio Currency)],
        ISNULL(dblCapitalGain, 0) AS [Total Capital Gain / Loss (Portfolio Currency)],
        ISNULL(dblTotalPayouts, 0) AS [Total Payouts],
        ISNULL(dblTotalProfit, 0) AS [Total Profit / Loss (Portfolio Currency)],
        ISNULL(strTrend, '-') AS [Trend Direction]
    FROM #PortfolioCalc
    WHERE (@bShowOpenPositionsOnly = 0 OR iQuantity <> 0)

    UNION ALL

    -- Footer Row
    SELECT
        NULL, 'TOTAL', NULL, NULL,
        SUM(ISNULL(dblMarketValue, 0)),
        SUM(ISNULL(dblTotalCost, 0)),
        SUM(ISNULL(dblCapitalGain, 0)),
        SUM(ISNULL(dblTotalPayouts, 0)),
        SUM(ISNULL(dblTotalProfit, 0)),
        ''
    FROM #PortfolioCalc
    WHERE @bShowTotalRows = 1 AND (@bShowOpenPositionsOnly = 0 OR iQuantity <> 0);

    DROP TABLE #PortfolioCalc;
END
GO
