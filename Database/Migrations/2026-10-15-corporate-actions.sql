-- =============================================
-- Migration: corporate actions (splits, consolidations, returns of capital, DRP)
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database AFTER 2026-10-15-ledger-fixes.sql
-- and BEFORE deploying the new site build with the Corporate actions pages.
-- The old site keeps working: no procedure is dropped or renamed, and every
-- changed procedure keeps its parameters (new ones are optional, at the end).
-- Safe to run more than once.
--
-- What it changes:
--   * tblCorporateActionTrades (new): the trades each split changed, with
--     their values before and after, so a split can be undone exactly.
--   * tblReturnsOfCapital (new): returns of capital per holding.
--   * uspApplyStockSplit: keeps fractional units (it used to round each
--     trade, which lost cost base on a consolidation), takes an exact
--     "new for old" ratio, refuses duplicates, notes the trades it changed
--     and returns one row (Result, iActionID, iTradesAdjusted).
--   * uspDeleteStockSplit (new): undoes a split and deletes it.
--   * uspAddReturnOfCapital / uspDeleteReturnOfCapital (new): record or
--     delete a return of capital. In Hybrid / DoubleEntry portfolios it posts
--       Dr  Bank                      cash received
--       Cr  <CODE>.<EXCH> - Cost Base cash received
--     (the stock's own cost base account, as uspAddShareTrade uses).
--   * GetProfitSummaryv2: returns of capital reduce a holding's cost.
--   * uspAddDRPTransaction: same parameters plus optional TFN withheld and
--     name; posts balanced journals (payout: Dr Bank / Cr Dividend Income;
--     new units: Dr Cost Base / Cr Bank, so the bank nets to any residual),
--     uses the holding's name, and returns one row
--     (Result, iPayoutID, iShareTradeID).
--   * uspDeletePayout: deleting a DRP payout also deletes its reinvestment
--     trade and that trade's journal (refused if CGT parcels use the trade).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- 1. Pre-flight: the ledger fixes must be in place
-- =============================================
IF OBJECT_ID('dbo.uspGetLedgerAccount', 'P') IS NULL OR OBJECT_ID('dbo.uspPostPayoutJournal', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-ledger-fixes.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =============================================
-- 2. Tables
-- =============================================
IF OBJECT_ID('dbo.tblCorporateActionTrades', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[tblCorporateActionTrades](
        [iActionID] [int] NOT NULL,
        [iShareTradeId] [int] NOT NULL,
        [dblQuantityBefore] [float] NULL,
        [dblPriceBefore] [float] NULL,
        [dblCostBasisBefore] [float] NULL,
        [dblQuantityAfter] [float] NULL,
        [dblPriceAfter] [float] NULL,
        [dblCostBasisAfter] [float] NULL,
        CONSTRAINT [PK_tblCorporateActionTrades] PRIMARY KEY CLUSTERED ([iActionID] ASC, [iShareTradeId] ASC),
        CONSTRAINT [FK_CorporateActionTrades_Action] FOREIGN KEY ([iActionID])
            REFERENCES [dbo].[tblCorporateActions] ([iActionID]) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID('dbo.tblReturnsOfCapital', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[tblReturnsOfCapital](
        [iReturnOfCapitalID] [int] IDENTITY(1,1) NOT NULL,
        [iUserID] [int] NOT NULL,
        [strStockExchangeCode] [varchar](50) NOT NULL,
        [strStockCode] [varchar](50) NOT NULL,
        [dtmPaymentDate] [date] NOT NULL,
        [dblAmountPerUnit] [float] NULL,
        [dblTotalAmount] [decimal](19, 4) NOT NULL,
        [strComment] [varchar](500) NULL,
        [dtmCreatedDate] [datetime] NOT NULL CONSTRAINT [DF_tblReturnsOfCapital_Created] DEFAULT (GETDATE()),
        CONSTRAINT [PK_tblReturnsOfCapital] PRIMARY KEY CLUSTERED ([iReturnOfCapitalID] ASC)
    );
    CREATE INDEX [IX_tblReturnsOfCapital_Holding] ON [dbo].[tblReturnsOfCapital] ([iUserID], [strStockExchangeCode], [strStockCode]);
END
GO

-- =============================================
-- 3. Share splits and consolidations
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspApplyStockSplit]
(
    @iUserId INT,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode VARCHAR(50),
    @dtmSplitDate DATE,
    @dblSplitRatio FLOAT, -- new units per old unit: 4 for a 4:1 split, 0.5 for a 1:2 consolidation
    @iNewUnits INT = NULL, -- optional exact ratio, e.g. 1 and 10 for a 1-for-10 consolidation
    @iOldUnits INT = NULL
)
AS
-- =============================================
-- Description: Adjusts the trades before a split or consolidation so every
--              report sees the holding in today's units. Trades dated before
--              @dtmSplitDate (the first day on the new basis) get units times
--              the ratio and price and cost per unit divided by it; order
--              totals, and so the cost base, don't change.
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description:	No rounding of units (a consolidation used to lose cost base),
--              exact new-for-old ratio, duplicate check, notes each trade's
--              values so uspDeleteStockSplit can undo it. Returns one row:
--              Result ('Split Applied Successfully' or 'Error: ...'),
--              iActionID, iTradesAdjusted.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @New FLOAT, @Old FLOAT;
    IF @iNewUnits > 0 AND @iOldUnits > 0
        SELECT @New = @iNewUnits, @Old = @iOldUnits, @dblSplitRatio = CAST(@iNewUnits AS FLOAT) / @iOldUnits;
    ELSE
        SELECT @New = @dblSplitRatio, @Old = 1;

    IF @dblSplitRatio IS NULL OR @dblSplitRatio <= 0 OR @dblSplitRatio = 1
    BEGIN
        SELECT 'Error: The ratio must be more than zero and not one for one.' AS Result, CAST(NULL AS INT) AS iActionID, 0 AS iTradesAdjusted;
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM tblShareTrades
                   WHERE iUserId = @iUserId AND strStockExchangeCode = @strStockExchangeCode
                     AND strStockCode = @strStockCode AND dtmDateOfTransaction < @dtmSplitDate)
    BEGIN
        SELECT 'Error: There are no trades in this holding before that date, so there is nothing to adjust.' AS Result, CAST(NULL AS INT) AS iActionID, 0 AS iTradesAdjusted;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblCorporateActions
               WHERE iUserID = @iUserId AND strStockExchangeCode = @strStockExchangeCode
                 AND strStockCode = @strStockCode AND dtmActionDate = @dtmSplitDate
                 AND strActionType IN ('SPLIT', 'CONSOLIDATION'))
    BEGIN
        SELECT 'Error: A split or consolidation is already recorded for this holding on that date.' AS Result, CAST(NULL AS INT) AS iActionID, 0 AS iTradesAdjusted;
        RETURN;
    END

    DECLARE @iActionID INT, @iTrades INT;

    BEGIN TRANSACTION;

    INSERT INTO tblCorporateActions (iUserID, strStockExchangeCode, strStockCode, dtmActionDate, strActionType, dblRatio, dtmDateApplied)
    VALUES (@iUserId, @strStockExchangeCode, @strStockCode, @dtmSplitDate,
            CASE WHEN @dblSplitRatio > 1 THEN 'SPLIT' ELSE 'CONSOLIDATION' END,
            @dblSplitRatio, GETDATE());
    SET @iActionID = SCOPE_IDENTITY();

    INSERT INTO tblCorporateActionTrades (iActionID, iShareTradeId, dblQuantityBefore, dblPriceBefore, dblCostBasisBefore)
    SELECT @iActionID, iShareTradeId, iQuantity, dblPricePerUnit, dblCostBasisPerUnit
    FROM tblShareTrades WITH (UPDLOCK, HOLDLOCK)
    WHERE iUserId = @iUserId
      AND strStockExchangeCode = @strStockExchangeCode
      AND strStockCode = @strStockCode
      AND dtmDateOfTransaction < @dtmSplitDate;
    SET @iTrades = @@ROWCOUNT;

    UPDATE T
    SET iQuantity = T.iQuantity * @New / @Old,
        dblPricePerUnit = T.dblPricePerUnit * @Old / @New,
        dblCostBasisPerUnit = T.dblCostBasisPerUnit * @Old / @New
    FROM tblShareTrades T
    JOIN tblCorporateActionTrades L ON L.iShareTradeId = T.iShareTradeId AND L.iActionID = @iActionID;

    UPDATE L
    SET dblQuantityAfter = T.iQuantity,
        dblPriceAfter = T.dblPricePerUnit,
        dblCostBasisAfter = T.dblCostBasisPerUnit
    FROM tblCorporateActionTrades L
    JOIN tblShareTrades T ON T.iShareTradeId = L.iShareTradeId
    WHERE L.iActionID = @iActionID;

    COMMIT TRANSACTION;

    SELECT 'Split Applied Successfully' AS Result, @iActionID AS iActionID, @iTrades AS iTradesAdjusted;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteStockSplit]
(
    @iUserId INT,
    @iActionID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Undoes a split or consolidation recorded by uspApplyStockSplit
--              and deletes it. A trade not changed since gets its noted values
--              back; one changed since (e.g. by a later split) is divided back
--              by the ratio. A split recorded before this migration has no
--              noted trades and is refused. Returns one row: Result
--              ('Split Deleted Successfully' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Ratio FLOAT;
    SELECT @Ratio = dblRatio
    FROM tblCorporateActions
    WHERE iActionID = @iActionID AND iUserID = @iUserId AND strActionType IN ('SPLIT', 'CONSOLIDATION');

    IF @Ratio IS NULL OR @Ratio <= 0
    BEGIN
        SELECT 'Error: That split was not found in this portfolio.' AS Result;
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM tblCorporateActionTrades WHERE iActionID = @iActionID)
    BEGIN
        SELECT 'Error: This split was recorded before the trades it changed were noted, so it can''t be undone automatically.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    UPDATE T
    SET iQuantity = CASE WHEN U.bUntouched = 1 THEN L.dblQuantityBefore ELSE ROUND(T.iQuantity / @Ratio, 6) END,
        dblPricePerUnit = CASE WHEN U.bUntouched = 1 THEN L.dblPriceBefore ELSE T.dblPricePerUnit * @Ratio END,
        dblCostBasisPerUnit = CASE WHEN U.bUntouched = 1 THEN L.dblCostBasisBefore ELSE T.dblCostBasisPerUnit * @Ratio END
    FROM tblShareTrades T
    JOIN tblCorporateActionTrades L ON L.iShareTradeId = T.iShareTradeId
    CROSS APPLY (SELECT CASE WHEN T.iQuantity = L.dblQuantityAfter
                               AND ISNULL(T.dblPricePerUnit, -1) = ISNULL(L.dblPriceAfter, -1)
                               AND ISNULL(T.dblCostBasisPerUnit, -1) = ISNULL(L.dblCostBasisAfter, -1)
                             THEN 1 ELSE 0 END AS bUntouched) U
    WHERE L.iActionID = @iActionID AND T.iUserId = @iUserId;

    -- Noted trades cascade with the action
    DELETE FROM tblCorporateActions WHERE iActionID = @iActionID AND iUserID = @iUserId;

    COMMIT TRANSACTION;

    SELECT 'Split Deleted Successfully' AS Result;
END
GO

-- =============================================
-- 4. Returns of capital
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAddReturnOfCapital]
(
    @iUserId INT,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode VARCHAR(50),
    @dtmPaymentDate DATE,
    @dblTotalAmount DECIMAL(19,4),
    @dblAmountPerUnit FLOAT = NULL, -- as entered, for reference
    @strComment VARCHAR(500) = NULL,
    @iTargetAccountID INT = NULL -- the "bank" side; NULL finds the bank account
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Records a return of capital paid on a holding. It reduces the
--              cost base of the parcels held at the end of the payment date
--              (worked out by the site's CGT matching); any excess over the
--              cost base is a capital gain (CGT event G1).
--              Hybrid / DoubleEntry portfolios get a journal:
--                Dr  Bank                       cash received
--                Cr  <CODE>.<EXCH> - Cost Base  cash received
--              Returns one row: Result ('Return Of Capital Added Successfully'
--              or 'Error: ...'), iReturnOfCapitalID.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @dblTotalAmount IS NULL OR @dblTotalAmount <= 0
    BEGIN
        SELECT 'Error: The amount must be more than zero.' AS Result, CAST(NULL AS INT) AS iReturnOfCapitalID;
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM tblShareTrades
                   WHERE iUserId = @iUserId AND strStockExchangeCode = @strStockExchangeCode
                     AND strStockCode = @strStockCode AND dtmDateOfTransaction <= @dtmPaymentDate)
    BEGIN
        SELECT 'Error: There are no trades in this holding on or before that date.' AS Result, CAST(NULL AS INT) AS iReturnOfCapitalID;
        RETURN;
    END

    DECLARE @iReturnOfCapitalID INT;

    BEGIN TRANSACTION;

    INSERT INTO tblReturnsOfCapital (iUserID, strStockExchangeCode, strStockCode, dtmPaymentDate, dblAmountPerUnit, dblTotalAmount, strComment)
    VALUES (@iUserId, @strStockExchangeCode, @strStockCode, @dtmPaymentDate, @dblAmountPerUnit, ROUND(@dblTotalAmount, 2), @strComment);
    SET @iReturnOfCapitalID = SCOPE_IDENTITY();

    DECLARE @strAccountingMode VARCHAR(20);
    SELECT TOP 1 @strAccountingMode = ISNULL(strAccountingMode, 'SingleEntry') FROM tblUsers WHERE iUserId = @iUserId;

    IF @strAccountingMode IN ('Hybrid', 'DoubleEntry')
    BEGIN
        DECLARE @Amount DECIMAL(19,4) = ROUND(@dblTotalAmount, 2);
        DECLARE @iBankAccountID INT, @iStockAccountID INT, @iParentAccountID INT, @iJournalID INT;
        DECLARE @StockAccountName VARCHAR(255) = @strStockCode + '.' + @strStockExchangeCode + ' - Cost Base';
        DECLARE @strCurrency VARCHAR(50);

        -- A. Bank: as uspPostPayoutJournal finds it
        IF @iTargetAccountID > 0
            SELECT @iBankAccountID = iAccountID FROM tblChartOfAccounts
            WHERE iAccountID = @iTargetAccountID AND (iUserID = @iUserId OR iUserID = 0);
        ELSE
            SELECT TOP 1 @iBankAccountID = iAccountID FROM tblChartOfAccounts
            WHERE (iUserID = @iUserId OR iUserID = 0) AND iAccountNumber >= 1100 AND iAccountNumber < 1200
            ORDER BY iUserID DESC, iAccountID;

        IF @iBankAccountID IS NULL AND ISNULL(@iTargetAccountID, 0) <= 0
            EXEC dbo.uspGetLedgerAccount @iUserId, 1100, 'Cash at Bank', 'Asset', @iBankAccountID OUTPUT;
        IF @iBankAccountID IS NULL
            THROW 50001, 'The target account for this return of capital does not belong to the portfolio.', 1;

        -- B. The stock's cost base account, found or added as uspAddShareTrade does
        SELECT TOP 1 @iStockAccountID = iAccountID
        FROM tblChartOfAccounts
        WHERE strAccountName = @StockAccountName AND iUserID = @iUserId;

        IF @iStockAccountID IS NULL
        BEGIN
            SELECT TOP 1 @iParentAccountID = iAccountID
            FROM tblChartOfAccounts
            WHERE iAccountNumber = 1300 AND (iUserID = 0 OR iUserID = @iUserId);

            SELECT @strCurrency = MAX(strCurrency) FROM tblShareTrades
            WHERE iUserId = @iUserId AND strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode;

            INSERT INTO tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable)
            VALUES (@iUserId, @iParentAccountID, 1301, @StockAccountName, 'Asset', ISNULL(@strCurrency, 'AUD'), 1);
            SET @iStockAccountID = SCOPE_IDENTITY();
        END

        -- C. Journal
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
        VALUES (@iUserId, @dtmPaymentDate,
                ISNULL(NULLIF(@strComment, ''), 'Return of capital ' + @strStockCode),
                'ReturnOfCapital', @iReturnOfCapitalID, GETDATE());
        SET @iJournalID = SCOPE_IDENTITY();

        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iBankAccountID, @Amount, 0, 'Cash received'),
               (@iJournalID, @iStockAccountID, 0, @Amount, 'Return of capital');
    END

    COMMIT TRANSACTION;

    SELECT 'Return Of Capital Added Successfully' AS Result, @iReturnOfCapitalID AS iReturnOfCapitalID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteReturnOfCapital]
