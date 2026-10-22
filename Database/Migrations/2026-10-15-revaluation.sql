-- =============================================
-- Migration: year-end market value revaluation
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database AFTER 2026-10-15-realised-gains.sql
-- and BEFORE deploying the site build with Accounting > Market value revaluation.
-- The old site keeps working: it only adds accounts and procedures.
-- Safe to run more than once.
--
-- What it changes:
--   * Adds system accounts (stops first if a number is used for another type):
--       1390 Investments - Market Value Adjustment   (Asset)
--       4600 Unrealised Gains on Investments         (Revenue)
--       2850 Deferred Income Tax Liability           (Liability)
--       5950 Income Tax Expense - Deferred           (Expense)
--   * uspStartRevaluation (new): replaces a portfolio's revaluation journal for
--     a date (strSourceModule 'Revaluation') with an empty one. The site then
--     adds one line per holding to 1390 with uspAddJournalLine: market value
--     less the cost in the ledger.
--   * uspFinishRevaluation (new): reverses the 1390 balance before the date,
--     posts the net movement to 4600 and, when a tax rate is given, the
--     deferred tax on the movement (5950 against 2850). Refuses an unbalanced
--     journal.
--   * uspDeleteRevaluation (new): removes the revaluation journal for a date.
--   Both refuse while a later revaluation exists, since it reverses this one.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- 1. Pre-flight
-- =============================================
IF OBJECT_ID('dbo.uspGetLedgerAccount', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-ledger-fixes.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF EXISTS (
    SELECT 1 FROM tblChartOfAccounts
    WHERE iUserID = 0 AND (
           (iAccountNumber = 1390 AND strAccountType <> 'Asset')
        OR (iAccountNumber = 4600 AND strAccountType <> 'Revenue')
        OR (iAccountNumber = 2850 AND strAccountType <> 'Liability')
        OR (iAccountNumber = 5950 AND strAccountType <> 'Expense')))
BEGIN
    SELECT iAccountID, iAccountNumber, strAccountName, strAccountType
    FROM tblChartOfAccounts WHERE iUserID = 0 AND iAccountNumber IN (1390, 4600, 2850, 5950);
    RAISERROR('Stopped: one of the account numbers 1390, 4600, 2850 or 5950 is already used for a different type of account (listed above). Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =============================================
-- 2. The accounts
-- =============================================
DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 1390, 'Investments - Market Value Adjustment', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4600, 'Unrealised Gains on Investments', 'Revenue', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 2850, 'Deferred Income Tax Liability', 'Liability', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 5950, 'Income Tax Expense - Deferred', 'Expense', @iAccountID OUTPUT;
SELECT iAccountID, iAccountNumber, strAccountName, strAccountType
FROM tblChartOfAccounts WHERE iUserID = 0 AND iAccountNumber IN (1390, 4600, 2850, 5950);
GO

-- =============================================
-- 3. Starting a revaluation journal
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspStartRevaluation]
(
    @iUserId        INT,
    @dtDate         DATE,
    @strDescription VARCHAR(500)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Deletes the portfolio's revaluation journal on this date, if
--              any, and adds an empty one. Call it inside the caller's
--              transaction, then add the holding lines with uspAddJournalLine
--              and finish with uspFinishRevaluation.
--              Returns one row: JournalId, AdjustmentAccountId (1390).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- A later revaluation reverses this one's adjustment, so it would go stale
    IF EXISTS (SELECT 1 FROM tblJournal WHERE iUserID = @iUserId AND strSourceModule = 'Revaluation' AND dtmTransactionDate > @dtDate)
        THROW 50204, 'There is a later revaluation. Remove it first, then post this one and post the later one again.', 1;

    DECLARE @iAdjustmentAccountID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1390, 'Investments - Market Value Adjustment', 'Asset', @iAdjustmentAccountID OUTPUT;

    DELETE L FROM tblJournalLines L
    JOIN tblJournal J ON J.iJournalID = L.iJournalID
    WHERE J.iUserID = @iUserId AND J.strSourceModule = 'Revaluation' AND J.dtmTransactionDate = @dtDate;

    DELETE FROM tblJournal
    WHERE iUserID = @iUserId AND strSourceModule = 'Revaluation' AND dtmTransactionDate = @dtDate;

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'Revaluation', GETDATE());

    SELECT CAST(SCOPE_IDENTITY() AS INT) AS JournalId, @iAdjustmentAccountID AS AdjustmentAccountId;
END
GO

-- =============================================
-- 4. Finishing it: reversal, movement and deferred tax
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspFinishRevaluation]
(
    @iUserId    INT,
    @iJournalID INT,
    @dblTaxRate DECIMAL(9,6) = 0   -- e.g. 0.30; 0 posts no deferred tax
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Completes a revaluation journal whose holding lines (each
--              holding's market value less cost, on 1390) are already added:
--                1390  reverses the adjustment already in the ledger before
--                      this journal (earlier revaluations)
--                4600  the net movement (credit for a gain, debit for a fall)
--                5950 / 2850  deferred tax on the movement at @dblTaxRate
--              Errors are thrown, so the caller's transaction rolls back.
--              Returns one row: Movement, DeferredTax.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @dtDate DATE;
    SELECT @dtDate = dtmTransactionDate FROM tblJournal
    WHERE iJournalID = @iJournalID AND iUserID = @iUserId AND strSourceModule = 'Revaluation';

    IF @dtDate IS NULL
        THROW 50201, 'That revaluation journal was not found in this portfolio.', 1;

    IF ISNULL(@dblTaxRate, 0) < 0 OR @dblTaxRate >= 1
        THROW 50202, 'The tax rate must be between 0% and 100%.', 1;

    DECLARE @iAdj INT, @iGain INT, @iDtl INT, @iTax INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1390, 'Investments - Market Value Adjustment', 'Asset', @iAdj OUTPUT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 4600, 'Unrealised Gains on Investments', 'Revenue', @iGain OUTPUT;

    -- What earlier revaluations left in 1390 at this date
    DECLARE @Prior DECIMAL(19,4) = (
        SELECT ISNULL(SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0)), 0)
        FROM tblJournalLines L
        JOIN tblJournal J ON J.iJournalID = L.iJournalID
        WHERE J.iUserID = @iUserId AND J.iJournalID <> @iJournalID
          AND J.dtmTransactionDate <= @dtDate AND L.iAccountID = @iAdj);

    IF @Prior <> 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iAdj,
                CASE WHEN @Prior < 0 THEN -@Prior ELSE 0 END,
                CASE WHEN @Prior > 0 THEN @Prior ELSE 0 END,
                'Reverse the market value adjustment already recorded');

    DECLARE @Movement DECIMAL(19,4) = (
        SELECT ISNULL(SUM(ISNULL(dblDebit, 0) - ISNULL(dblCredit, 0)), 0)
        FROM tblJournalLines WHERE iJournalID = @iJournalID);

    IF @Movement <> 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iGain,
                CASE WHEN @Movement < 0 THEN -@Movement ELSE 0 END,
                CASE WHEN @Movement > 0 THEN @Movement ELSE 0 END,
                'Movement in market value of investments');

    DECLARE @Tax DECIMAL(19,4) = ROUND(@Movement * ISNULL(@dblTaxRate, 0), 2);
    IF @Tax <> 0
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 2850, 'Deferred Income Tax Liability', 'Liability', @iDtl OUTPUT;
        EXEC dbo.uspGetLedgerAccount @iUserId, 5950, 'Income Tax Expense - Deferred', 'Expense', @iTax OUTPUT;
        DECLARE @Text VARCHAR(500) = 'Deferred tax on the movement at ' + FORMAT(@dblTaxRate * 100, '0.##') + '%';

        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iTax, CASE WHEN @Tax > 0 THEN @Tax ELSE 0 END, CASE WHEN @Tax < 0 THEN -@Tax ELSE 0 END, @Text);
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iDtl, CASE WHEN @Tax < 0 THEN -@Tax ELSE 0 END, CASE WHEN @Tax > 0 THEN @Tax ELSE 0 END, @Text);
    END

    IF EXISTS (SELECT 1 FROM tblJournalLines WHERE iJournalID = @iJournalID
               HAVING SUM(ISNULL(dblDebit, 0)) <> SUM(ISNULL(dblCredit, 0)))
        THROW 50203, 'The revaluation journal does not balance, so it was not saved.', 1;

    SELECT @Movement AS Movement, @Tax AS DeferredTax;
END
GO

-- =============================================
-- 5. Removing a revaluation
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspDeleteRevaluation]
(
    @iUserId INT,
    @dtDate  DATE
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Deletes the portfolio's revaluation journal on this date.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblJournal WHERE iUserID = @iUserId AND strSourceModule = 'Revaluation' AND dtmTransactionDate = @dtDate)
    BEGIN
        SELECT 'Error: There is no revaluation on that date.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblJournal WHERE iUserID = @iUserId AND strSourceModule = 'Revaluation' AND dtmTransactionDate > @dtDate)
    BEGIN
        SELECT 'Error: There is a later revaluation, which reverses this one. Remove the later one first.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    DELETE L FROM tblJournalLines L
    JOIN tblJournal J ON J.iJournalID = L.iJournalID
    WHERE J.iUserID = @iUserId AND J.strSourceModule = 'Revaluation' AND J.dtmTransactionDate = @dtDate;

    DELETE FROM tblJournal
    WHERE iUserID = @iUserId AND strSourceModule = 'Revaluation' AND dtmTransactionDate = @dtDate;

    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO
