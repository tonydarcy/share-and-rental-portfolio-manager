-- =============================================
-- Migration: deleting a cash transaction
-- Date:      28-Oct-2026
-- Adds uspDeleteCashTransaction, used by the API and MCP tools delete_cash_transaction and edit_cash_transaction
-- (an edit deletes the entry and adds it again with uspAddCashTransaction, in one transaction).
-- Nothing else changes. Safe to run twice.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteCashTransaction]
(
    @iUserId          INT,
    @iBankStatementId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description: Deletes a cash transaction (tblBankStatement) added by hand, with the journal uspAddCashTransaction
--              posted for it (BankTransaction), then recalculates that account's running balances.
--              Refused for a trade's settlement (change or delete the trade instead) and when a line of its
--              journal is ticked off on a bank reconciliation. Locked periods are refused by their triggers.
--              Returns one row: Result = 'Cash Transaction Deleted' or a message starting with 'Error:'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strBankAccountName VARCHAR(500), @strCurrency VARCHAR(50), @iShareTradeId INT, @bFound BIT = 0;

    SELECT @bFound = 1, @strBankAccountName = strBankAccountName, @strCurrency = strCurrency, @iShareTradeId = iShareTradeId
    FROM tblBankStatement
    WHERE iUserId = @iUserId AND iBankStatementId = @iBankStatementId;

    IF @bFound = 0
    BEGIN
        SELECT 'Error: That cash transaction was not found in this portfolio.' AS Result;
        RETURN;
    END

    IF ISNULL(@iShareTradeId, 0) <> 0
    BEGIN
        SELECT 'Error: This is the cash settlement of a trade. Change or delete the trade instead.' AS Result;
        RETURN;
    END

    IF OBJECT_ID('dbo.tblBankReconciliationLine', 'U') IS NOT NULL
       AND EXISTS (SELECT 1
                   FROM tblJournal J
                   JOIN tblJournalLines L ON L.iJournalID = J.iJournalID
                   JOIN tblBankReconciliationLine R ON R.iJournalLineID = L.iJournalLineID
                   WHERE J.iUserID = @iUserId AND J.strSourceModule = 'BankTransaction' AND J.iSourceID = @iBankStatementId)
    BEGIN
        SELECT 'Error: This cash transaction is ticked off on a bank reconciliation. Untick it there first.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    -- The journal's lines cascade with it
    DELETE FROM tblJournal
    WHERE iUserID = @iUserId AND strSourceModule = 'BankTransaction' AND iSourceID = @iBankStatementId;

    DELETE FROM tblBankStatement WHERE iUserId = @iUserId AND iBankStatementId = @iBankStatementId;

    ;WITH RunningBalances AS (
        SELECT
            dblClosingBalance AS CurrentStoredBalance,
            SUM(ISNULL(dblDepositAmount, 0) - ISNULL(dblWithdrawalAmount, 0)) OVER (
                PARTITION BY iUserId, strBankAccountName, strCurrency
                ORDER BY dtmDateOfTransaction ASC, iBankStatementId ASC
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ) AS CalculatedRunningBalance
        FROM tblBankStatement
        WHERE iUserId = @iUserId
          AND ISNULL(strBankAccountName, '') = ISNULL(@strBankAccountName, '')
          AND strCurrency = @strCurrency
    )
    UPDATE RunningBalances
    SET CurrentStoredBalance = CalculatedRunningBalance
    WHERE CurrentStoredBalance IS NULL OR CurrentStoredBalance <> CalculatedRunningBalance;

    COMMIT TRANSACTION;

    SELECT 'Cash Transaction Deleted' AS Result;
END
GO
