-- =============================================
-- Migration: complex corporate actions
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Corporate actions > Demergers, takeovers and code changes. Run it after 2026-10-15-lock-periods.sql,
-- 2026-10-21-08-alerts.sql and 2026-10-21-07-fund-tax-statements.sql. Safe to run more than once. It only adds things.
--
-- What it adds:
--   * tblParcelTransfers: demergers, takeovers and code changes. For a demerger or a takeover with scrip-for-scrip
--     rollover, the capital gains figures move the cost base share to the new shares, which keep the original
--     purchase dates (for the CGT discount); a takeover's sale then only counts the cash for CGT.
--   * uspApplyDemerger: adds the new company's shares (a purchase at the cost base moved, through uspAddShareTrade)
--     and, for double-entry portfolios, moves that cost from the old company's cost base account.
--   * uspApplyTakeover: sells the old shares at the offer's value and buys the new ones at their value, both through
--     uspAddShareTrade, so the cash part is left in Trade Clearing for the bank line to settle.
--   * uspChangeStockCode: renames a holding (trades, payouts, corporate actions, statements, alerts, look-through
--     and its cost base account).
--   * uspGetParcelTransfers, uspDeleteParcelTransfer (removes a demerger's or takeover's trades and journal).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnLockedTo', 'FN') IS NULL OR OBJECT_ID('dbo.tblFundTaxStatements', 'U') IS NULL OR OBJECT_ID('dbo.tblPriceAlerts', 'U') IS NULL
    OR OBJECT_ID('dbo.tblFundExposure', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-lock-periods.sql, 2026-10-21-08-alerts.sql, 2026-10-21-06-fund-look-through.sql and 2026-10-21-07-fund-tax-statements.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblParcelTransfers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblParcelTransfers (
        iParcelTransferID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblParcelTransfers PRIMARY KEY,
        iUserID               INT NOT NULL,
        strKind               VARCHAR(20) NOT NULL,      -- Demerger, Takeover (rollover), TakeoverTaxed, CodeChange
        dtmDate               DATE NOT NULL,
        strFromExchangeCode   VARCHAR(50) NOT NULL,
        strFromCode           VARCHAR(50) NOT NULL,
        strToExchangeCode     VARCHAR(50) NOT NULL,
        strToCode             VARCHAR(50) NOT NULL,
        dblNewPerOld          FLOAT NOT NULL DEFAULT (0),   -- new shares for each old one
        dblCostShare          FLOAT NOT NULL DEFAULT (0),   -- share of the old cost base that moves to the new shares
        dblScripValue         FLOAT NOT NULL DEFAULT (0),   -- takeover: market value of the new shares received
        iBuyTradeID           INT NULL,
        iSellTradeID          INT NULL,
        strComment            NVARCHAR(500) NULL,
        dtmCreated            DATETIME NOT NULL CONSTRAINT DF_tblParcelTransfers_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblParcelTransfers_User ON dbo.tblParcelTransfers (iUserID);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspGetParcelTransfers
    @iUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT iParcelTransferID AS Id, strKind AS Kind, CAST(dtmDate AS datetime) AS Date, strFromExchangeCode AS FromExchangeCode,
           strFromCode AS FromCode, strToExchangeCode AS ToExchangeCode, strToCode AS ToCode, dblNewPerOld AS NewPerOld,
           dblCostShare AS CostShare, dblScripValue AS ScripValue, iBuyTradeID AS BuyTradeId, iSellTradeID AS SellTradeId, strComment AS Comment
    FROM dbo.tblParcelTransfers
    WHERE iUserID = @iUserID
    ORDER BY dtmDate, iParcelTransferID;
END
GO

-- =============================================
-- Demerger: @iNewUnits of the new company for the units held on @dtmDate; @dblCostShare (0 to 1) of the old cost
-- base moves to them (the ATO's apportionment); @dblCostMoved is that amount in dollars (worked out by the site from
-- the parcels), used for the new purchase and the ledger. Returns Result and Id.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspApplyDemerger
    @iUserID         INT,
    @strMarket       VARCHAR(50),
    @strCode         VARCHAR(50),
    @strNewMarket    VARCHAR(50),
    @strNewCode      VARCHAR(50),
    @strNewName      VARCHAR(500) = NULL,
    @dtmDate         DATE,
    @iNewUnits       INT,
    @dblNewPerOld    FLOAT,
    @dblCostShare    FLOAT,
    @dblCostMoved    FLOAT,
    @strComment      NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SELECT @strMarket = UPPER(LTRIM(RTRIM(@strMarket))), @strCode = UPPER(LTRIM(RTRIM(@strCode))),
           @strNewMarket = UPPER(LTRIM(RTRIM(@strNewMarket))), @strNewCode = UPPER(LTRIM(RTRIM(@strNewCode)));
    DECLARE @Held FLOAT = (SELECT SUM(CASE WHEN UPPER(strType) = 'BUY' THEN ABS(iQuantity) WHEN UPPER(strType) = 'SELL' THEN -ABS(iQuantity) ELSE 0 END)
                           FROM dbo.tblShareTrades WHERE iUserId = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode
                             AND dtmDateOfTransaction <= @dtmDate);
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @Currency VARCHAR(50) = ISNULL((SELECT TOP 1 strPortfolioCurrency FROM dbo.tblUsers WHERE iUserId = @iUserID), 'AUD');
    DECLARE @Error VARCHAR(300) = CASE
        WHEN ISNULL(@Held, 0) <= 0 THEN 'No ' + @strCode + ' shares were held on that date.'
        WHEN @Locked IS NOT NULL AND @dtmDate <= @Locked THEN 'That date is in a locked period.'
        WHEN ISNULL(@strNewCode, '') = '' OR (@strNewCode = @strCode AND @strNewMarket = @strMarket) THEN 'Enter the new company''s code.'
        WHEN @iNewUnits IS NULL OR @iNewUnits <= 0 THEN 'Enter the number of new shares received.'
        WHEN @dblCostShare IS NULL OR @dblCostShare <= 0 OR @dblCostShare >= 1 THEN 'The cost base share of the new company must be between 0% and 100%.'
        WHEN @dblCostMoved IS NULL OR @dblCostMoved < 0 THEN 'The cost base moved can''t be negative.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, NULL AS Id;
        RETURN;
    END

    DECLARE @Out TABLE (Result VARCHAR(500));
    DECLARE @Note VARCHAR(500) = LEFT('Demerger from ' + @strCode + ISNULL(': ' + @strComment, ''), 500);
    DECLARE @Price FLOAT = @dblCostMoved / @iNewUnits;
    DECLARE @BuyID INT, @TransferID INT;

    BEGIN TRANSACTION;
    INSERT INTO @Out EXEC dbo.uspAddShareTrade @iUserID, @strNewMarket, @strNewCode, @strNewName, 'Buy', @dtmDate, @iNewUnits, @Price, 0,
        @Currency, 1, @Note, NULL;
    SELECT TOP 1 @BuyID = iShareTradeId FROM dbo.tblShareTrades
    WHERE iUserId = @iUserID AND strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode ORDER BY iShareTradeId DESC;
    -- The purchase price is the cost base moved, not a market price, so it isn't kept as one
    DELETE dbo.tblSharePrice WHERE iUserID = @iUserID AND strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode AND dtmDetailsAsAtDate = @dtmDate;
    DELETE dbo.tblSharePriceCache WHERE iUserID = @iUserID AND strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode AND dtmDetailsAsAtDate = @dtmDate;

    INSERT dbo.tblParcelTransfers (iUserID, strKind, dtmDate, strFromExchangeCode, strFromCode, strToExchangeCode, strToCode,
        dblNewPerOld, dblCostShare, iBuyTradeID, strComment)
    VALUES (@iUserID, 'Demerger', @dtmDate, @strMarket, @strCode, @strNewMarket, @strNewCode, @dblNewPerOld, @dblCostShare, @BuyID, @strComment);
    SET @TransferID = SCOPE_IDENTITY();

    -- Ledger: the purchase posted Dr new cost base / Cr Trade Clearing; take the same cost out of the old cost base
    IF (SELECT TOP 1 ISNULL(strAccountingMode, 'SingleEntry') FROM dbo.tblUsers WHERE iUserId = @iUserID) IN ('Hybrid', 'DoubleEntry') AND @dblCostMoved > 0
    BEGIN
        DECLARE @Clearing INT = (SELECT TOP 1 iAccountID FROM dbo.tblChartOfAccounts WHERE strAccountName = 'Trade Clearing / Suspense'
                                  AND (iUserID = 0 OR iUserID = @iUserID) ORDER BY iUserID DESC);
        DECLARE @OldAccount INT = (SELECT TOP 1 iAccountID FROM dbo.tblChartOfAccounts
                                   WHERE strAccountName = @strCode + '.' + @strMarket + ' - Cost Base' AND iUserID = @iUserID);
        IF @Clearing IS NOT NULL AND @OldAccount IS NOT NULL
        BEGIN
            DECLARE @JournalID INT;
            INSERT dbo.tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
            VALUES (@iUserID, @dtmDate, LEFT('Demerger of ' + @strNewCode + ' from ' + @strCode + ': cost base moved', 500), 'CorporateAction', @TransferID);
            SET @JournalID = SCOPE_IDENTITY();
            INSERT dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit) VALUES (@JournalID, @Clearing, @dblCostMoved, 0);
            INSERT dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit) VALUES (@JournalID, @OldAccount, 0, @dblCostMoved);
        END
    END
    COMMIT;
    SELECT 'Success' AS Result, @TransferID AS Id;
