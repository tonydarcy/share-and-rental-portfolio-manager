-- =============================================
-- Migration: loans and fixed income in the ledger
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build that posts loans to the ledger.
-- Run it after 2026-10-21-12-debt-fixed-income.sql and 2026-10-23-01-rental-reports.sql. Safe to run more than once.
--
-- In Hybrid / DoubleEntry mode:
--   * Each loan entry posts a journal (source 'LoanEntry'), with the loan as a liability account of its own (made the
--     first time, and kept in tblLoans.iAccountID so renaming the loan keeps it):
--       drawdown or private redraw   Dr Cash at Bank                    Cr the loan
--       repayment                    Dr the loan                        Cr Cash at Bank
--       interest or fee charged      Dr 5300 Investment Loan Interest   Cr the loan
--     uspPostLoanEntryJournal (re)posts one entry's journal, and removes it when the entry is gone. Entries already
--     recorded are posted when this runs.
--   * uspPostInterestAccruals posts a year's interest accruals at the year end and reverses them the next day (source
--     'InterestAccrual'):
--       interest earned on term deposits and bonds but not yet paid   Dr 1450 Accrued Interest Receivable  Cr 4100 Interest Income
--       interest owed on loans but not yet charged                    Dr 5300 Investment Loan Interest     Cr 2390 Accrued Interest Payable
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblLoanEntries', 'U') IS NULL OR OBJECT_ID('dbo.uspGetLedgerAccount', 'P') IS NULL OR OBJECT_ID('dbo.tblRentalLoanLinks', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-12-debt-fixed-income.sql and 2026-10-23-01-rental-reports.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iUserID = 0 AND ((iAccountNumber IN (1450) AND strAccountType <> 'Asset')
    OR (iAccountNumber IN (2300, 2390) AND strAccountType <> 'Liability') OR (iAccountNumber = 5300 AND strAccountType <> 'Expense')))