(
    @iUserId INT,
    @iReturnOfCapitalID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Deletes a return of capital and its journal (lines cascade).
--              Returns one row: Result ('Return Of Capital Deleted
--              Successfully' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblReturnsOfCapital WHERE iReturnOfCapitalID = @iReturnOfCapitalID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That return of capital was not found in this portfolio.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    DELETE FROM tblJournal
    WHERE iUserID = @iUserId AND strSourceModule = 'ReturnOfCapital' AND iSourceID = @iReturnOfCapitalID;

    DELETE FROM tblReturnsOfCapital
    WHERE iReturnOfCapitalID = @iReturnOfCapitalID AND iUserID = @iUserId;

    COMMIT TRANSACTION;

    SELECT 'Return Of Capital Deleted Successfully' AS Result;
END
GO

-- =============================================
-- 5. Holdings: returns of capital reduce the cost
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
-- Date:        15-Oct-2026
-- Description:	Returns of capital (tblReturnsOfCapital) come off the cost,
--              like sale proceeds. Otherwise unchanged.
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
        SUM(ISNULL(dblCapitalGain, 0)),
        SUM(ISNULL(dblTotalPayouts, 0)),
        SUM(ISNULL(dblTotalProfit, 0)),
        ''
    FROM #PortfolioCalc
    WHERE @bShowTotalRows = 1 AND (@bShowOpenPositionsOnly = 0 OR iQuantity <> 0);

    DROP TABLE #PortfolioCalc;
END
GO

-- =============================================
-- 6. Dividend reinvestment (DRP)
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAddDRPTransaction]
(
    -- Payout Details
    @iUserId INT,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode VARCHAR(50),
    @dtmTransactionDate DATE,
    @dblTotalPayout FLOAT, -- cash dividend (after any TFN withheld)
    @dblFrankedAmount FLOAT = NULL,
    @dblUnFrankedAmount FLOAT = NULL,
    @dblFrankingCredit FLOAT = NULL,
    -- Reinvestment Trade Details
    @iQuantity INT,
    @dblPricePerUnit FLOAT,
    -- Shared Details
    @strComment VARCHAR(500) = 'Dividend Reinvestment (DRP)',
    -- Optional
    @dblTFNWithholdingAmount FLOAT = 0,
    @strName VARCHAR(500) = NULL
)
AS
-- =============================================
-- Description: Atomically adds a Dividend AND its corresponding Reinvestment Buy Trade.
--              Does NOT create a bank transaction.
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description:	Posts journals in Hybrid / DoubleEntry portfolios: the payout
--              through uspPostPayoutJournal (Dr Bank, Cr Dividend Income,
--              franking credit and TFN lines) and the new units
--              (Dr <CODE>.<EXCH> - Cost Base, Cr the same Bank), so the bank
--              nets to any residual left with the registry. Fills in the
--              stock name and net amount, checks the figures, saves the
--              price like uspAddShareTrade, and returns one row: Result
--              ('DRP Added Successfully' or 'Error: ...'), iPayoutID, iShareTradeID.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF ISNULL(@iQuantity, 0) <= 0 OR ISNULL(@dblPricePerUnit, 0) <= 0 OR ISNULL(@dblTotalPayout, 0) <= 0
    BEGIN
        SELECT 'Error: The dividend, the units and the DRP price must all be more than zero.' AS Result,
               CAST(NULL AS INT) AS iPayoutID, CAST(NULL AS INT) AS iShareTradeID;
        RETURN;
    END

    IF @strName IS NULL OR @strName = ''
        SET @strName = COALESCE(
            (SELECT TOP 1 strName FROM tblShareTrades
             WHERE iUserId = @iUserId AND strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode AND strName <> ''
             ORDER BY dtmDateOfTransaction DESC, iShareTradeId DESC),
            (SELECT TOP 1 strStockName FROM tblShareDetails WHERE strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode),
            '');

    DECLARE @strCurrency VARCHAR(50) = ISNULL((SELECT TOP 1 strPortfolioCurrency FROM tblUsers WHERE iUserId = @iUserId), 'AUD');
    DECLARE @dblOrderTotal FLOAT = @iQuantity * @dblPricePerUnit;
    DECLARE @NewTradeID INT, @NewPayoutID INT;

    BEGIN TRANSACTION;

    -- 1. The reinvestment buy (no brokerage, no bank row)
    INSERT INTO [dbo].[tblShareTrades] (
        [iUserId], [strStockExchangeCode], [strStockCode], [strName], [strType],
        [dtmDateOfTransaction], [iQuantity], [dblPricePerUnit],
        [dblBrokerage], [strCurrency], [dblExchangeRate],
        [dblOrderTotal], [dblCostBasisPerUnit],
        [strComment], [iBankStatementId]
    )
    VALUES (
        @iUserId, @strStockExchangeCode, @strStockCode, @strName, 'Buy',
        @dtmTransactionDate, @iQuantity, @dblPricePerUnit,
        0, @strCurrency, 1.0,
        @dblOrderTotal, @dblPricePerUnit,
        ISNULL(@strComment, ''), NULL
    );
    SET @NewTradeID = SCOPE_IDENTITY();

    -- 2. The payout, linked to the trade
    INSERT INTO [dbo].[tblPayouts] (
        [iUserID], [strStockExchangeCode], [strStockCode], [dtmTransactionDate],
        [dblTotal], [dblNetAmount], [dblFrankedAmount], [dblUnFrankedAmount], [dblFrankingCredit],
        [dblInterestAmount], [dblTFNWithholdingAmount], [dblForeignIncomeTaxAmount], [dblOtherAmount], [dblTrustIncome],
        [strComment], [strPayoutConfirmed], [iReinvestmentTradeID]
    )
    VALUES (
        @iUserId, @strStockExchangeCode, @strStockCode, @dtmTransactionDate,
        @dblTotalPayout, @dblTotalPayout, ISNULL(@dblFrankedAmount, 0), ISNULL(@dblUnFrankedAmount, 0), ISNULL(@dblFrankingCredit, 0),
        0, ISNULL(@dblTFNWithholdingAmount, 0), 0, 0, 0,
        @strComment, 'Yes', @NewTradeID
    );
    SET @NewPayoutID = SCOPE_IDENTITY();

    -- 3. The DRP price as a share price, as uspAddShareTrade saves a trade price
    INSERT INTO [dbo].[tblSharePrice] ([strStockExchangeCode], [strStockCode], [dtmDetailsAsAtDate], [dblClosePrice], [iUserID])
    VALUES (@strStockExchangeCode, @strStockCode, @dtmTransactionDate, @dblPricePerUnit, @iUserId);

    IF @iUserId > 0
    BEGIN
        INSERT INTO [dbo].[tblSharePriceCache] ([strStockExchangeCode], [strStockCode], [dtmDetailsAsAtDate], [dblClosePrice], [iUserID])
        VALUES (@strStockExchangeCode, @strStockCode, @dtmTransactionDate, @dblPricePerUnit, @iUserId);

        DELETE FROM [dbo].[tblSharePriceCache]
        WHERE strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode
          AND iUserID = @iUserId AND dtmDetailsAsAtDate < @dtmTransactionDate;
    END

    -- 4. Double entry
    DECLARE @strAccountingMode VARCHAR(20);
    SELECT TOP 1 @strAccountingMode = ISNULL(strAccountingMode, 'SingleEntry') FROM tblUsers WHERE iUserId = @iUserId;

    IF @strAccountingMode IN ('Hybrid', 'DoubleEntry')
    BEGIN
        DECLARE @iBankAccountID INT, @iStockAccountID INT, @iParentAccountID INT, @iJournalID INT;
        DECLARE @StockAccountName VARCHAR(255) = @strStockCode + '.' + @strStockExchangeCode + ' - Cost Base';
        DECLARE @Reinvested DECIMAL(19,4) = ROUND(CAST(@dblOrderTotal AS DECIMAL(19,4)), 2);

        -- A. Bank, as uspPostPayoutJournal finds it, so both journals use the same account
        SELECT TOP 1 @iBankAccountID = iAccountID FROM tblChartOfAccounts
        WHERE (iUserID = @iUserId OR iUserID = 0) AND iAccountNumber >= 1100 AND iAccountNumber < 1200
        ORDER BY iUserID DESC, iAccountID;
        IF @iBankAccountID IS NULL
            EXEC dbo.uspGetLedgerAccount @iUserId, 1100, 'Cash at Bank', 'Asset', @iBankAccountID OUTPUT;

        -- B. Payout journal: Dr Bank, Cr income (and franking / TFN lines)
        EXEC dbo.uspPostPayoutJournal @iUserId, @NewPayoutID, @iBankAccountID;

        -- C. The stock's cost base account, found or added as uspAddShareTrade does
        SELECT TOP 1 @iStockAccountID = iAccountID
        FROM tblChartOfAccounts
        WHERE strAccountName = @StockAccountName AND iUserID = @iUserId;

        IF @iStockAccountID IS NULL
        BEGIN
            SELECT TOP 1 @iParentAccountID = iAccountID
            FROM tblChartOfAccounts
            WHERE iAccountNumber = 1300 AND (iUserID = 0 OR iUserID = @iUserId);

            INSERT INTO tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable)
            VALUES (@iUserId, @iParentAccountID, 1301, @StockAccountName, 'Asset', @strCurrency, 1);
            SET @iStockAccountID = SCOPE_IDENTITY();
        END

        -- D. Trade journal: Dr Cost Base, Cr Bank (the dividend paid for the units)
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
        VALUES (@iUserId, @dtmTransactionDate,
                'Buy ' + CAST(@iQuantity AS VARCHAR(20)) + ' units of ' + @strStockCode + ' (DRP)',
                'ShareTrade', @NewTradeID, GETDATE());
        SET @iJournalID = SCOPE_IDENTITY();

        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iStockAccountID, @Reinvested, 0, 'DRP units'),
               (@iJournalID, @iBankAccountID, 0, @Reinvested, 'Dividend reinvested');
    END

    COMMIT TRANSACTION;

    SELECT 'DRP Added Successfully' AS Result, @NewPayoutID AS iPayoutID, @NewTradeID AS iShareTradeID;
