-- =============================================
-- Migration: Loans read from ledger accounts (07-Nov-2026).
-- Run it after 2026-10-26-07-loans-in-ledger.sql. Safe to run more than once.
--   * tblLoans.iInterestAccountID: the expense account the loan's interest goes to (optional).
--   * uspSetLoanAccounts: picks the liability account a loan's balance is read from (tblLoans.iAccountID, which the
--     loan's own entries already post to) and its interest account. The loan's entries are re-posted by the site
--     afterwards, so they move to the picked account.
-- =============================================
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('dbo.tblLoans', 'iAccountID') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-26-07-loans-in-ledger.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF COL_LENGTH('dbo.tblLoans', 'iInterestAccountID') IS NULL
    ALTER TABLE dbo.tblLoans ADD iInterestAccountID INT NULL;
GO

CREATE OR ALTER PROCEDURE dbo.uspSetLoanAccounts
    @iUserID            INT,
    @iLoanID            INT,
    @iAccountID         INT = NULL, -- a liability account of this portfolio; NULL lets the loan's entries make their own
    @iInterestAccountID INT = NULL  -- an expense account; NULL for none
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        07-Nov-2026
-- Description: Sets the ledger account a loan's balance comes from and its interest account. Returns Result.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLoans WHERE iLoanID = @iLoanID AND iUserID = @iUserID)
    BEGIN
        SELECT 'Error: That loan isn''t in this portfolio.' AS Result;
        RETURN;
    END
    IF @iAccountID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.tblChartOfAccounts
        WHERE iAccountID = @iAccountID AND iUserID = @iUserID AND strAccountType = 'Liability' AND ISNULL(bIsPostable, 1) = 1)
    BEGIN
        SELECT 'Error: Choose one of this portfolio''s own liability accounts for the loan.' AS Result;
        RETURN;
    END
    IF @iInterestAccountID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.tblChartOfAccounts
        WHERE iAccountID = @iInterestAccountID AND (iUserID = @iUserID OR iUserID = 0) AND strAccountType = 'Expense' AND ISNULL(bIsPostable, 1) = 1)
    BEGIN
        SELECT 'Error: Choose an expense account for the loan''s interest.' AS Result;
        RETURN;
    END
    IF @iAccountID IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.tblLoans WITH (UPDLOCK, HOLDLOCK) WHERE iUserID = @iUserID AND iLoanID <> @iLoanID AND iAccountID = @iAccountID)
    BEGIN
        SELECT 'Error: Another loan already uses that ledger account.' AS Result;
        RETURN;
    END
    IF @iInterestAccountID IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.tblLoans WITH (UPDLOCK, HOLDLOCK) WHERE iUserID = @iUserID AND iLoanID <> @iLoanID AND iInterestAccountID = @iInterestAccountID)
    BEGIN
        SELECT 'Error: Another loan already uses that interest account. Give each loan an interest account of its own.' AS Result;
        RETURN;
    END
    -- The checks above hold their locks until the caller (LoanService.SetAccountsAsync) commits, so two edits can't both link one account
    UPDATE dbo.tblLoans SET iAccountID = @iAccountID, iInterestAccountID = @iInterestAccountID WHERE iLoanID = @iLoanID AND iUserID = @iUserID;
    SELECT 'Success' AS Result;
END
GO

SET NOEXEC OFF;
GO
