-- =============================================
-- Migration: crypto rewards
-- Date:      27-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with crypto staking rewards,
-- crypto swaps and the crypto exchange imports. Run it after 2026-10-26-01-part-units.sql. Safe to run more than once.
--
-- uspAddDRPTransaction takes a new optional @dblOtherAmount: income paid as new units that isn't a dividend (a crypto
-- staking reward, airdrop or mining reward). The units are bought at their market value and the value is the payout's
-- other amount, so the ledger posts it to Other Investment Income. It also refuses franking on crypto (market CRYPTO).
-- Nothing else in the proc changes, and existing callers work as before.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnUnitsText', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-26-01-part-units.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

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
    @iQuantity FLOAT,
    @dblPricePerUnit FLOAT,
    -- Shared Details
    @strComment VARCHAR(500) = 'Dividend Reinvestment (DRP)',
    -- Optional
    @dblTFNWithholdingAmount FLOAT = 0,
    @strName VARCHAR(500) = NULL,
    -- Income that isn't a dividend, such as a crypto staking reward (tblPayouts.dblOtherAmount)
    @dblOtherAmount FLOAT = 0
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
-- Date:        27-Oct-2026
-- Description:	@dblOtherAmount: income paid in new units that isn't a dividend (a crypto staking reward, airdrop
--              or mining reward), kept as the payout's other amount so it posts to Other Investment Income and
--              the tax return shows it as other income. Crypto (market CRYPTO) can't carry franking.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF UPPER(LTRIM(RTRIM(@strStockExchangeCode))) = 'CRYPTO' AND (ISNULL(@dblFrankedAmount, 0) <> 0 OR ISNULL(@dblFrankingCredit, 0) <> 0)
    BEGIN
        SELECT 'Error: Crypto doesn''t pay franked dividends, so it can''t have a franked amount or a franking credit.' AS Result,
               CAST(NULL AS INT) AS iPayoutID, CAST(NULL AS INT) AS iShareTradeID;
        RETURN;
    END

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
        0, ISNULL(@dblTFNWithholdingAmount, 0), 0, ISNULL(@dblOtherAmount, 0), 0,
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
                'Buy ' + dbo.fnUnitsText(@iQuantity) + ' units of ' + @strStockCode + CASE WHEN ISNULL(@dblOtherAmount, 0) > 0 THEN ' (reward)' ELSE ' (DRP)' END,
                'ShareTrade', @NewTradeID, GETDATE());
        SET @iJournalID = SCOPE_IDENTITY();

        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iStockAccountID, @Reinvested, 0, CASE WHEN ISNULL(@dblOtherAmount, 0) > 0 THEN 'Reward units' ELSE 'DRP units' END),
               (@iJournalID, @iBankAccountID, 0, @Reinvested, CASE WHEN ISNULL(@dblOtherAmount, 0) > 0 THEN 'Reward received as units' ELSE 'Dividend reinvested' END);
    END

    COMMIT TRANSACTION;

    SELECT 'DRP Added Successfully' AS Result, @NewPayoutID AS iPayoutID, @NewTradeID AS iShareTradeID;
END
GO

SET NOEXEC OFF
GO