END
GO

-- =============================================
-- Takeover or merger: all @iUnitsSold old shares go on @dtmDate for @dblConsideration (cash plus the new shares' market
-- value @dblScripValue). With @bRollover, the CGT figures move @dblCostShare (normally scrip value / consideration) of the
-- old cost base to the new shares and count only the cash as the sale's proceeds. Returns Result and Id.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspApplyTakeover
    @iUserID          INT,
    @strMarket        VARCHAR(50),
    @strCode          VARCHAR(50),
    @strNewMarket     VARCHAR(50) = NULL,
    @strNewCode       VARCHAR(50) = NULL,
    @strNewName       VARCHAR(500) = NULL,
    @dtmDate          DATE,
    @iUnitsSold       INT,
    @dblConsideration FLOAT,
    @iNewUnits        INT = 0,
    @dblScripValue    FLOAT = 0,
    @bRollover        BIT = 0,
    @dblCostShare     FLOAT = 0,
    @strComment       NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SELECT @strMarket = UPPER(LTRIM(RTRIM(@strMarket))), @strCode = UPPER(LTRIM(RTRIM(@strCode))),
           @strNewMarket = UPPER(LTRIM(RTRIM(ISNULL(@strNewMarket, '')))), @strNewCode = UPPER(LTRIM(RTRIM(ISNULL(@strNewCode, ''))));
    DECLARE @Held FLOAT = (SELECT SUM(CASE WHEN UPPER(strType) = 'BUY' THEN ABS(iQuantity) WHEN UPPER(strType) = 'SELL' THEN -ABS(iQuantity) ELSE 0 END)
                           FROM dbo.tblShareTrades WHERE iUserId = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode
                             AND dtmDateOfTransaction <= @dtmDate);
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @Currency VARCHAR(50) = ISNULL((SELECT TOP 1 strPortfolioCurrency FROM dbo.tblUsers WHERE iUserId = @iUserID), 'AUD');
    DECLARE @Error VARCHAR(300) = CASE
        WHEN ISNULL(@Held, 0) <= 0 THEN 'No ' + @strCode + ' shares were held on that date.'
        WHEN @iUnitsSold IS NULL OR @iUnitsSold <= 0 OR @iUnitsSold > @Held + 0.0001 THEN 'The units given up must be more than nil and no more than were held.'
        WHEN @Locked IS NOT NULL AND @dtmDate <= @Locked THEN 'That date is in a locked period.'
        WHEN @dblConsideration IS NULL OR @dblConsideration < 0 OR @dblScripValue < 0 OR @dblScripValue > @dblConsideration + 0.005 THEN 'Check the offer''s value and the new shares'' value.'
        WHEN ISNULL(@iNewUnits, 0) > 0 AND (@strNewCode = '' OR (@strNewCode = @strCode AND @strNewMarket = @strMarket)) THEN 'Enter the new company''s code.'
        WHEN ISNULL(@iNewUnits, 0) <= 0 AND @dblScripValue > 0 THEN 'Enter the number of new shares received.'
        WHEN @bRollover = 1 AND (ISNULL(@iNewUnits, 0) <= 0 OR @dblCostShare <= 0 OR @dblCostShare > 1) THEN 'Rollover needs new shares and a cost base share above 0%.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, NULL AS Id;
        RETURN;
    END

    DECLARE @Out TABLE (Result VARCHAR(500));
    DECLARE @Note VARCHAR(500) = LEFT('Takeover' + CASE WHEN @iNewUnits > 0 THEN ' by ' + @strNewCode ELSE '' END + ISNULL(': ' + @strComment, ''), 500);
    DECLARE @SellPrice FLOAT = @dblConsideration / @iUnitsSold;
    DECLARE @SellID INT, @BuyID INT = NULL, @TransferID INT;

    BEGIN TRANSACTION;
    INSERT INTO @Out EXEC dbo.uspAddShareTrade @iUserID, @strMarket, @strCode, NULL, 'Sell', @dtmDate, @iUnitsSold, @SellPrice, 0, @Currency, 1, @Note, NULL;
    SELECT TOP 1 @SellID = iShareTradeId FROM dbo.tblShareTrades
    WHERE iUserId = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode ORDER BY iShareTradeId DESC;
    IF ISNULL(@iNewUnits, 0) > 0
    BEGIN
        DECLARE @BuyPrice FLOAT = @dblScripValue / @iNewUnits;
        DECLARE @BuyNote VARCHAR(500) = LEFT('Takeover of ' + @strCode + ISNULL(': ' + @strComment, ''), 500);
        INSERT INTO @Out EXEC dbo.uspAddShareTrade @iUserID, @strNewMarket, @strNewCode, @strNewName, 'Buy', @dtmDate, @iNewUnits, @BuyPrice, 0, @Currency, 1, @BuyNote, NULL;
        SELECT TOP 1 @BuyID = iShareTradeId FROM dbo.tblShareTrades
        WHERE iUserId = @iUserID AND strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode ORDER BY iShareTradeId DESC;
    END
    INSERT dbo.tblParcelTransfers (iUserID, strKind, dtmDate, strFromExchangeCode, strFromCode, strToExchangeCode, strToCode,
        dblNewPerOld, dblCostShare, dblScripValue, iBuyTradeID, iSellTradeID, strComment)
    VALUES (@iUserID, CASE WHEN @bRollover = 1 THEN 'Takeover' ELSE 'TakeoverTaxed' END, @dtmDate, @strMarket, @strCode, @strNewMarket, @strNewCode,
        CASE WHEN @iNewUnits > 0 THEN CAST(@iNewUnits AS FLOAT) / @iUnitsSold ELSE 0 END,
        CASE WHEN @bRollover = 1 THEN @dblCostShare ELSE 0 END, @dblScripValue, @BuyID, @SellID, @strComment);
    SET @TransferID = SCOPE_IDENTITY();
    COMMIT;
    SELECT 'Success' AS Result, @TransferID AS Id;
