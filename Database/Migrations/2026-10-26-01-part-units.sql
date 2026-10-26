-- =============================================
-- Migration: part units
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build that takes part units (crypto,
-- managed funds). Run it after 2026-10-21-13-ledger-fx.sql, 2026-10-21-09-complex-corporate-actions.sql and
-- 2026-10-21-10-employee-shares-options.sql. Safe to run more than once.
--
-- tblShareTrades.iQuantity is already a FLOAT; the procs that add trades took whole numbers only. This changes:
--   * dbo.fnUnitsText: units as text with no trailing zeros (0.5, 1234.56789), for journal descriptions.
--   * uspAddShareTrade, AddShareTradeWithCashTransaction, uspAddDRPTransaction, uspApplyDemerger, uspApplyTakeover and
--     uspExerciseOptions take their unit counts as FLOAT. Nothing else in them changes.
--   * tblCGTParcelMatching.iMatchedQuantity becomes FLOAT (nothing fills that table now, but it should hold part units).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.uspApplyTakeover', 'P') IS NULL OR OBJECT_ID('dbo.uspExerciseOptions', 'P') IS NULL OR OBJECT_ID('dbo.uspPostSaleGain', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-09-complex-corporate-actions.sql, 2026-10-21-10-employee-shares-options.sql and 2026-10-21-13-ledger-fx.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- Units as text without trailing zeros: 12, 0.5, 1234.56789012
CREATE OR ALTER FUNCTION dbo.fnUnitsText(@Units FLOAT)
RETURNS VARCHAR(40)
AS
BEGIN
    DECLARE @Text VARCHAR(40) = CONVERT(VARCHAR(40), CAST(ROUND(ISNULL(@Units, 0), 8) AS DECIMAL(28, 8)));
    -- Trailing zeros, then a trailing point
    SET @Text = REPLACE(RTRIM(REPLACE(@Text, '0', ' ')), ' ', '0');
    SET @Text = REPLACE(RTRIM(REPLACE(@Text, '.', ' ')), ' ', '.');
    RETURN CASE WHEN @Text = '' THEN '0' ELSE @Text END;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.tblCGTParcelMatching') AND name = 'iMatchedQuantity'
           AND system_type_id = TYPE_ID('int'))
    ALTER TABLE dbo.tblCGTParcelMatching ALTER COLUMN iMatchedQuantity FLOAT NOT NULL;
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddShareTrade] 
(
    @iUserId Integer,
    @strMarket VarChar(50),
    @strCode VarChar(50),
    @strName VarChar(500),
    @strType VarChar(50),
    @dtmDateOfTransaction Date,
    @iQuantity Float,
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
-- Date:        26-Oct-2026
-- Description:	Part units: the quantity is a FLOAT (up to 8 decimal places from the site), not a whole number.
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
    DECLARE @iAbsQuantity FLOAT = ABS(@iQuantity);
    
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
            @strType + ' ' + dbo.fnUnitsText(@iAbsQuantity) + ' units of ' + @strCode,
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

CREATE OR ALTER PROCEDURE [dbo].[AddShareTradeWithCashTransaction] 
(
    @iUserId            Integer,
    @strMarket          VarChar(50),
    @strCode            VarChar(50),
    @strName            VarChar(500),
    @strType            VarChar(50),
    @dtmDateOfTransaction Date,
    @iQuantity          Float,
    @dblPricePerUnit    Float,
    @dblBrokerage       Float,
    @strCurrency        VarChar(50),
    @dblExchangeRate    Float,
    @strShareTradeComment VarChar(500),
    @strBankAccountName VarChar(500),
    @dtmSettlementDate  Date,
    @strCashTransactionComment VarChar(500)
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        07-Dec-2019
-- Version:     1.001
-- Description:	Records a new share trade & records the corresponding cash transaction
-- =============================================
-- =============================================
-- Author:		Tony Darcy & Gemini
-- Date:        05-Dec-2025
-- Version:     1.002
-- Description:	Adds logic for double entry accounting
-- =============================================
-- Date:        26-Oct-2026
-- Description:	Part units: the quantity is a FLOAT (up to 8 decimal places from the site), not a whole number.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    -- =========================================================================
    -- 1. LEGACY VALIDATION & DEFAULTS
    -- =========================================================================
    
    -- Stock Name Default
    IF @strName IS NULL OR @strName = ''
        SET @strName = COALESCE((SELECT TOP 1 strStockName FROM [dbo].[tblShareDetails] WHERE strStockExchangeCode = @strMarket AND strStockCode = @strCode), '');
    IF @strName IS NULL SET @strName = '';

    -- Bank Account Default
    IF @strBankAccountName IS NULL OR @strBankAccountName = ''
        SET @strBankAccountName = COALESCE((SELECT TOP 1 strBankAccountName FROM [dbo].[tblBankStatement] WHERE iUserId = @iUserId ORDER BY dtmDateOfTransaction DESC), 'Default Bank Account');

    -- Settlement Date Default (T+2)
    IF @dtmSettlementDate IS NULL
        SET @dtmSettlementDate = DATEADD(DAY, 2, @dtmDateOfTransaction);

    -- Comment Default
    IF @strCashTransactionComment IS NULL
        SET @strCashTransactionComment = @strType + ' Settlement: ' + dbo.fnUnitsText(@iQuantity) + ' ' + @strCode;

    -- =========================================================================
    -- 2. EXECUTE SHARE TRADE (Legacy + Ledger)
    -- =========================================================================
    -- This SP (which you updated previously) handles the Trade -> Clearing Journal
    DECLARE @iBankStatementId Integer = NULL;
    
    EXEC [dbo].[uspAddShareTrade] 
        @iUserId, @strMarket, @strCode, @strName, @strType, 
        @dtmDateOfTransaction, @iQuantity, @dblPricePerUnit, @dblBrokerage, 
        @strCurrency, @dblExchangeRate, @strShareTradeComment, @iBankStatementId;

    -- =========================================================================
    -- 3. CALCULATE CASH AMOUNTS (Legacy Logic)
    -- =========================================================================
    
    DECLARE @dblWithdrawalAmount Float = 0;
    DECLARE @dblDepositAmount    Float = 0;
    
    -- Normalize Logic (Sharesight sell exports are negative)
    DECLARE @iAbsQuantity FLOAT = ABS(@iQuantity);
    
    IF UPPER(@strType) = 'BUY'
    BEGIN
        SET @dblWithdrawalAmount = ROUND((((@dblPricePerUnit * @iAbsQuantity) + @dblBrokerage) / @dblExchangeRate), 2);
    END
    ELSE IF UPPER(@strType) = 'SELL'
    BEGIN
        SET @dblDepositAmount = ROUND((((@dblPricePerUnit * @iAbsQuantity) - @dblBrokerage) / @dblExchangeRate), 2);
    END

    -- =========================================================================
    -- 4. EXECUTE CASH TRANSACTION (Legacy Logic)
    -- =========================================================================
    -- Retrieve the Trade ID we just created (Legacy Lookup method)
    DECLARE @iShareTradeID Integer;
    
    SELECT TOP 1 @iShareTradeID = [iShareTradeID] 
    FROM [tblShareTrades] 
    WHERE [iUserId] = @iUserId
      AND [strStockExchangeCode] = @strMarket
      AND [strStockCode] = @strCode
      AND [strType] = @strType
      AND [dtmDateOfTransaction] = @dtmDateOfTransaction
      ORDER BY [iShareTradeID] DESC;

    -- Add the Legacy Bank Statement Record
    EXEC [dbo].[uspAddCashTransaction] 
        @iUserId, @strBankAccountName, @dtmSettlementDate, 
        @dblWithdrawalAmount, @dblDepositAmount, @strCashTransactionComment, @iShareTradeID;

    -- Link Trade to Bank Statement (Legacy Requirement)
    -- Need to find the ID of the bank statement we just added
    SET @iBankStatementId = (
        SELECT TOP 1 [iBankStatementId] 
        FROM [tblBankStatement] 
        WHERE [iUserId] = @iUserId AND [iShareTradeId] = @iShareTradeID 
        ORDER BY [iBankStatementId] DESC
    );

    UPDATE [tblShareTrades]
    SET [iBankStatementId] = @iBankStatementId
    WHERE [iShareTradeId] = @iShareTradeId;

    -- =========================================================================
    -- 5. DOUBLE ENTRY: CASH SETTLEMENT (New Feature)
    -- =========================================================================
    
    DECLARE @strAccountingMode VARCHAR(20);
    SELECT TOP 1 @strAccountingMode = ISNULL(strAccountingMode, 'SingleEntry') 
    FROM tblUsers WHERE iUserId = @iUserId;

    IF @strAccountingMode IN ('Hybrid', 'DoubleEntry')
    BEGIN
        DECLARE @JournalID INT;
        DECLARE @BankGLAccountID INT;
        DECLARE @ClearingAccountID INT;
        DECLARE @ParentBankID INT;
        DECLARE @TotalCashAmount DECIMAL(19,4) = CASE WHEN @dblWithdrawalAmount > 0 THEN @dblWithdrawalAmount ELSE @dblDepositAmount END;

        -- A. GET CLEARING ACCOUNT (Must match uspAddShareTrade)
        -- This is where the Trade put the money "Pending Settlement"
        SELECT TOP 1 @ClearingAccountID = iAccountID 
        FROM tblChartOfAccounts 
        WHERE strAccountName = 'Trade Clearing / Suspense' AND (iUserID = 0 OR iUserID = @iUserId);

        -- B. GET OR CREATE BANK GL ACCOUNT
        -- We verify if a GL account exists for this specific "Bank Account Name"
        SELECT TOP 1 @BankGLAccountID = iAccountID
        FROM tblChartOfAccounts
        WHERE strAccountName = @strBankAccountName AND iUserID = @iUserId;

        IF @BankGLAccountID IS NULL
        BEGIN
            -- Find the System "Cash at Bank" Header (1100)
            SELECT TOP 1 @ParentBankID = iAccountID FROM tblChartOfAccounts WHERE iAccountNumber = 1100 AND iUserID = 0;
            
            -- Create the specific bank account in the ledger
            INSERT INTO tblChartOfAccounts (
                iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable
            )
            VALUES (
                @iUserId, @ParentBankID, 1101, -- Placeholder number
                @strBankAccountName, 'Asset', @strCurrency, 1
            );
            SET @BankGLAccountID = SCOPE_IDENTITY();
        END

        -- C. CREATE JOURNAL (Settlement)
        INSERT INTO tblJournal (
            iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID
        )
        VALUES (
            @iUserId, @dtmSettlementDate, 
            'Settlement: ' + @strType + ' ' + @strCode,
            'BankTransaction', @iBankStatementId
        );
        SET @JournalID = SCOPE_IDENTITY();

        -- D. POST LINES
        IF UPPER(@strType) = 'BUY'
        BEGIN
            -- BUY SETTLEMENT: We pay cash to clear the liability.
            -- Dr Clearing (Reduce Liability)
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @ClearingAccountID, @TotalCashAmount, 0);

            -- Cr Bank (Reduce Asset)
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @BankGLAccountID, 0, @TotalCashAmount);
        END
        ELSE IF UPPER(@strType) = 'SELL'
        BEGIN
            -- SELL SETTLEMENT: We receive cash, clearing the receivable.
            -- Dr Bank (Increase Asset)
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @BankGLAccountID, @TotalCashAmount, 0);

            -- Cr Clearing (Reduce Receivable)
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @ClearingAccountID, 0, @TotalCashAmount);
        END
    END

    -- =========================================================================
    -- 6. FINISH
    -- =========================================================================
    SELECT 'Share Trade & Cash Transaction Added Successfully';
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
                'Buy ' + dbo.fnUnitsText(@iQuantity) + ' units of ' + @strStockCode + ' (DRP)',
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