BEGIN
    RAISERROR('Stopped: account 1450, 2300, 2390 or 5300 is already used for another kind of account. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF COL_LENGTH('dbo.tblLoans', 'iAccountID') IS NULL
    ALTER TABLE dbo.tblLoans ADD iAccountID INT NULL;
GO

CREATE OR ALTER PROCEDURE dbo.uspPostLoanEntryJournal
    @iUserID      INT,
    @iLoanEntryID INT
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        26-Oct-2026
-- Description: Replaces the ledger journal for one loan entry. Returns no result set.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Old TABLE (iJournalID INT PRIMARY KEY);
    INSERT INTO @Old SELECT iJournalID FROM tblJournal WHERE iUserID = @iUserID AND strSourceModule = 'LoanEntry' AND iSourceID = @iLoanEntryID;
    DELETE L FROM tblJournalLines L JOIN @Old O ON O.iJournalID = L.iJournalID;
    DELETE J FROM tblJournal J JOIN @Old O ON O.iJournalID = J.iJournalID;

    DECLARE @Mode VARCHAR(20);
    SELECT TOP 1 @Mode = ISNULL(strAccountingMode, 'SingleEntry') FROM tblUsers WHERE iUserId = @iUserID;
    IF ISNULL(@Mode, 'SingleEntry') NOT IN ('Hybrid', 'DoubleEntry')
        RETURN;

    DECLARE @LoanID INT, @LoanName NVARCHAR(200), @LoanAccount INT, @Date DATE, @Kind VARCHAR(20), @Amount DECIMAL(19,4), @Comment NVARCHAR(500);
    SELECT @LoanID = L.iLoanID, @LoanName = L.strName, @LoanAccount = L.iAccountID, @Date = E.dtmDate, @Kind = E.strKind,
           @Amount = ROUND(CAST(E.dblAmount AS DECIMAL(19,4)), 2), @Comment = E.strComment
    FROM dbo.tblLoanEntries E JOIN dbo.tblLoans L ON L.iLoanID = E.iLoanID
    WHERE E.iLoanEntryID = @iLoanEntryID AND L.iUserID = @iUserID;
    IF @LoanID IS NULL OR ISNULL(@Amount, 0) = 0
        RETURN;

    -- The loan's own liability account, under 2300 Investment Loans
    IF @LoanAccount IS NULL OR NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @LoanAccount AND iUserID = @iUserID)
    BEGIN
        DECLARE @Parent INT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 2300, 'Investment Loans', 'Liability', @Parent OUTPUT;
        INSERT INTO tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, strCurrency, bIsPostable)
        SELECT @iUserID, @Parent, 2301, LEFT('Loan: ' + @LoanName, 255), 'Liability', ISNULL(NULLIF(strPortfolioCurrency, ''), 'AUD'), 1
        FROM tblUsers WHERE iUserId = @iUserID;
        SET @LoanAccount = SCOPE_IDENTITY();
        UPDATE dbo.tblLoans SET iAccountID = @LoanAccount WHERE iLoanID = @LoanID;
    END

    DECLARE @Other INT;
    IF @Kind IN ('Interest', 'Fee')
        EXEC dbo.uspGetLedgerAccount @iUserID, 5300, 'Investment Loan Interest', 'Expense', @Other OUTPUT;
    ELSE
    BEGIN
        SELECT TOP 1 @Other = iAccountID FROM tblChartOfAccounts
        WHERE (iUserID = @iUserID OR iUserID = 0) AND iAccountNumber >= 1100 AND iAccountNumber < 1200
        ORDER BY iUserID DESC, iAccountID;
        IF @Other IS NULL
            EXEC dbo.uspGetLedgerAccount @iUserID, 1100, 'Cash at Bank', 'Asset', @Other OUTPUT;
    END
    IF @Other IS NULL OR @LoanAccount IS NULL
        THROW 50001, 'The accounts for the loan journal could not be found.', 1;

    DECLARE @Text VARCHAR(500) = LEFT(@LoanName + ': ' + CASE @Kind
        WHEN 'Drawdown' THEN 'drawdown' WHEN 'Redraw' THEN 'redraw' WHEN 'Repayment' THEN 'repayment'
        WHEN 'Interest' THEN 'interest charged' ELSE 'fee charged' END + ISNULL(' (' + NULLIF(@Comment, '') + ')', ''), 500);
    DECLARE @iJournalID INT;
    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
    VALUES (@iUserID, @Date, @Text, 'LoanEntry', @iLoanEntryID);
    SET @iJournalID = SCOPE_IDENTITY();

    -- A repayment lowers the loan; everything else adds to it
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES
        (@iJournalID, CASE WHEN @Kind = 'Repayment' THEN @LoanAccount ELSE @Other END, @Amount, 0, @Text),
        (@iJournalID, CASE WHEN @Kind = 'Repayment' THEN @Other ELSE @LoanAccount END, 0, @Amount, @Text);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspPostInterestAccruals
    @iUserID       INT,
    @iYear         INT,             -- the financial year it ends
    @dtmYearEnd    DATE,
    @dblReceivable DECIMAL(19,4),   -- interest earned on fixed income but not yet paid at the year end
    @dblPayable    DECIMAL(19,4)    -- interest owed on loans but not yet charged at the year end
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        26-Oct-2026
-- Description: Replaces a year's interest accruals (at the year end) and their reversal (the next day). Returns Result.
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
    INSERT INTO @Old SELECT iJournalID FROM tblJournal WHERE iUserID = @iUserID AND strSourceModule = 'InterestAccrual' AND iSourceID = @iYear;
    DELETE L FROM tblJournalLines L JOIN @Old O ON O.iJournalID = L.iJournalID;
    DELETE J FROM tblJournal J JOIN @Old O ON O.iJournalID = J.iJournalID;

    SET @dblReceivable = ROUND(ISNULL(@dblReceivable, 0), 2);
    SET @dblPayable = ROUND(ISNULL(@dblPayable, 0), 2);
    IF @dblReceivable > 0 OR @dblPayable > 0
    BEGIN
        DECLARE @Receivable INT, @Income INT, @Payable INT, @Expense INT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 1450, 'Accrued Interest Receivable', 'Asset', @Receivable OUTPUT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 4100, 'Interest Income', 'Revenue', @Income OUTPUT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 2390, 'Accrued Interest Payable', 'Liability', @Payable OUTPUT;
        EXEC dbo.uspGetLedgerAccount @iUserID, 5300, 'Investment Loan Interest', 'Expense', @Expense OUTPUT;

        DECLARE @Years VARCHAR(9) = CAST(@iYear - 1 AS VARCHAR(4)) + '-' + RIGHT(CAST(@iYear AS VARCHAR(4)), 2);
        DECLARE @Lines TABLE (N INT, A INT, Amt DECIMAL(19,4), D VARCHAR(200));
        INSERT INTO @Lines VALUES
            (1, @Receivable, @dblReceivable, 'Interest earned, not yet paid'),
            (2, @Income, -@dblReceivable, 'Interest earned, not yet paid'),
            (3, @Expense, @dblPayable, 'Loan interest owed, not yet charged'),
            (4, @Payable, -@dblPayable, 'Loan interest owed, not yet charged');

        DECLARE @Accrual INT, @Reversal INT;
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
        VALUES (@iUserID, @dtmYearEnd, 'Interest accruals at the end of ' + @Years, 'InterestAccrual', @iYear);
        SET @Accrual = SCOPE_IDENTITY();
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
        VALUES (@iUserID, DATEADD(DAY, 1, @dtmYearEnd), 'Reversal of the interest accruals at the end of ' + @Years, 'InterestAccrual', @iYear);
        SET @Reversal = SCOPE_IDENTITY();

        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        SELECT @Accrual, A, CASE WHEN Amt > 0 THEN Amt ELSE 0 END, CASE WHEN Amt < 0 THEN -Amt ELSE 0 END, D FROM @Lines WHERE Amt <> 0 ORDER BY N;
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        SELECT @Reversal, A, CASE WHEN Amt < 0 THEN -Amt ELSE 0 END, CASE WHEN Amt > 0 THEN Amt ELSE 0 END, 'Reversed: ' + D FROM @Lines WHERE Amt <> 0 ORDER BY N;
    END
    COMMIT;
    SELECT 'Success' AS Result;
END
GO

-- Post the loan entries already recorded
DECLARE @User INT, @Id INT;
DECLARE entries CURSOR LOCAL FAST_FORWARD FOR
    SELECT L.iUserID, E.iLoanEntryID FROM dbo.tblLoanEntries E JOIN dbo.tblLoans L ON L.iLoanID = E.iLoanID
    WHERE NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iUserID = L.iUserID AND J.strSourceModule = 'LoanEntry' AND J.iSourceID = E.iLoanEntryID)
    ORDER BY E.dtmDate, E.iLoanEntryID;
OPEN entries;
FETCH NEXT FROM entries INTO @User, @Id;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC dbo.uspPostLoanEntryJournal @User, @Id;
    END TRY
    BEGIN CATCH
        -- An entry in a locked period is left as it was
        PRINT 'Loan entry ' + CAST(@Id AS VARCHAR(10)) + ' not posted: ' + ERROR_MESSAGE();
    END CATCH
    FETCH NEXT FROM entries INTO @User, @Id;
END
CLOSE entries;
DEALLOCATE entries;
GO

SET NOEXEC OFF;
GO
