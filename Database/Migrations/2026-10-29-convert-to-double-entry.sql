-- =============================================
-- Migration: Switch a single-entry portfolio to double entry (29-Oct-2026).
--   * dbo.fnDoubleEntryPending: the records of a portfolio that have no ledger journal yet but would have one in
--     double entry (trades, settlements, cash, payouts, DRP, returns of capital, demergers, fund statements, loans).
--   * dbo.uspGetDoubleEntryConversion: what switching would post, for the page to show first.
--   * dbo.uspConvertToDoubleEntry: posts those journals, the same ones the add procs post in double entry, and
--     sets the portfolio to DoubleEntry, all in one transaction.
--   * tblUsers.strAccountingMode now defaults to DoubleEntry (AddNewUser and AddNewPortfolio already set it).
-- Safe to run twice. Needs 2026-10-26-07-loans-in-ledger.sql and 2026-10-26-04-fund-statement-journal.sql first.
-- =============================================
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- A row added without a mode is double entry, as the site already makes every new portfolio
DECLARE @Default SYSNAME, @Sql NVARCHAR(400);
SELECT @Default = D.name
FROM sys.default_constraints D
JOIN sys.columns C ON C.object_id = D.parent_object_id AND C.column_id = D.parent_column_id
WHERE D.parent_object_id = OBJECT_ID('dbo.tblUsers') AND C.name = 'strAccountingMode'
  AND D.definition <> '(''DoubleEntry'')';
IF @Default IS NOT NULL
BEGIN
    SET @Sql = N'ALTER TABLE dbo.tblUsers DROP CONSTRAINT ' + QUOTENAME(@Default);
    EXEC sp_executesql @Sql;
END
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints D
               JOIN sys.columns C ON C.object_id = D.parent_object_id AND C.column_id = D.parent_column_id
               WHERE D.parent_object_id = OBJECT_ID('dbo.tblUsers') AND C.name = 'strAccountingMode')
    ALTER TABLE dbo.tblUsers ADD CONSTRAINT DF_tblUsers_strAccountingMode DEFAULT ('DoubleEntry') FOR strAccountingMode;
GO