CREATE OR ALTER PROCEDURE dbo.uspApplyDemerger
    @iUserID         INT,
    @strMarket       VARCHAR(50),
    @strCode         VARCHAR(50),
    @strNewMarket    VARCHAR(50),
    @strNewCode      VARCHAR(50),
    @strNewName      VARCHAR(500) = NULL,
    @dtmDate         DATE,
    @iNewUnits       FLOAT,
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

CREATE OR ALTER PROCEDURE dbo.uspApplyTakeover
    @iUserID          INT,
    @strMarket        VARCHAR(50),
    @strCode          VARCHAR(50),
    @strNewMarket     VARCHAR(50) = NULL,
    @strNewCode       VARCHAR(50) = NULL,
    @strNewName       VARCHAR(500) = NULL,
    @dtmDate          DATE,
    @iUnitsSold       FLOAT,
    @dblConsideration FLOAT,
    @iNewUnits        FLOAT = 0,
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

CREATE OR ALTER PROCEDURE dbo.uspExerciseOptions
    @iUserID          INT,
    @strMarket        VARCHAR(50),
    @strCode          VARCHAR(50),
    @strNewMarket     VARCHAR(50),
    @strNewCode       VARCHAR(50),
    @strNewName       VARCHAR(500) = NULL,
    @dtmDate          DATE,
    @iOptions         FLOAT,
    @iShares          FLOAT,
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
