-- =============================================
-- Migration: foreign trades in the ledger
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build that posts foreign trades in
-- the portfolio's currency. Run it after 2026-10-15-lock-periods.sql and 2026-10-15-ledger-fixes.sql.
-- Safe to run more than once.
--
-- Before this, uspAddShareTrade posted a trade's journal in the trade's own currency (US dollars for a US share)
-- while the cash side was converted, so foreign trades left amounts in Trade Clearing and overstated cost bases.
--   * uspAddShareTrade now posts the order total divided by the exchange rate, to the cent.
--   * Existing trade journals with an exchange rate other than 1 are converted the same way, except in locked
--     periods. A sale's journal goes back to proceeds only; the site re-posts its realised gain from the converted
--     costs the next time the ledger is opened. Only journals still in the trade's currency are changed, so running
--     this again changes nothing.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnLockedTo', 'FN') IS NULL OR OBJECT_ID('dbo.uspPostSaleGain', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-lock-periods.sql and 2026-10-15-ledger-fixes.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddShareTrade] 
(
    @iUserId Integer,
    @strMarket VarChar(50),
    @strCode VarChar(50),
    @strName VarChar(500),
    @strType VarChar(50),
    @dtmDateOfTransaction Date,
    @iQuantity Integer,
    @dblPricePerUnit Float,
    @dblBrokerage Float,
    @strCurrency VarChar(50),
    @dblExchangeRate Float,
    @strComment VarChar(500),
    @iBankStatementId Integer
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        07-Dec-2019
-- Version:     1.001
-- Description:	Records a new share trade
-- =============================================
-- Author:		Gemini (Refactored 11-Nov-2025)
-- Date:        11-Nov-2025
-- Version:     1.002
-- Description:	Consolidated logic to remove EXEC dbo.uspAddSharePrice overhead.
-- =============================================
-- Author:		Tony Darcy & Gemini
-- Date:        5-Dec-2025
-- Version:     1.003
-- Description:	Added logic for double entry
-- =============================================
-- Date:        21-Oct-2026
-- Description:	The journal is in the portfolio's currency: the order total divided by the exchange rate,
--              to the cent, as AddShareTradeWithCashTransaction already does for the cash side.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    -- =========================================================================
    -- 1. PREPARATION & CALCULATIONS
    -- =========================================================================
    
    -- Handle Missing Names (Legacy Logic)
    IF @strName IS NULL OR @strName = ''
        SET @strName = COALESCE((SELECT TOP 1 strStockName FROM [dbo].[tblShareDetails] WHERE strStockExchangeCode = @strMarket AND strStockCode = @strCode), '');

    -- Sanitize Input
    IF @strMarket LIKE CHAR(239) + CHAR(187) + CHAR(191) + '%' SET @strMarket = REPLACE(@strMarket, CHAR(239) + CHAR(187) + CHAR(191), '');
    
    -- Calculate Financials
    DECLARE @dblOrderTotal FLOAT;
    DECLARE @dblCostBasisPerUnit FLOAT;
    DECLARE @dblTotalCapitalizedCost DECIMAL(19,4);

    -- Ensure Quantity polarity is correct for the logic (absolute for calculations)
    DECLARE @iAbsQuantity INT = ABS(@iQuantity);
    
    -- Legacy logic: Sharesight exports use negative quantity for sells
    IF @iQuantity < 0 AND UPPER(@strType) = 'BUY' SET @iQuantity = @iAbsQuantity; 
    
    IF UPPER(@strType) = 'BUY'
    BEGIN
        SET @dblOrderTotal = ((@dblPricePerUnit * @iAbsQuantity) + @dblBrokerage);
    END
    ELSE -- SELL
    BEGIN
        SET @dblOrderTotal = ((@dblPricePerUnit * @iAbsQuantity) - @dblBrokerage);
    END

    -- Calculate Cost Basis (Legacy Logic)
    SET @dblCostBasisPerUnit = (@dblOrderTotal / NULLIF(@iAbsQuantity,0)) / NULLIF(@dblExchangeRate,0);
    IF @dblCostBasisPerUnit IS NULL SET @dblCostBasisPerUnit = 0;

    -- =========================================================================
    -- 2. LEGACY INSERT (Maintains compatibility with current Dashboard/UI)
    -- =========================================================================
    
    INSERT INTO [dbo].[tblShareTrades] (
       [iUserId], [strStockExchangeCode], [strStockCode], [strName], [strType],
       [dtmDateOfTransaction], [iQuantity], [dblPricePerUnit], [dblBrokerage],
       [strCurrency], [dblExchangeRate], [dblOrderTotal], [dblCostBasisPerUnit],
       [strComment], [iBankStatementId]
    )
    VALUES (
       @iUserId, @strMarket, @strCode, @strName, @strType,
       @dtmDateOfTransaction, @iQuantity, @dblPricePerUnit, @dblBrokerage,
       @strCurrency, @dblExchangeRate, @dblOrderTotal, @dblCostBasisPerUnit,
       @strComment, @iBankStatementId
    );

    DECLARE @NewTradeID INT = SCOPE_IDENTITY();

    -- Update Share Prices (Legacy Logic)
    INSERT INTO [dbo].[tblSharePrice] (
        [strStockExchangeCode], [strStockCode], [dtmDetailsAsAtDate], [dblClosePrice], [iUserID]
    )
    VALUES (@strMarket, @strCode, @dtmDateOfTransaction, @dblPricePerUnit, @iUserId);

    IF @iUserId > 0
    BEGIN
        INSERT INTO [dbo].[tblSharePriceCache] (
            [strStockExchangeCode], [strStockCode], [dtmDetailsAsAtDate], [dblClosePrice], [iUserID]
        )
        VALUES (@strMarket, @strCode, @dtmDateOfTransaction, @dblPricePerUnit, @iUserId);
        
        -- Keep Cache clean
        DELETE FROM [dbo].[tblSharePriceCache]
        WHERE strStockExchangeCode = @strMarket AND strStockCode = @strCode 
          AND iUserID = @iUserId AND dtmDetailsAsAtDate < @dtmDateOfTransaction;
    END

    -- =========================================================================
    -- 3. DOUBLE ENTRY ACCOUNTING LOGIC (New Feature)
    -- =========================================================================

    DECLARE @strAccountingMode VARCHAR(20);
    SELECT TOP 1 @strAccountingMode = ISNULL(strAccountingMode, 'SingleEntry') 
    FROM tblUsers WHERE iUserId = @iUserId;

    -- Only proceed if user is migrated or testing
    IF @strAccountingMode IN ('Hybrid', 'DoubleEntry')
    BEGIN
        DECLARE @JournalID INT;
        DECLARE @ParentAssetAccountID INT;
        DECLARE @SpecificStockAccountID INT;
        DECLARE @ClearingAccountID INT;
        DECLARE @StockAccountName VARCHAR(255) = @strCode + '.' + @strMarket + ' - Cost Base';
        DECLARE @dblJournalTotal DECIMAL(19,4) = ROUND(@dblOrderTotal / ISNULL(NULLIF(@dblExchangeRate, 0), 1), 2);

        -- A. GET OR CREATE PARENT ASSET ACCOUNT (Investment Portfolio - 1300)
        SELECT TOP 1 @ParentAssetAccountID = iAccountID 
        FROM tblChartOfAccounts 
        WHERE iAccountNumber = 1300 AND (iUserID = 0 OR iUserID = @iUserId);

        -- B. GET OR CREATE CHILD STOCK ACCOUNT
        -- Check if this specific stock account exists for this user
        SELECT TOP 1 @SpecificStockAccountID = iAccountID 
        FROM tblChartOfAccounts 
        WHERE strAccountName = @StockAccountName AND iUserID = @iUserId;

        IF @SpecificStockAccountID IS NULL
        BEGIN
            -- Create the sub-account dynamically
            INSERT INTO tblChartOfAccounts (
                iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable
            )
            VALUES (
                @iUserId, @ParentAssetAccountID, 1301, -- 1301 is a placeholder number, logic could auto-increment this
                @StockAccountName, 'Asset', @strCurrency, 1
            );
            SET @SpecificStockAccountID = SCOPE_IDENTITY();
        END

        -- C. GET CLEARING ACCOUNT (Trade Payable/Receivable)
        -- We use a clearing account because the Cash settlement might happen in a separate procedure/date
        SELECT TOP 1 @ClearingAccountID = iAccountID 
        FROM tblChartOfAccounts 
        WHERE strAccountName = 'Trade Clearing / Suspense' AND (iUserID = 0 OR iUserID = @iUserId);

        -- If Clearing account missing, create a system default one (Liability/Asset neutral)
        IF @ClearingAccountID IS NULL
        BEGIN
            INSERT INTO tblChartOfAccounts (iUserID, iAccountNumber, strAccountName, strAccountType, bIsPostable)
            VALUES (0, 2900, 'Trade Clearing / Suspense', 'Liability', 1);
            SET @ClearingAccountID = SCOPE_IDENTITY();
        END

        -- D. CREATE JOURNAL HEADER
        INSERT INTO tblJournal (
            iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID
        )
        VALUES (
            @iUserId, @dtmDateOfTransaction, 
            @strType + ' ' + CAST(@iAbsQuantity AS VARCHAR) + ' units of ' + @strCode,
            'ShareTrade', @NewTradeID
        );
        SET @JournalID = SCOPE_IDENTITY();

        -- E. POST DEBITS AND CREDITS
        -- Note: We are capitalizing brokerage, so Expense = 0, Asset increases by full Order Total.
        
        IF UPPER(@strType) = 'BUY'
        BEGIN
            -- BUY: Debit Asset (Increase), Credit Clearing (Increase Liability/Pending Cash Out)
            
            -- Line 1: Debit Stock Asset (Cost Base)
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @SpecificStockAccountID, @dblJournalTotal, 0);

            -- Line 2: Credit Clearing Account
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @ClearingAccountID, 0, @dblJournalTotal);
        END
        ELSE IF UPPER(@strType) = 'SELL'
        BEGIN
            -- SELL: Complex. Need to calculate Realized Gain/Loss.
            -- For Phase 1, we will do a simplified posting: 
            -- Debit Clearing (Cash Receivable), Credit Stock Asset (Reduce Cost Base).
            -- Note: This creates a "moving average" cost base reduction. 
            -- Strict accounting requires identifying the specific parcel cost (FIFO), 
            -- but this logic aligns with your current "Event Based" system for now.
            
            -- Line 1: Debit Clearing (Receivable)
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @ClearingAccountID, @dblJournalTotal, 0);

            -- Line 2: Credit Stock Asset (Book Value Out)
            -- WARNING: Strictly speaking, you should only Credit the COST BASE, and the difference goes to Gain/Loss.
            -- However, without running the FIFO engine here, we credit the Proceeds. 
            -- This means the Asset Account Balance represents (Remaining Cost + Realized Gains).
            -- You will need a period-end adjustment process to strip Gains out to the P&L.
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @SpecificStockAccountID, 0, @dblJournalTotal);
        END
    END

    -- =========================================================================
    -- 4. FINISH
    -- =========================================================================
    SELECT 'Share Trade Added Successfully';
