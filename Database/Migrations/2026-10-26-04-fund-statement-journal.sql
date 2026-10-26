-- =============================================
-- Migration: post a fund tax statement's cost base change to the ledger
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build that posts fund statements.
-- Run it after 2026-10-21-07-fund-tax-statements.sql. Safe to run more than once.
--
-- A fund's payouts are posted as Trust Distribution Income when they're paid. Its annual tax statement then says how
-- much of that cash was really a return of capital (an AMIT cost base net decrease, or tax-deferred amounts), or how
-- much taxable income was attributed without being paid (an AMIT net increase). This posts that change, at the year end:
--     decrease:  Dr 4300 Trust Distribution Income   Cr <CODE>.<EXCH> - Cost Base
--     increase:  Dr <CODE>.<EXCH> - Cost Base         Cr 4300 Trust Distribution Income
--   * uspPostFundTaxStatementJournal (re)posts the journal for one statement (source 'FundStatement'), and removes it
--     when the statement is gone or has no cost base change. Only in Hybrid / DoubleEntry mode.
--   * Statements already entered are posted when this runs.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblFundTaxStatements', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-07-fund-tax-statements.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspPostFundTaxStatementJournal]
(
    @iUserID             INT,
    @iFundTaxStatementID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        26-Oct-2026
-- Description: Replaces the ledger journal for a fund tax statement's cost base change. Returns no result set.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- The old journal goes first, so a changed or deleted statement leaves nothing stale behind
    DECLARE @Old TABLE (iJournalID INT PRIMARY KEY);
    INSERT INTO @Old SELECT iJournalID FROM tblJournal
    WHERE iUserID = @iUserID AND strSourceModule = 'FundStatement' AND iSourceID = @iFundTaxStatementID;
    DELETE L FROM tblJournalLines L JOIN @Old O ON O.iJournalID = L.iJournalID;
    DELETE J FROM tblJournal J JOIN @Old O ON O.iJournalID = J.iJournalID;

    DECLARE @strAccountingMode VARCHAR(20);
    SELECT TOP 1 @strAccountingMode = ISNULL(strAccountingMode, 'SingleEntry') FROM tblUsers WHERE iUserId = @iUserID;
    IF ISNULL(@strAccountingMode, 'SingleEntry') NOT IN ('Hybrid', 'DoubleEntry')
        RETURN;

    DECLARE @Exchange VARCHAR(50), @Code VARCHAR(50), @YearEnd DATE, @Year INT, @Net DECIMAL(19,4);
    SELECT @Exchange = RTRIM(strStockExchangeCode), @Code = RTRIM(strStockCode), @YearEnd = dtmYearEnd, @Year = iFinancialYear,
           @Net = ROUND(CAST(ISNULL(dblCostBaseDecrease, 0) - ISNULL(dblCostBaseIncrease, 0) AS DECIMAL(19,4)), 2)
    FROM dbo.tblFundTaxStatements
    WHERE iFundTaxStatementID = @iFundTaxStatementID AND iUserID = @iUserID;
    IF @Code IS NULL OR @Net = 0
        RETURN;

    -- The fund's cost base account, made as a trade would make it if it isn't there yet
    DECLARE @StockAccountName VARCHAR(255) = @Code + '.' + @Exchange + ' - Cost Base';
    DECLARE @StockAccountID INT, @IncomeAccountID INT, @ParentAccountID INT;
    SELECT TOP 1 @StockAccountID = iAccountID FROM tblChartOfAccounts WHERE strAccountName = @StockAccountName AND iUserID = @iUserID;
    IF @StockAccountID IS NULL
    BEGIN
        SELECT TOP 1 @ParentAccountID = iAccountID FROM tblChartOfAccounts
        WHERE iAccountNumber = 1300 AND (iUserID = 0 OR iUserID = @iUserID) ORDER BY iUserID DESC;
        INSERT INTO tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable)
        SELECT @iUserID, @ParentAccountID, 1301, @StockAccountName, 'Asset', ISNULL(NULLIF(strPortfolioCurrency, ''), 'AUD'), 1
        FROM tblUsers WHERE iUserId = @iUserID;
        SET @StockAccountID = SCOPE_IDENTITY();
    END
    EXEC dbo.uspGetLedgerAccount @iUserID, 4300, 'Trust Distribution Income', 'Revenue', @IncomeAccountID OUTPUT;
    IF @StockAccountID IS NULL OR @IncomeAccountID IS NULL
        THROW 50001, 'The accounts for the fund statement journal could not be found.', 1;

    DECLARE @Years VARCHAR(9) = CAST(@Year - 1 AS VARCHAR(4)) + '-' + RIGHT(CAST(@Year AS VARCHAR(4)), 2);
    DECLARE @Description VARCHAR(500) = @Code + ' ' + @Years + ' tax statement: cost base '
        + CASE WHEN @Net > 0 THEN 'decrease (tax-deferred part of the distributions)' ELSE 'increase (income attributed but not paid)' END;
    DECLARE @iJournalID INT;
    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
    VALUES (@iUserID, @YearEnd, @Description, 'FundStatement', @iFundTaxStatementID);
    SET @iJournalID = SCOPE_IDENTITY();

    DECLARE @Amount DECIMAL(19,4) = ABS(@Net);
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES
        (@iJournalID, CASE WHEN @Net > 0 THEN @IncomeAccountID ELSE @StockAccountID END, @Amount, 0,
         CASE WHEN @Net > 0 THEN 'Distributions that were a return of capital' ELSE 'Cost base increase' END),
        (@iJournalID, CASE WHEN @Net > 0 THEN @StockAccountID ELSE @IncomeAccountID END, 0, @Amount,
         CASE WHEN @Net > 0 THEN 'Cost base decrease' ELSE 'Income attributed by the fund' END);
END
GO

-- Post the statements already entered
DECLARE @User INT, @Id INT;
DECLARE statements CURSOR LOCAL FAST_FORWARD FOR
    SELECT iUserID, iFundTaxStatementID FROM dbo.tblFundTaxStatements
    WHERE ISNULL(dblCostBaseDecrease, 0) <> ISNULL(dblCostBaseIncrease, 0);
OPEN statements;
FETCH NEXT FROM statements INTO @User, @Id;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC dbo.uspPostFundTaxStatementJournal @User, @Id;
    END TRY
    BEGIN CATCH
        -- A statement in a locked period is left as it was
        PRINT 'Fund statement ' + CAST(@Id AS VARCHAR(10)) + ' not posted: ' + ERROR_MESSAGE();
    END CATCH
    FETCH NEXT FROM statements INTO @User, @Id;
END
CLOSE statements;
DEALLOCATE statements;
GO

SET NOEXEC OFF;
GO