END
GO

-- =============================================
-- 7. uspDeletePayout: a DRP payout takes its reinvestment trade with it
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspDeletePayout]
(
	@iUserId Integer,
	@iPayoutID Integer
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        22-Apr-2021
-- Version:     1.001
-- Description:	Deletes a payout (Dividend, Interest etc.)
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.002
-- Description:	Deletes the payout's journal too (lines cascade)
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.003
-- Description:	A DRP payout also deletes its reinvestment trade and that
--              trade's journal, unless CGT parcels use the trade. Returns
--              one row: Result ('Payout Deleted Sucessfully' or 'Error: ...').
-- =============================================
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	DECLARE @iTradeID INT;
	SELECT @iTradeID = iReinvestmentTradeID
	FROM tblPayouts
	WHERE iUserID = @iUserId AND iPayoutID = @iPayoutID;

	IF @iTradeID IS NOT NULL
	   AND EXISTS (SELECT 1 FROM tblCGTParcelMatching WHERE iBuyTradeID = @iTradeID OR iSellTradeID = @iTradeID)
	BEGIN
		SELECT 'Error: This payout''s reinvested units are matched to CGT parcels, so it can''t be deleted until those matches are recalculated without them.' AS Result;
		RETURN;
	END

	BEGIN TRANSACTION;

	IF EXISTS (SELECT 1 FROM tblPayouts WHERE iUserID = @iUserId AND iPayoutID = @iPayoutID)
		DELETE FROM tblJournal
		WHERE iUserID = @iUserId AND strSourceModule = 'Payout' AND iSourceID = @iPayoutID;

	DELETE FROM [dbo].[tblPayouts]
	WHERE iUserID = @iUserId
	AND iPayoutID = @iPayoutID;

	-- The reinvestment trade, now nothing points at it (it has no bank row)
	IF @iTradeID IS NOT NULL
	   AND NOT EXISTS (SELECT 1 FROM tblPayouts WHERE iReinvestmentTradeID = @iTradeID)
	BEGIN
		DELETE FROM tblJournal
		WHERE iUserID = @iUserId AND strSourceModule = 'ShareTrade' AND iSourceID = @iTradeID;

		DELETE FROM tblShareTradeGroups WHERE iShareTradeID = @iTradeID;

		DELETE FROM tblShareTrades WHERE iUserId = @iUserId AND iShareTradeId = @iTradeID;
	END

	COMMIT TRANSACTION;

	SELECT 'Payout Deleted Sucessfully' AS Result;
END
GO

SET NOEXEC OFF
GO
