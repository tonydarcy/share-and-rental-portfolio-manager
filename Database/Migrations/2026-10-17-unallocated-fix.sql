-- =============================================
-- Migration: allocated bank lines no longer come back as unallocated
-- Date:      17-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying. Safe to run more
-- than once.
--
-- The bug: allocating a bank line posts a journal whose first line takes the amount back out
-- of Bank Import Suspense. uspGetUnallocatedTransactions listed every unmatched suspense line,
-- so that clearing line showed on the Unallocated page as a new transaction, and Apply rules
-- could allocate it again, undoing the first allocation. Only lines of imported statements
-- (strSourceModule 'BankImport') are bank transactions waiting to be allocated.
--
-- What it changes:
--   * uspGetUnallocatedTransactions: only lines of imported statements.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetUnallocatedTransactions]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Imported bank lines still in Bank Import Suspense. Only lines
--              of imported statements: an allocation journal's own suspense
--              line is not a transaction to allocate.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    -- Find the specific "Bank Import Suspense" account for this user (or system default)
    DECLARE @SuspenseAccID INT;
    SELECT TOP 1 @SuspenseAccID = iAccountID
    FROM tblChartOfAccounts
    WHERE strAccountName = 'Bank Import Suspense' AND (iUserID = @iUserId OR iUserID = 0)
    ORDER BY iUserID DESC, iAccountID;

    -- Select lines that are in Suspense and NOT yet matched
    SELECT
        JL.iJournalLineID,
        J.dtmTransactionDate,
        J.strDescription,
        -- Calculate Net Amount (Positive = Deposit/Inflow, Negative = Withdrawal/Outflow)
        (JL.dblCredit - JL.dblDebit) AS Amount,
        JL.iMatchedJournalID
    FROM tblJournalLines JL
    INNER JOIN tblJournal J ON JL.iJournalID = J.iJournalID
    WHERE JL.iAccountID = @SuspenseAccID
      AND J.iUserID = @iUserId
      AND J.strSourceModule = 'BankImport'
      AND JL.iMatchedJournalID IS NULL -- Only show unfinished items
    ORDER BY J.dtmTransactionDate ASC;
END
GO