END
GO

-- =============================================
-- A holding changes its code or market (a name change, a re-listing). Everything recorded under the old code moves
-- to the new one. Returns Result and Id.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspChangeStockCode
    @iUserID      INT,
    @strMarket    VARCHAR(50),
    @strCode      VARCHAR(50),
    @strNewMarket VARCHAR(50),
    @strNewCode   VARCHAR(50),
    @strNewName   VARCHAR(500) = NULL,
    @dtmDate      DATE,
    @strComment   NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SELECT @strMarket = UPPER(LTRIM(RTRIM(@strMarket))), @strCode = UPPER(LTRIM(RTRIM(@strCode))),
           @strNewMarket = UPPER(LTRIM(RTRIM(@strNewMarket))), @strNewCode = UPPER(LTRIM(RTRIM(@strNewCode)));
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @Error VARCHAR(300) = CASE
        WHEN NOT EXISTS (SELECT 1 FROM dbo.tblShareTrades WHERE iUserId = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode)
            THEN 'There are no ' + @strCode + ' trades in this portfolio.'
        WHEN ISNULL(@strNewCode, '') = '' OR ISNULL(@strNewMarket, '') = '' OR (@strNewCode = @strCode AND @strNewMarket = @strMarket) THEN 'Enter the new code.'
        WHEN EXISTS (SELECT 1 FROM dbo.tblShareTrades WHERE iUserId = @iUserID AND strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode)
            THEN 'This portfolio already has ' + @strNewCode + ' trades, so the two can''t be merged this way.'
        WHEN @Locked IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.tblShareTrades WHERE iUserId = @iUserID AND strStockExchangeCode = @strMarket
                                              AND strStockCode = @strCode AND dtmDateOfTransaction <= @Locked)
            THEN 'Some ' + @strCode + ' trades are in a locked period, so the code can''t be changed.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, NULL AS Id;
        RETURN;
    END

    BEGIN TRANSACTION;
    UPDATE dbo.tblShareTrades SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode,
           strName = CASE WHEN ISNULL(@strNewName, '') = '' THEN strName ELSE @strNewName END
    WHERE iUserId = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblPayouts SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode
    WHERE iUserID = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblCorporateActions SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode
    WHERE iUserID = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblFundTaxStatements SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode
    WHERE iUserID = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblFundExposure SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode
    WHERE iUserID = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblPriceAlerts SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode
    WHERE iPortfolioID = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblParcelTransfers SET strFromExchangeCode = @strNewMarket, strFromCode = @strNewCode
    WHERE iUserID = @iUserID AND strFromExchangeCode = @strMarket AND strFromCode = @strCode;
    UPDATE dbo.tblParcelTransfers SET strToExchangeCode = @strNewMarket, strToCode = @strNewCode
    WHERE iUserID = @iUserID AND strToExchangeCode = @strMarket AND strToCode = @strCode;
    -- The portfolio's own prices (manual prices) follow too
    UPDATE dbo.tblSharePrice SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode
    WHERE iUserID = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblSharePriceCache SET strStockExchangeCode = @strNewMarket, strStockCode = @strNewCode
    WHERE iUserID = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode;
    UPDATE dbo.tblChartOfAccounts SET strAccountName = @strNewCode + '.' + @strNewMarket + ' - Cost Base'
    WHERE iUserID = @iUserID AND strAccountName = @strCode + '.' + @strMarket + ' - Cost Base'
      AND NOT EXISTS (SELECT 1 FROM dbo.tblChartOfAccounts WHERE iUserID = @iUserID AND strAccountName = @strNewCode + '.' + @strNewMarket + ' - Cost Base');

    INSERT dbo.tblParcelTransfers (iUserID, strKind, dtmDate, strFromExchangeCode, strFromCode, strToExchangeCode, strToCode, dblNewPerOld, strComment)
    VALUES (@iUserID, 'CodeChange', @dtmDate, @strMarket, @strCode, @strNewMarket, @strNewCode, 1, @strComment);
    COMMIT;
    SELECT 'Success' AS Result, SCOPE_IDENTITY() AS Id;
