-- =============================================
-- Migration: employee share schemes and options
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Income > Employee share
-- schemes and Corporate actions > Exercise options. Run it after 2026-10-21-09-complex-corporate-actions.sql.
-- Safe to run more than once. It only adds things.
--
-- What it adds:
--   * tblEssStatements: the employer's ESS statement for each year (the discounts that go at item 12 of an individual
--     return, and TFN amounts withheld), with uspSaveEssStatement and uspDeleteEssStatement (both refuse a locked year).
--   * uspExerciseOptions: exercising options or warrants. Through uspAddShareTrade, the options are sold for their cost
--     base (so nothing is gained or lost on them) and the shares are bought for the exercise price plus that cost base.
--     It's recorded in tblParcelTransfers, so the capital gains figures make no event of the options' sale and the
--     shares are acquired on the exercise date; deleting it there removes both trades.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblParcelTransfers', 'U') IS NULL OR OBJECT_ID('dbo.fnLockedTo', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-09-complex-corporate-actions.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblEssStatements', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblEssStatements (
        iEssStatementID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblEssStatements PRIMARY KEY,
        iUserID                INT NOT NULL,
        iFinancialYear         INT NOT NULL,             -- the year it ends, e.g. 2026 for 2025-26
        strEmployer            NVARCHAR(200) NOT NULL,
        dblUpfrontEligible     FLOAT NOT NULL DEFAULT (0),   -- 12D: taxed upfront, eligible for the reduction
        dblUpfrontNotEligible  FLOAT NOT NULL DEFAULT (0),   -- 12E: taxed upfront, not eligible
        dblDeferral            FLOAT NOT NULL DEFAULT (0),   -- 12F: deferral schemes
        dblPre2009             FLOAT NOT NULL DEFAULT (0),   -- 12G: acquired before 1 July 2009, cessation time this year
        dblForeignSource       FLOAT NOT NULL DEFAULT (0),   -- 12A: foreign source discounts (part of the above)
        dblTfnWithheld         FLOAT NOT NULL DEFAULT (0),   -- 12C
        strComment             NVARCHAR(500) NULL,
        dtmSaved               DATETIME NOT NULL CONSTRAINT DF_tblEssStatements_Saved DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblEssStatements_User ON dbo.tblEssStatements (iUserID, iFinancialYear);
END
GO

-- Adds (@iEssStatementID 0) or changes a statement. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveEssStatement
    @iUserID               INT,
    @iEssStatementID       INT = 0,
    @iFinancialYear        INT,
    @dtmYearEnd            DATE,
    @strEmployer           NVARCHAR(200),
    @dblUpfrontEligible    FLOAT = 0,
    @dblUpfrontNotEligible FLOAT = 0,
    @dblDeferral           FLOAT = 0,
    @dblPre2009            FLOAT = 0,
    @dblForeignSource      FLOAT = 0,
    @dblTfnWithheld        FLOAT = 0,
    @strComment            NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF ISNULL(@iEssStatementID, 0) > 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.tblEssStatements WHERE iEssStatementID = @iEssStatementID AND iUserID = @iUserID)
        BEGIN
            SELECT 'Error: That statement isn''t in this portfolio.' AS Result, NULL AS Id;
            RETURN;
        END
    END
    IF @Locked IS NOT NULL AND @dtmYearEnd <= @Locked
    BEGIN
        SELECT 'Error: That year is locked.' AS Result, NULL AS Id;
        RETURN;
    END
    IF LTRIM(RTRIM(ISNULL(@strEmployer, ''))) = ''
    BEGIN
        SELECT 'Error: Enter the employer.' AS Result, NULL AS Id;
        RETURN;
    END
    IF @dblUpfrontEligible < 0 OR @dblUpfrontNotEligible < 0 OR @dblDeferral < 0 OR @dblPre2009 < 0 OR @dblForeignSource < 0 OR @dblTfnWithheld < 0
    BEGIN
        SELECT 'Error: Amounts can''t be negative.' AS Result, NULL AS Id;
        RETURN;
    END

    IF ISNULL(@iEssStatementID, 0) > 0
    BEGIN
        IF @Locked IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.tblEssStatements WHERE iEssStatementID = @iEssStatementID
                                             AND DATEFROMPARTS(iFinancialYear, 6, 30) <= @Locked)
        BEGIN
            SELECT 'Error: That statement''s year is locked.' AS Result, NULL AS Id;
            RETURN;
        END
        UPDATE dbo.tblEssStatements SET iFinancialYear = @iFinancialYear, strEmployer = LTRIM(RTRIM(@strEmployer)),
            dblUpfrontEligible = @dblUpfrontEligible, dblUpfrontNotEligible = @dblUpfrontNotEligible, dblDeferral = @dblDeferral,
            dblPre2009 = @dblPre2009, dblForeignSource = @dblForeignSource, dblTfnWithheld = @dblTfnWithheld,
            strComment = @strComment, dtmSaved = GETDATE()
        WHERE iEssStatementID = @iEssStatementID AND iUserID = @iUserID;
        SELECT 'Success' AS Result, @iEssStatementID AS Id;
        RETURN;
    END
    INSERT dbo.tblEssStatements (iUserID, iFinancialYear, strEmployer, dblUpfrontEligible, dblUpfrontNotEligible, dblDeferral, dblPre2009,
        dblForeignSource, dblTfnWithheld, strComment)
    VALUES (@iUserID, @iFinancialYear, LTRIM(RTRIM(@strEmployer)), @dblUpfrontEligible, @dblUpfrontNotEligible, @dblDeferral, @dblPre2009,
        @dblForeignSource, @dblTfnWithheld, @strComment);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteEssStatement
    @iUserID         INT,
    @iEssStatementID INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @Year INT = (SELECT iFinancialYear FROM dbo.tblEssStatements WHERE iEssStatementID = @iEssStatementID AND iUserID = @iUserID);
    IF @Year IS NULL
        SELECT 'Error: That statement isn''t in this portfolio.' AS Result;
    ELSE IF @Locked IS NOT NULL AND DATEFROMPARTS(@Year, 6, 30) <= @Locked
        SELECT 'Error: That statement''s year is locked.' AS Result;
    ELSE
    BEGIN
        DELETE dbo.tblEssStatements WHERE iEssStatementID = @iEssStatementID AND iUserID = @iUserID;
        SELECT 'Success' AS Result;
    END