CREATE OR ALTER FUNCTION dbo.fnDoubleEntryPending (@iUserId INT)
RETURNS TABLE
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: Every record of the portfolio that would have posted a journal in double entry but has none.
--              iStep is the order uspConvertToDoubleEntry posts them in: trades make the cost base and clearing
--              accounts, cash makes the bank accounts, and DRP, payouts and returns of capital then find the
--              bank account as they do when added.
--                1 Trade          a buy or sell (uspAddShareTrade)          source ShareTrade
--                2 Settlement     a trade's cash row (AddShareTradeWithCash)  source BankTransaction
--                2 Cash           any other cash row (uspAddCashTransaction)  source BankTransaction
--                3 DrpTrade       a dividend reinvestment buy (uspAddDRPTransaction) source ShareTrade
--                4 Payout         a payout (uspPostPayoutJournal)           source Payout
--                5 ReturnOfCapital (uspAddReturnOfCapital)                  source ReturnOfCapital
--                6 Demerger       the cost moved out of the old shares (uspApplyDemerger) source CorporateAction
--                7 FundStatement  an AMIT cost base change (uspPostFundTaxStatementJournal) source FundStatement
--                8 LoanEntry      a loan drawdown, repayment or interest (uspPostLoanEntryJournal) source LoanEntry
-- =============================================
RETURN
    WITH Trades AS (
        SELECT T.iShareTradeId, T.dtmDateOfTransaction, T.dblOrderTotal,
               UPPER(LTRIM(RTRIM(T.strType))) AS strType,
               CASE WHEN EXISTS (SELECT 1 FROM dbo.tblPayouts P WHERE P.iUserID = @iUserId AND P.iReinvestmentTradeID = T.iShareTradeId)
                    THEN 1 ELSE 0 END AS bDrp
        FROM dbo.tblShareTrades T
        WHERE T.iUserId = @iUserId AND UPPER(LTRIM(RTRIM(T.strType))) IN ('BUY', 'SELL')),
    Cash AS (
        SELECT B.iBankStatementId, B.dtmDateOfTransaction,
               CASE WHEN EXISTS (SELECT 1 FROM Trades T WHERE T.iShareTradeId = B.iShareTradeId AND T.bDrp = 0)
                    THEN 'Settlement' ELSE 'Cash' END AS strKind
        FROM dbo.tblBankStatement B
        WHERE B.iUserId = @iUserId AND (ISNULL(B.dblDepositAmount, 0) <> 0 OR ISNULL(B.dblWithdrawalAmount, 0) <> 0))
    SELECT 1 AS iStep, 'Trade' AS strKind, T.iShareTradeId AS iSourceID, T.dtmDateOfTransaction AS dtmDate
    FROM Trades T
    WHERE T.bDrp = 0
      AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'ShareTrade' AND J.iSourceID = T.iShareTradeId)
    UNION ALL
    SELECT 2, C.strKind, C.iBankStatementId, C.dtmDateOfTransaction
    FROM Cash C
    WHERE NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'BankTransaction' AND J.iSourceID = C.iBankStatementId)
    UNION ALL
    SELECT 3, 'DrpTrade', T.iShareTradeId, T.dtmDateOfTransaction
    FROM Trades T
    WHERE T.bDrp = 1 AND T.strType = 'BUY' AND ROUND(ISNULL(T.dblOrderTotal, 0), 2) <> 0
      AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'ShareTrade' AND J.iSourceID = T.iShareTradeId)
    UNION ALL
    SELECT 4, 'Payout', P.iPayoutID, P.dtmTransactionDate
    FROM dbo.tblPayouts P
    WHERE P.iUserID = @iUserId
      AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'Payout' AND J.iSourceID = P.iPayoutID)
    UNION ALL
    SELECT 5, 'ReturnOfCapital', R.iReturnOfCapitalID, R.dtmPaymentDate
    FROM dbo.tblReturnsOfCapital R
    WHERE R.iUserID = @iUserId AND ROUND(ISNULL(R.dblTotalAmount, 0), 2) > 0
      AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'ReturnOfCapital' AND J.iSourceID = R.iReturnOfCapitalID)
    UNION ALL
    SELECT 6, 'Demerger', X.iParcelTransferID, X.dtmDate
    FROM dbo.tblParcelTransfers X
    JOIN dbo.tblShareTrades T ON T.iShareTradeId = X.iBuyTradeID AND T.iUserId = @iUserId
    WHERE X.iUserID = @iUserId AND X.strKind = 'Demerger' AND ISNULL(T.dblOrderTotal, 0) > 0
      AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'CorporateAction' AND J.iSourceID = X.iParcelTransferID)
    UNION ALL
    SELECT 7, 'FundStatement', F.iFundTaxStatementID, F.dtmYearEnd
    FROM dbo.tblFundTaxStatements F
    WHERE F.iUserID = @iUserId AND ROUND(ISNULL(F.dblCostBaseDecrease, 0) - ISNULL(F.dblCostBaseIncrease, 0), 2) <> 0
      AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'FundStatement' AND J.iSourceID = F.iFundTaxStatementID)
    UNION ALL
    SELECT 8, 'LoanEntry', E.iLoanEntryID, E.dtmDate
    FROM dbo.tblLoanEntries E
    JOIN dbo.tblLoans L ON L.iLoanID = E.iLoanID
    WHERE L.iUserID = @iUserId
      AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal J WHERE J.iUserID = @iUserId AND J.strSourceModule = 'LoanEntry' AND J.iSourceID = E.iLoanEntryID);
GO