END
GO

-- Removes a demerger or takeover with the trades it added (through uspDeleteShareTrade) and its journal.
-- A code change is undone by changing the code back. Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspDeleteParcelTransfer
    @iUserID           INT,
    @iParcelTransferID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Kind VARCHAR(20), @Date DATE, @Buy INT, @Sell INT;
    SELECT @Kind = strKind, @Date = dtmDate, @Buy = iBuyTradeID, @Sell = iSellTradeID
    FROM dbo.tblParcelTransfers WHERE iParcelTransferID = @iParcelTransferID AND iUserID = @iUserID;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF @Kind IS NULL
    BEGIN
        SELECT 'Error: That corporate action isn''t in this portfolio.' AS Result;
        RETURN;
    END
    IF @Kind = 'CodeChange'
    BEGIN
        SELECT 'Error: To undo a code change, change the code back.' AS Result;
        RETURN;
    END
    IF @Locked IS NOT NULL AND @Date <= @Locked
    BEGIN
        SELECT 'Error: That date is in a locked period.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM dbo.tblShareTrades T JOIN dbo.tblParcelTransfers P ON P.iParcelTransferID = @iParcelTransferID
               WHERE T.iUserId = @iUserID AND T.strStockExchangeCode = P.strToExchangeCode AND T.strStockCode = P.strToCode
                 AND UPPER(T.strType) = 'SELL' AND T.dtmDateOfTransaction >= P.dtmDate AND T.iShareTradeId <> ISNULL(P.iSellTradeID, 0)
                 AND P.iBuyTradeID IS NOT NULL)
    BEGIN
        SELECT 'Error: Some of the new shares have been sold since. Delete those sales first.' AS Result;
        RETURN;
    END

    DECLARE @Out TABLE (Result VARCHAR(500));
    BEGIN TRANSACTION;
    DELETE dbo.tblJournal WHERE iUserID = @iUserID AND strSourceModule = 'CorporateAction' AND iSourceID = @iParcelTransferID;
    IF @Buy IS NOT NULL
        INSERT INTO @Out EXEC dbo.uspDeleteShareTrade @iUserID, @Buy;
    IF @Sell IS NOT NULL
        INSERT INTO @Out EXEC dbo.uspDeleteShareTrade @iUserID, @Sell;
    IF EXISTS (SELECT 1 FROM @Out WHERE Result LIKE 'Error%')
    BEGIN
        ROLLBACK;
        SELECT TOP 1 Result FROM @Out WHERE Result LIKE 'Error%';
        RETURN;
    END
    DELETE dbo.tblParcelTransfers WHERE iParcelTransferID = @iParcelTransferID AND iUserID = @iUserID;
    COMMIT;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Complex corporate actions migration done.';
GO
