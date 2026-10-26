-- =============================================
-- Migration: trading stock and investments in one portfolio, and trading stock in the ledger
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with these trading stock extras.
-- Run it after 2026-10-21-11-share-trader.sql and 2026-10-15-realised-gains.sql. Safe to run more than once.
--
--   * tblShareTraderHoldings: holdings a share trader keeps as investments (their gains stay capital gains). Every other
--     holding is trading stock from the trader's first year. uspGetShareTraderHoldings lists them and
--     uspSetShareTraderHolding adds or removes one (refusing a locked year).
--   * uspPostTradingStockJournal (re)posts a year's trading stock journal at the year end (source 'TradingStock'),
--     in Hybrid / DoubleEntry mode:
--       Dr 4500 Realised Gains on Investments    Cr 4550 Trading Profit on Shares       the year's gain on trading stock sold
--       Dr 1395 Trading Stock Valuation Adjustment  Cr 4550 Trading Profit on Shares    the change in closing stock over cost
--     (the other way round for a loss or a fall). Zero amounts remove the journal.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblShareTrader', 'U') IS NULL OR OBJECT_ID('dbo.uspGetLedgerAccount', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-11-share-trader.sql and 2026-10-15-ledger-fixes.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iUserID = 0 AND ((iAccountNumber = 1395 AND strAccountType <> 'Asset') OR (iAccountNumber = 4550 AND strAccountType <> 'Revenue')))
BEGIN
    RAISERROR('Stopped: account 1395 or 4550 is already used for another kind of account. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblShareTraderHoldings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblShareTraderHoldings (
        iUserID              INT NOT NULL,
        strStockExchangeCode VARCHAR(50) NOT NULL,
        strStockCode         VARCHAR(50) NOT NULL,
        dtmSaved             DATETIME NOT NULL CONSTRAINT DF_tblShareTraderHoldings_Saved DEFAULT (GETDATE()),
        CONSTRAINT PK_tblShareTraderHoldings PRIMARY KEY (iUserID, strStockExchangeCode, strStockCode)
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.uspGetShareTraderHoldings
    @iUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT strStockExchangeCode AS ExchangeCode, strStockCode AS StockCode FROM dbo.tblShareTraderHoldings WHERE iUserID = @iUserID;
END
GO

-- @bInvestment 1 keeps the holding as an investment; 0 makes it trading stock again. @dtmFirstYearEnd is the end of the
-- trader's first year (every trader year's figures change). Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspSetShareTraderHolding
    @iUserID              INT,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @bInvestment          BIT,
    @dtmFirstYearEnd      DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF @Locked IS NOT NULL AND @dtmFirstYearEnd IS NOT NULL AND @dtmFirstYearEnd <= @Locked
    BEGIN
        SELECT 'Error: That would change a locked year. Unlock it first.' AS Result;
        RETURN;
    END
    SET @strStockExchangeCode = UPPER(LTRIM(RTRIM(@strStockExchangeCode)));
    SET @strStockCode = UPPER(LTRIM(RTRIM(@strStockCode)));
    IF @bInvestment = 1
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.tblShareTraderHoldings WHERE iUserID = @iUserID AND strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode)
            INSERT dbo.tblShareTraderHoldings (iUserID, strStockExchangeCode, strStockCode) VALUES (@iUserID, @strStockExchangeCode, @strStockCode);
    END
    ELSE
        DELETE dbo.tblShareTraderHoldings WHERE iUserID = @iUserID AND strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspPostTradingStockJournal
    @iUserID        INT,
    @iYear          INT,              -- the financial year it ends
    @dtmYearEnd     DATE,
    @dblRealised    DECIMAL(19,4),    -- the year's gain (negative: loss) on trading stock sold, at cost
    @dblAdjustment  DECIMAL(19,4)     -- this year's change in (closing stock value less its cost)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        26-Oct-2026
-- Description: Replaces the year-end trading stock journal. Returns Result.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF @Locked IS NOT NULL AND @dtmYearEnd <= @Locked
    BEGIN
        SELECT 'Error: That year is locked.' AS Result;
        RETURN;
    END
    DECLARE @Mode VARCHAR(20);
    SELECT TOP 1 @Mode = ISNULL(strAccountingMode, 'SingleEntry') FROM tblUsers WHERE iUserId = @iUserID;
    IF ISNULL(@Mode, 'SingleEntry') NOT IN ('Hybrid', 'DoubleEntry')
    BEGIN
        SELECT 'Error: The ledger is only kept in Hybrid or double-entry mode.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    DECLARE @Old TABLE (iJournalID INT PRIMARY KEY);
    INSERT INTO @Old SELECT iJournalID FROM tblJournal WHERE iUserID = @iUserID AND strSourceModule = 'TradingStock' AND iSourceID = @iYear;
    DELETE L FROM tblJournalLines L JOIN @Old O ON O.iJournalID = L.iJournalID;
    DELETE J FROM tblJournal J JOIN @Old O ON O.iJournalID = J.iJournalID;

    SET @dblRealised = ROUND(ISNULL(@dblRealised, 0), 2);
    SET @dblAdjustment = ROUND(ISNULL(@dblAdjustment, 0), 2);
    IF @dblRealised <> 0 OR @dblAdjustment <> 0
    BEGIN
        DECLARE @Realised INT, @Trading INT, @Adjustment INT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 4500, 'Realised Gains on Investments', 'Revenue', @Realised OUTPUT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 4550, 'Trading Profit on Shares', 'Revenue', @Trading OUTPUT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 1395, 'Trading Stock Valuation Adjustment', 'Asset', @Adjustment OUTPUT;

        DECLARE @Years VARCHAR(9) = CAST(@iYear - 1 AS VARCHAR(4)) + '-' + RIGHT(CAST(@iYear AS VARCHAR(4)), 2);
        DECLARE @iJournalID INT;
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
        VALUES (@iUserID, @dtmYearEnd, 'Trading stock ' + @Years + ': trading profit and closing stock', 'TradingStock', @iYear);
        SET @iJournalID = SCOPE_IDENTITY();

        -- Debits positive, credits negative
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        SELECT @iJournalID, A, CASE WHEN Amt > 0 THEN Amt ELSE 0 END, CASE WHEN Amt < 0 THEN -Amt ELSE 0 END, D
        FROM (VALUES
            (1, @Realised, @dblRealised, 'Gains on trading stock sold, moved to trading profit'),
            (2, @Trading, -@dblRealised, 'Gains on trading stock sold'),
            (3, @Adjustment, @dblAdjustment, 'Change in closing stock value over cost'),
            (4, @Trading, -@dblAdjustment, 'Change in closing stock value over cost')
        ) V(N, A, Amt, D)
        WHERE Amt <> 0
        ORDER BY N;
    END
    COMMIT;
    SELECT 'Success' AS Result;
END
GO

SET NOEXEC OFF;
GO
