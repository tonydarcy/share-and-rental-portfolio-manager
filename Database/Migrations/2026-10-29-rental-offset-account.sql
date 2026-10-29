-- =============================================
-- Migration: Non-cash rental entries (29-Oct-2026).
-- uspRepostPayoutJournal takes an optional account for the payout's "cash" side. The connector's add_rental_entry
-- uses it for entries no cash moved for, such as depreciation (against accumulated depreciation) or interest
-- charged straight to the loan, so the journal doesn't go through the bank. Without the account it works as before.
-- Safe to run twice.
-- =============================================
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[uspRepostPayoutJournal]
(
    @iUserId          INT,
    @iPayoutID        INT,
    @iTargetAccountID INT = NULL -- the account for the cash side; NULL keeps the one the journal already uses
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: After a payout is edited, rebuilds its journal lines from the
--              saved row. Keeps the journal id and the account the cash went
--              to (bank, or the import suspense account it was allocated from).
--              A payout without a journal (single entry, DRP) is left alone.
--              Returns no result set.
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: @iTargetAccountID moves the cash side to another account of the
--              portfolio (uspPostPayoutJournal checks it belongs to it).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT = (
        SELECT MIN(iJournalID) FROM tblJournal
        WHERE iUserID = @iUserId AND strSourceModule = 'Payout' AND iSourceID = @iPayoutID);
    IF @iJournalID IS NULL
        RETURN;

    IF @iTargetAccountID IS NULL
    BEGIN
        -- The cash line: marked by description since the ledger-fixes migration,
        -- otherwise the first balance sheet line that isn't a tax or franking credit
        SELECT TOP 1 @iTargetAccountID = JL.iAccountID
        FROM tblJournal J
        JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
        WHERE J.iUserID = @iUserId AND J.strSourceModule = 'Payout' AND J.iSourceID = @iPayoutID
          AND JL.strDescription IN ('Cash received', 'Cash paid')
        ORDER BY J.iJournalID, JL.iJournalLineID;

        IF @iTargetAccountID IS NULL
            SELECT TOP 1 @iTargetAccountID = JL.iAccountID
            FROM tblJournal J
            JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
            JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
            WHERE J.iUserID = @iUserId AND J.strSourceModule = 'Payout' AND J.iSourceID = @iPayoutID
              AND CA.strAccountType NOT IN ('Revenue', 'Expense')
              AND CA.iAccountNumber NOT IN (1400, 1410, 1420)
            ORDER BY J.iJournalID, JL.iJournalLineID;
    END

    BEGIN TRANSACTION;

    -- Lines cascade with their journal
    DELETE FROM tblJournal
    WHERE iUserID = @iUserId AND strSourceModule = 'Payout' AND iSourceID = @iPayoutID AND iJournalID <> @iJournalID;

    DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID;

    EXEC dbo.uspPostPayoutJournal @iUserId, @iPayoutID, @iTargetAccountID, @iJournalID;

    COMMIT TRANSACTION;
END
GO