END
GO

-- =============================================
-- Exercise @iOptions options (or warrants) @strCode for @iShares shares @strNewCode at @dblExercisePrice each.
-- @dblOptionCost is the options' cost base (worked out by the site from the parcels). Returns Result and Id.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspExerciseOptions
    @iUserID          INT,
    @strMarket        VARCHAR(50),
    @strCode          VARCHAR(50),
    @strNewMarket     VARCHAR(50),
    @strNewCode       VARCHAR(50),
    @strNewName       VARCHAR(500) = NULL,
    @dtmDate          DATE,
    @iOptions         INT,
    @iShares          INT,
    @dblExercisePrice FLOAT,
    @dblOptionCost    FLOAT,
    @strComment       NVARCHAR(500) = NULL
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
        WHEN ISNULL(@Held, 0) <= 0 THEN 'No ' + @strCode + ' options were held on that date.'
        WHEN @iOptions IS NULL OR @iOptions <= 0 OR @iOptions > @Held + 0.0001 THEN 'The options exercised must be more than nil and no more than were held.'
        WHEN @Locked IS NOT NULL AND @dtmDate <= @Locked THEN 'That date is in a locked period.'
        WHEN ISNULL(@strNewCode, '') = '' OR (@strNewCode = @strCode AND @strNewMarket = @strMarket) THEN 'Enter the code of the shares received.'
        WHEN @iShares IS NULL OR @iShares <= 0 THEN 'Enter the number of shares received.'
        WHEN @dblExercisePrice IS NULL OR @dblExercisePrice < 0 OR @dblOptionCost IS NULL OR @dblOptionCost < 0 THEN 'Check the exercise price.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, NULL AS Id;
        RETURN;
    END

    DECLARE @Out TABLE (Result VARCHAR(500));
    DECLARE @SellPrice FLOAT = @dblOptionCost / @iOptions;
    DECLARE @BuyPrice FLOAT = (@dblExercisePrice * @iShares + @dblOptionCost) / @iShares;
    DECLARE @SellID INT, @BuyID INT, @TransferID INT;
    DECLARE @SellNote VARCHAR(500) = LEFT('Exercised for ' + @strNewCode + ISNULL(': ' + @strComment, ''), 500);
    DECLARE @BuyNote VARCHAR(500) = LEFT('Exercise of ' + @strCode + ' at ' + CONVERT(VARCHAR(30), CAST(@dblExercisePrice AS DECIMAL(19,4))) + ISNULL(': ' + @strComment, ''), 500);

    BEGIN TRANSACTION;
    INSERT INTO @Out EXEC dbo.uspAddShareTrade @iUserID, @strMarket, @strCode, NULL, 'Sell', @dtmDate, @iOptions, @SellPrice, 0, @Currency, 1, @SellNote, NULL;
    SELECT TOP 1 @SellID = iShareTradeId FROM dbo.tblShareTrades
    WHERE iUserId = @iUserID AND strStockExchangeCode = @strMarket AND strStockCode = @strCode ORDER BY iShareTradeId DESC;
    INSERT INTO @Out EXEC dbo.uspAddShareTrade @iUserID, @strNewMarket, @strNewCode, @strNewName, 'Buy', @dtmDate, @iShares, @BuyPrice, 0, @Currency, 1, @BuyNote, NULL;
    SELECT TOP 1 @BuyID = iShareTradeId FROM dbo.tblShareTrades
    WHERE iUserId = @iUserID AND strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode ORDER BY iShareTradeId DESC;
    -- Neither price is a market price, so they aren't kept as prices
    DELETE dbo.tblSharePrice WHERE iUserID = @iUserID AND dtmDetailsAsAtDate = @dtmDate
       AND ((strStockExchangeCode = @strMarket AND strStockCode = @strCode) OR (strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode));
    DELETE dbo.tblSharePriceCache WHERE iUserID = @iUserID AND dtmDetailsAsAtDate = @dtmDate
       AND ((strStockExchangeCode = @strMarket AND strStockCode = @strCode) OR (strStockExchangeCode = @strNewMarket AND strStockCode = @strNewCode));
    INSERT dbo.tblParcelTransfers (iUserID, strKind, dtmDate, strFromExchangeCode, strFromCode, strToExchangeCode, strToCode,
        dblNewPerOld, dblCostShare, dblScripValue, iBuyTradeID, iSellTradeID, strComment)
    VALUES (@iUserID, 'OptionExercise', @dtmDate, @strMarket, @strCode, @strNewMarket, @strNewCode,
        CAST(@iShares AS FLOAT) / @iOptions, 1, @dblExercisePrice * @iShares, @BuyID, @SellID, @strComment);
    SET @TransferID = SCOPE_IDENTITY();
    COMMIT;
    SELECT 'Success' AS Result, @TransferID AS Id;
END
GO

PRINT 'Employee share schemes and options migration done.';
GO