END
GO

-- Existing journals: each trade journal whose Trade Clearing line still holds the order total in the trade's currency
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Fix TABLE (iJournalID INT PRIMARY KEY, bSell BIT, dblTotal DECIMAL(19,4));
INSERT INTO @Fix (iJournalID, bSell, dblTotal)
SELECT J.iJournalID,
       CASE WHEN UPPER(LTRIM(RTRIM(T.strType))) = 'SELL' THEN 1 ELSE 0 END,
       ROUND(T.dblOrderTotal / T.dblExchangeRate, 2)
FROM dbo.tblJournal J
JOIN dbo.tblShareTrades T ON T.iShareTradeId = J.iSourceID AND T.iUserId = J.iUserID
WHERE J.strSourceModule = 'ShareTrade'
  AND T.dblExchangeRate > 0 AND ABS(T.dblExchangeRate - 1) > 0.000001
  AND ABS(ROUND(T.dblOrderTotal / T.dblExchangeRate, 2) - T.dblOrderTotal) >= 0.01
  AND (dbo.fnLockedTo(J.iUserID) IS NULL OR J.dtmTransactionDate > dbo.fnLockedTo(J.iUserID))
  AND EXISTS (SELECT 1 FROM dbo.tblJournalLines L JOIN dbo.tblChartOfAccounts A ON A.iAccountID = L.iAccountID
              WHERE L.iJournalID = J.iJournalID AND A.strAccountName = 'Trade Clearing / Suspense'
                AND ABS(ISNULL(L.dblDebit, 0) + ISNULL(L.dblCredit, 0) - ROUND(T.dblOrderTotal, 4)) < 0.005);