CREATE OR ALTER PROCEDURE dbo.uspGetDoubleEntryConversion
    @iUserId      INT,  -- the signed-in user
    @iPortfolioId INT
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: What switching the portfolio to double entry would do. Returns one row: the current mode, whether
--              the user may switch it (owners only, fnCanSharePortfolio), how many of each kind of record would
--              get a journal, the earliest date among them, the lock date, how many bank import journals are
--              already in the ledger (their allocations can double up with the payouts and cash now posted) and
--              how many manual journals there are.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT ISNULL(U.strAccountingMode, 'SingleEntry') AS strAccountingMode,
           CAST(dbo.fnCanSharePortfolio(@iUserId, @iPortfolioId) AS BIT) AS bCanSwitch,
           ISNULL(P.Trades, 0) AS iTrades, ISNULL(P.Cash, 0) AS iCash, ISNULL(P.Payouts, 0) AS iPayouts,
           ISNULL(P.CorporateActions, 0) AS iCorporateActions, ISNULL(P.Other, 0) AS iOther,
           P.dtmEarliest, dbo.fnLockedTo(@iPortfolioId) AS dtmLockedTo,
           (SELECT COUNT(*) FROM dbo.tblJournal J WHERE J.iUserID = @iPortfolioId AND J.strSourceModule = 'BankImport') AS iBankImportJournals,
           (SELECT COUNT(*) FROM dbo.tblJournal J WHERE J.iUserID = @iPortfolioId AND J.strSourceModule = 'ManualJournal') AS iManualJournals
    FROM dbo.tblUsers U
    OUTER APPLY (
        SELECT SUM(CASE WHEN strKind IN ('Trade', 'DrpTrade') THEN 1 ELSE 0 END) AS Trades,
               SUM(CASE WHEN strKind IN ('Settlement', 'Cash') THEN 1 ELSE 0 END) AS Cash,
               SUM(CASE WHEN strKind = 'Payout' THEN 1 ELSE 0 END) AS Payouts,
               SUM(CASE WHEN strKind IN ('ReturnOfCapital', 'Demerger') THEN 1 ELSE 0 END) AS CorporateActions,
               SUM(CASE WHEN strKind IN ('FundStatement', 'LoanEntry') THEN 1 ELSE 0 END) AS Other,
               MIN(dtmDate) AS dtmEarliest
        FROM dbo.fnDoubleEntryPending(@iPortfolioId)) P
    WHERE U.iUserId = @iPortfolioId;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspConvertToDoubleEntry
    @iUserId      INT,  -- the signed-in user
    @iPortfolioId INT
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: Switches a single-entry (or Hybrid) portfolio to double entry. Every record in
--              fnDoubleEntryPending gets the journal its add proc posts in double entry, worked out from the saved
--              row, and the portfolio's mode becomes DoubleEntry, all in one transaction: if anything fails, nothing
--              changes. Records are not changed, and those that already have a journal are left alone. Sales are
--              posted at their proceeds, as uspAddShareTrade posts them; the site then moves each sale's FIFO gain
--              to Realised Gains (RealisedGainService). Owners only. Refused when any record to post is dated in a
--              locked period. Keep the journal blocks in step with the add procs: tests/smoke/convert-check.sql
--              compares the two. Returns one row: Result = 'Switched' or a message starting with 'Error:', and
--              iJournals, the number of journals posted.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Mode VARCHAR(20) = (SELECT ISNULL(strAccountingMode, 'SingleEntry') FROM dbo.tblUsers WHERE iUserId = @iPortfolioId);
    DECLARE @LockedTo DATE = dbo.fnLockedTo(@iPortfolioId);
    DECLARE @Error VARCHAR(300) = CASE
        WHEN @Mode IS NULL THEN 'That portfolio wasn''t found.'
        WHEN dbo.fnCanSharePortfolio(@iUserId, @iPortfolioId) = 0 THEN 'Only the portfolio''s owner can switch it to double entry.'
        WHEN @Mode = 'DoubleEntry' THEN 'This portfolio already uses double entry.'
        WHEN @LockedTo IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.fnDoubleEntryPending(@iPortfolioId) WHERE dtmDate <= @LockedTo)
            THEN 'Some records are dated in a locked period (up to ' + CONVERT(VARCHAR(11), @LockedTo, 106)
                 + '). Remove the lock on the Lock periods page, switch, then lock the years again.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, 0 AS iJournals;
        RETURN;
    END

    DECLARE @Pending TABLE (iRow INT IDENTITY PRIMARY KEY, iStep INT, strKind VARCHAR(20), iSourceID INT);
    DECLARE @Before INT = (SELECT COUNT(*) FROM dbo.tblJournal WHERE iUserID = @iPortfolioId);

    BEGIN TRANSACTION;

    DECLARE @Lock VARCHAR(255) = 'uspConvertToDoubleEntry:' + CAST(@iPortfolioId AS VARCHAR(20));
    EXEC sp_getapplock @Resource = @Lock, @LockMode = 'Exclusive', @LockOwner = 'Transaction';

    -- The posting procs only post in double entry, so the mode changes first (inside the transaction)
    UPDATE dbo.tblUsers SET strAccountingMode = 'DoubleEntry' WHERE iUserId = @iPortfolioId;

    INSERT INTO @Pending (iStep, strKind, iSourceID)
    SELECT iStep, strKind, iSourceID FROM dbo.fnDoubleEntryPending(@iPortfolioId) ORDER BY iStep, dtmDate, iSourceID;

    DECLARE @Row INT = 1, @Last INT = (SELECT ISNULL(MAX(iRow), 0) FROM @Pending);
    DECLARE @Kind VARCHAR(20), @Id INT, @JournalID INT;
    DECLARE @Date DATE, @Type VARCHAR(50), @Code VARCHAR(50), @Market VARCHAR(50), @Currency VARCHAR(50), @Text VARCHAR(500);
    DECLARE @Units FLOAT, @Amount DECIMAL(19,4), @Deposit DECIMAL(19,4), @Withdrawal DECIMAL(19,4), @Other FLOAT;
    DECLARE @StockAccountID INT, @ClearingAccountID INT, @BankAccountID INT, @EquityAccountID INT, @ParentID INT, @OldAccountID INT;

    WHILE @Row <= @Last
    BEGIN
        SELECT @Kind = strKind, @Id = iSourceID FROM @Pending WHERE iRow = @Row;
        SELECT @StockAccountID = NULL, @ClearingAccountID = NULL, @BankAccountID = NULL, @EquityAccountID = NULL,
               @ParentID = NULL, @OldAccountID = NULL, @JournalID = NULL;

        IF @Kind IN ('Trade', 'DrpTrade', 'Demerger')
        BEGIN
            -- The share's cost base account, found or added as uspAddShareTrade does
            SELECT @Date = T.dtmDateOfTransaction, @Type = T.strType, @Code = T.strStockCode, @Market = T.strStockExchangeCode,
                   @Currency = T.strCurrency, @Units = ABS(T.iQuantity),
                   @Amount = ROUND(T.dblOrderTotal / ISNULL(NULLIF(T.dblExchangeRate, 0), 1), 2)
            FROM dbo.tblShareTrades T
            WHERE T.iShareTradeId = CASE WHEN @Kind = 'Demerger'
                                         THEN (SELECT iBuyTradeID FROM dbo.tblParcelTransfers WHERE iParcelTransferID = @Id)
                                         ELSE @Id END;

            SELECT TOP 1 @StockAccountID = iAccountID FROM dbo.tblChartOfAccounts
            WHERE strAccountName = @Code + '.' + @Market + ' - Cost Base' AND iUserID = @iPortfolioId;
            IF @StockAccountID IS NULL AND @Kind <> 'Demerger'
            BEGIN
                SELECT TOP 1 @ParentID = iAccountID FROM dbo.tblChartOfAccounts
                WHERE iAccountNumber = 1300 AND (iUserID = 0 OR iUserID = @iPortfolioId);
                INSERT INTO dbo.tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable)
                VALUES (@iPortfolioId, @ParentID, 1301, @Code + '.' + @Market + ' - Cost Base', 'Asset', @Currency, 1);
                SET @StockAccountID = SCOPE_IDENTITY();
            END
        END

        IF @Kind IN ('Trade', 'Settlement', 'Demerger')
        BEGIN
            -- Trade Clearing / Suspense: the trade puts the amount there until the cash settles
            SELECT TOP 1 @ClearingAccountID = iAccountID FROM dbo.tblChartOfAccounts
            WHERE strAccountName = 'Trade Clearing / Suspense' AND (iUserID = 0 OR iUserID = @iPortfolioId)
            ORDER BY iUserID DESC;
            IF @ClearingAccountID IS NULL AND @Kind = 'Trade'
            BEGIN
                INSERT INTO dbo.tblChartOfAccounts (iUserID, iAccountNumber, strAccountName, strAccountType, bIsPostable)
                VALUES (0, 2900, 'Trade Clearing / Suspense', 'Liability', 1);
                SET @ClearingAccountID = SCOPE_IDENTITY();
            END
        END

        IF @Kind IN ('DrpTrade', 'ReturnOfCapital')
        BEGIN
            -- The bank, as uspPostPayoutJournal finds it, so these and the payouts use the same account
            SELECT TOP 1 @BankAccountID = iAccountID FROM dbo.tblChartOfAccounts
            WHERE (iUserID = @iPortfolioId OR iUserID = 0) AND iAccountNumber >= 1100 AND iAccountNumber < 1200
            ORDER BY iUserID DESC, iAccountID;
            IF @BankAccountID IS NULL
                EXEC dbo.uspGetLedgerAccount @iPortfolioId, 1100, 'Cash at Bank', 'Asset', @BankAccountID OUTPUT;
        END

        -- 1. A buy or sell (uspAddShareTrade): Dr cost base / Cr clearing for a buy, the other way for a sale
        IF @Kind = 'Trade'
        BEGIN
            INSERT INTO dbo.tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
            VALUES (@iPortfolioId, @Date, @Type + ' ' + dbo.fnUnitsText(@Units) + ' units of ' + @Code, 'ShareTrade', @Id);
            SET @JournalID = SCOPE_IDENTITY();

            INSERT INTO dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
            VALUES (@JournalID, @StockAccountID, CASE WHEN UPPER(@Type) = 'BUY' THEN @Amount ELSE 0 END, CASE WHEN UPPER(@Type) = 'BUY' THEN 0 ELSE @Amount END),
                   (@JournalID, @ClearingAccountID, CASE WHEN UPPER(@Type) = 'BUY' THEN 0 ELSE @Amount END, CASE WHEN UPPER(@Type) = 'BUY' THEN @Amount ELSE 0 END);
        END

        -- 2. A cash row: a trade's settlement (AddShareTradeWithCashTransaction) or a deposit or withdrawal (uspAddCashTransaction)
        IF @Kind IN ('Settlement', 'Cash')
        BEGIN
            SELECT @Date = B.dtmDateOfTransaction, @Text = B.strBankAccountName,
                   @Currency = CASE WHEN @Kind = 'Settlement' THEN T.strCurrency ELSE B.strCurrency END,
                   @Deposit = ROUND(ISNULL(B.dblDepositAmount, 0), 2), @Withdrawal = ROUND(ISNULL(B.dblWithdrawalAmount, 0), 2),
                   @Type = T.strType, @Code = T.strStockCode, @Other = NULL
            FROM dbo.tblBankStatement B
            LEFT JOIN dbo.tblShareTrades T ON T.iShareTradeId = B.iShareTradeId AND T.iUserId = B.iUserId
            WHERE B.iBankStatementId = @Id;
            SET @Amount = CASE WHEN @Withdrawal > 0 THEN @Withdrawal ELSE @Deposit END;

            SELECT TOP 1 @BankAccountID = iAccountID FROM dbo.tblChartOfAccounts
            WHERE strAccountName = @Text AND iUserID = @iPortfolioId;
            IF @BankAccountID IS NULL
            BEGIN
                SELECT TOP 1 @ParentID = iAccountID FROM dbo.tblChartOfAccounts WHERE iAccountNumber = 1100 AND iUserID = 0;
                INSERT INTO dbo.tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable)
                VALUES (@iPortfolioId, @ParentID, 1101, @Text, 'Asset', @Currency, 1);
                SET @BankAccountID = SCOPE_IDENTITY();
            END

            IF @Kind = 'Settlement'
            BEGIN
                INSERT INTO dbo.tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
                VALUES (@iPortfolioId, @Date, 'Settlement: ' + @Type + ' ' + @Code, 'BankTransaction', @Id);
                SET @JournalID = SCOPE_IDENTITY();

                -- A buy pays the clearing account off; a sale's proceeds clear it
                INSERT INTO dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
                VALUES (@JournalID, @ClearingAccountID, CASE WHEN UPPER(@Type) = 'BUY' THEN @Amount ELSE 0 END, CASE WHEN UPPER(@Type) = 'BUY' THEN 0 ELSE @Amount END),
                       (@JournalID, @BankAccountID, CASE WHEN UPPER(@Type) = 'BUY' THEN 0 ELSE @Amount END, CASE WHEN UPPER(@Type) = 'BUY' THEN @Amount ELSE 0 END);
            END
            ELSE
            BEGIN
                -- Owner's Capital (3100): deposits are capital put in, withdrawals are drawings
                SELECT TOP 1 @EquityAccountID = iAccountID FROM dbo.tblChartOfAccounts
                WHERE iAccountNumber = 3100 AND (iUserID = 0 OR iUserID = @iPortfolioId)
                ORDER BY iUserID DESC;

                INSERT INTO dbo.tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
                SELECT @iPortfolioId, @Date, ISNULL(strComment, 'Manual Cash Transaction'), 'BankTransaction', @Id
                FROM dbo.tblBankStatement WHERE iBankStatementId = @Id;
                SET @JournalID = SCOPE_IDENTITY();

                INSERT INTO dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
                VALUES (@JournalID, @BankAccountID, CASE WHEN @Deposit > 0 THEN @Amount ELSE 0 END, CASE WHEN @Deposit > 0 THEN 0 ELSE @Amount END),
                       (@JournalID, @EquityAccountID, CASE WHEN @Deposit > 0 THEN 0 ELSE @Amount END, CASE WHEN @Deposit > 0 THEN @Amount ELSE 0 END);
            END
        END

        -- 3. A dividend reinvestment buy (uspAddDRPTransaction): Dr cost base / Cr bank, the dividend paid for the units
        IF @Kind = 'DrpTrade'
        BEGIN
            SELECT @Other = MAX(ISNULL(dblOtherAmount, 0)) FROM dbo.tblPayouts WHERE iUserID = @iPortfolioId AND iReinvestmentTradeID = @Id;
            SET @Amount = (SELECT ROUND(CAST(dblOrderTotal AS DECIMAL(19,4)), 2) FROM dbo.tblShareTrades WHERE iShareTradeId = @Id);

            INSERT INTO dbo.tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
            VALUES (@iPortfolioId, @Date,
                    'Buy ' + dbo.fnUnitsText(@Units) + ' units of ' + @Code + CASE WHEN @Other > 0 THEN ' (reward)' ELSE ' (DRP)' END,
                    'ShareTrade', @Id);
            SET @JournalID = SCOPE_IDENTITY();

            INSERT INTO dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
            VALUES (@JournalID, @StockAccountID, @Amount, 0, CASE WHEN @Other > 0 THEN 'Reward units' ELSE 'DRP units' END),
                   (@JournalID, @BankAccountID, 0, @Amount, CASE WHEN @Other > 0 THEN 'Reward received as units' ELSE 'Dividend reinvested' END);
        END

        -- 4. A payout: its own proc posts it from the saved row
        IF @Kind = 'Payout'
            EXEC dbo.uspPostPayoutJournal @iPortfolioId, @Id;

        -- 5. A return of capital (uspAddReturnOfCapital): Dr bank / Cr cost base
        IF @Kind = 'ReturnOfCapital'
        BEGIN
            SELECT @Date = dtmPaymentDate, @Code = strStockCode, @Market = strStockExchangeCode,
                   @Amount = ROUND(dblTotalAmount, 2), @Text = NULLIF(strComment, '')
            FROM dbo.tblReturnsOfCapital WHERE iReturnOfCapitalID = @Id;

            SELECT TOP 1 @StockAccountID = iAccountID FROM dbo.tblChartOfAccounts
            WHERE strAccountName = @Code + '.' + @Market + ' - Cost Base' AND iUserID = @iPortfolioId;
            IF @StockAccountID IS NULL
            BEGIN
                SELECT TOP 1 @ParentID = iAccountID FROM dbo.tblChartOfAccounts
                WHERE iAccountNumber = 1300 AND (iUserID = 0 OR iUserID = @iPortfolioId);
                SELECT @Currency = MAX(strCurrency) FROM dbo.tblShareTrades
                WHERE iUserId = @iPortfolioId AND strStockExchangeCode = @Market AND strStockCode = @Code;
                INSERT INTO dbo.tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable)
                VALUES (@iPortfolioId, @ParentID, 1301, @Code + '.' + @Market + ' - Cost Base', 'Asset', ISNULL(@Currency, 'AUD'), 1);
                SET @StockAccountID = SCOPE_IDENTITY();
            END

            INSERT INTO dbo.tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
            VALUES (@iPortfolioId, @Date, ISNULL(@Text, 'Return of capital ' + @Code), 'ReturnOfCapital', @Id);
            SET @JournalID = SCOPE_IDENTITY();

            INSERT INTO dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
            VALUES (@JournalID, @BankAccountID, @Amount, 0, 'Cash received'),
                   (@JournalID, @StockAccountID, 0, @Amount, 'Return of capital');
        END

        -- 6. A demerger (uspApplyDemerger): the new shares' buy is a trade above; this takes the same cost out of the old shares
        IF @Kind = 'Demerger'
        BEGIN
            SELECT @Date = X.dtmDate, @Text = X.strToCode, @Code = X.strFromCode, @Market = X.strFromExchangeCode,
                   @Amount = CAST(T.dblOrderTotal AS DECIMAL(19,4))
            FROM dbo.tblParcelTransfers X JOIN dbo.tblShareTrades T ON T.iShareTradeId = X.iBuyTradeID
            WHERE X.iParcelTransferID = @Id;

            SELECT TOP 1 @OldAccountID = iAccountID FROM dbo.tblChartOfAccounts
            WHERE strAccountName = @Code + '.' + @Market + ' - Cost Base' AND iUserID = @iPortfolioId;
            IF @ClearingAccountID IS NOT NULL AND @OldAccountID IS NOT NULL
            BEGIN
                INSERT INTO dbo.tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
                VALUES (@iPortfolioId, @Date, LEFT('Demerger of ' + @Text + ' from ' + @Code + ': cost base moved', 500), 'CorporateAction', @Id);
                SET @JournalID = SCOPE_IDENTITY();
                INSERT INTO dbo.tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
                VALUES (@JournalID, @ClearingAccountID, @Amount, 0),
                       (@JournalID, @OldAccountID, 0, @Amount);
            END
        END

        -- 7 and 8. Fund statements and loan entries: their own procs post them from the saved row
        IF @Kind = 'FundStatement'
            EXEC dbo.uspPostFundTaxStatementJournal @iPortfolioId, @Id;
        IF @Kind = 'LoanEntry'
            EXEC dbo.uspPostLoanEntryJournal @iPortfolioId, @Id;

        SET @Row += 1;
    END

    COMMIT TRANSACTION;

    SELECT 'Switched' AS Result, (SELECT COUNT(*) FROM dbo.tblJournal WHERE iUserID = @iPortfolioId) - @Before AS iJournals;
END
GO