BEGIN TRANSACTION;
-- A sale's realised gain line is re-posted by the site from the converted costs
DELETE L FROM dbo.tblJournalLines L JOIN @Fix F ON F.iJournalID = L.iJournalID
WHERE F.bSell = 1 AND L.strDescription = 'Realised gain or loss on sale (FIFO)';
-- The clearing line and the cost base line each carry the order total, on whichever side they were posted
UPDATE L SET dblDebit = CASE WHEN ISNULL(L.dblDebit, 0) > 0 THEN F.dblTotal ELSE 0 END,
             dblCredit = CASE WHEN ISNULL(L.dblDebit, 0) > 0 THEN 0 ELSE F.dblTotal END
FROM dbo.tblJournalLines L
JOIN @Fix F ON F.iJournalID = L.iJournalID
JOIN dbo.tblChartOfAccounts A ON A.iAccountID = L.iAccountID
WHERE A.strAccountName = 'Trade Clearing / Suspense' OR A.strAccountName LIKE '% - Cost Base';
COMMIT;
DECLARE @Count INT = (SELECT COUNT(*) FROM @Fix);
PRINT CAST(@Count AS VARCHAR(20)) + ' foreign trade journal(s) converted to the portfolio''s currency.';
GO

PRINT 'Foreign trades ledger migration done.';
GO
