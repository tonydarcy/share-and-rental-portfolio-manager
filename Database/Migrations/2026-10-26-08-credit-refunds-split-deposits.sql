-- =============================================
-- Migration: credit note refunds and one deposit paying several invoices
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with credit note refunds and
-- split deposits. Run it after 2026-10-21-26-invoice-books.sql. Safe to run more than once.
--
-- What it changes:
--   * New: uspRefundCreditNote records money paid back to a customer against a credit note: Dr Accounts Receivable,
--     Cr the bank account. The refund is a row on the credit note, so what is left on it goes down.
--   * New: uspMatchInvoiceDepositSplit matches one imported bank deposit to several invoices. One journal moves the
--     whole deposit out of Bank Import Suspense against Accounts Receivable, and each invoice gets a payment row.
--   * uspDeleteInvoicePayment: deleting any payment of a split deposit deletes all of them and puts the deposit back
--     in Unallocated transactions (the deposit is matched as one).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF COL_LENGTH('dbo.tblInvoices', 'strKind') IS NULL OR COL_LENGTH('dbo.tblInvoices', 'strCurrency') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-17-invoicing-extras.sql and 2026-10-21-26-invoice-books.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspRefundCreditNote
    @iUserId       INT,
    @iCreditNoteID INT,
    @dtDate        DATE,
    @dblAmount     DECIMAL(19,4),
    @iAccountID    INT,                 -- the bank account the refund was paid from
    @strReference  VARCHAR(180) = NULL
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        26-Oct-2026
-- Description: Pays back what is left on an approved credit note: Dr Accounts
--              Receivable, Cr the bank account. Returns Result ('Success' or
--              'Error: ...') and iPaymentID.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @dblTotal DECIMAL(19,4), @strNumber VARCHAR(40), @strCustomer VARCHAR(200), @strCurrency CHAR(3), @bOk BIT = 0;
    SELECT @dblTotal = I.dblTotal, @strNumber = I.strInvoiceNumber, @strCustomer = C.strName, @strCurrency = I.strCurrency, @bOk = 1
    FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iCreditNoteID AND I.iUserID = @iUserId AND I.strKind = 'CreditNote' AND I.strStatus = 'Approved';
    DECLARE @dblLeft DECIMAL(19,4) = @dblTotal - ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iCreditNoteID), 0);
    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @bOk = 0 THEN 'Only an approved credit note can be refunded.'
        WHEN @strCurrency IS NOT NULL THEN 'Credit notes in another currency can''t be refunded yet.'
        WHEN @dtDate IS NULL THEN 'Enter the date the refund was paid.'
        WHEN ISNULL(@dblAmount, 0) <= 0 THEN 'Enter the amount refunded.'
        WHEN @dblAmount > @dblLeft THEN 'That is more than is left on the credit note.'
        WHEN @iAccountID = @iReceivableID OR NOT EXISTS (
                SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                  AND strAccountType IN ('Asset', 'Liability') AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the bank account the refund was paid from.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iPaymentID;
        RETURN;
    END

    DECLARE @strDescription VARCHAR(500) = LEFT('Refund of credit note ' + @strNumber + ' - ' + @strCustomer, 500);
    DECLARE @strRef VARCHAR(200) = 'Refund' + ISNULL(' (' + NULLIF(LTRIM(RTRIM(@strReference)), '') + ')', '');

    BEGIN TRANSACTION;
    INSERT INTO tblInvoicePayments (iInvoiceID, dtmPaymentDate, dblAmount, iAccountID, strReference)
    VALUES (@iCreditNoteID, @dtDate, @dblAmount, @iAccountID, @strRef);
    DECLARE @iPaymentID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'InvoicePayment', @iPaymentID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iReceivableID, @dblAmount, 0, @strDescription),
           (@iJournalID, @iAccountID, 0, @dblAmount, @strDescription);
    UPDATE tblInvoicePayments SET iJournalID = @iJournalID WHERE iPaymentID = @iPaymentID;
    COMMIT TRANSACTION;

    SELECT 'Success' AS Result, @iPaymentID AS iPaymentID;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspMatchInvoiceDepositSplit
    @iUserId         INT,
    @iSuspenseLineID INT,
    @xmlPayments     XML       -- <p><a i="invoice id" v="amount"/>...</p>
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        26-Oct-2026
-- Description: Matches one imported bank deposit to several approved invoices
--              in the portfolio's currency. The amounts must add up to the
--              deposit and none may be more than is owing. One journal clears
--              suspense against Accounts Receivable; each invoice gets a
--              payment row sharing it. Returns Result and iPaymentID (the first).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iSuspenseID INT, @dblDeposit DECIMAL(19,4), @dtDate DATE, @iMatched INT, @iImportJournal INT, @iBankID INT;
    SELECT @iSuspenseID = JL.iAccountID, @dblDeposit = ISNULL(JL.dblCredit, 0) - ISNULL(JL.dblDebit, 0), @dtDate = J.dtmTransactionDate,
           @iMatched = JL.iMatchedJournalID, @iImportJournal = J.iJournalID
    FROM tblJournalLines JL
    JOIN tblJournal J ON J.iJournalID = JL.iJournalID
    JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
    WHERE JL.iJournalLineID = @iSuspenseLineID AND J.iUserID = @iUserId AND J.strSourceModule = 'BankImport'
      AND CA.strAccountName = 'Bank Import Suspense';
    SELECT TOP 1 @iBankID = iAccountID FROM tblJournalLines WHERE iJournalID = @iImportJournal AND iJournalLineID <> @iSuspenseLineID;

    DECLARE @Pay TABLE (iInvoiceID INT PRIMARY KEY, dblAmount DECIMAL(19,4), strNumber VARCHAR(40), strCustomer VARCHAR(200),
        bOk BIT, dblOwing DECIMAL(19,4));
    INSERT @Pay (iInvoiceID, dblAmount)
    SELECT R.iInvoiceID, SUM(R.dblAmount)
    FROM (SELECT X.a.value('@i', 'INT') AS iInvoiceID, X.a.value('@v', 'DECIMAL(19,4)') AS dblAmount
          FROM @xmlPayments.nodes('/p/a') AS X(a)) R
    GROUP BY R.iInvoiceID
    HAVING SUM(R.dblAmount) <> 0;

    UPDATE P SET strNumber = I.strInvoiceNumber, strCustomer = C.strName,
           bOk = CASE WHEN I.strKind = 'Invoice' AND I.strStatus = 'Approved' AND I.strCurrency IS NULL THEN 1 ELSE 0 END,
           dblOwing = I.dblTotal - ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = I.iInvoiceID), 0)
    FROM @Pay P JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID AND I.iUserID = @iUserId
    JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @iSuspenseID IS NULL THEN 'That bank transaction wasn''t found.'
        WHEN @iMatched IS NOT NULL THEN 'That bank transaction has already been allocated.'
        WHEN @dblDeposit <= 0 THEN 'Only a deposit can pay an invoice.'
        WHEN NOT EXISTS (SELECT 1 FROM @Pay) THEN 'Enter an amount against at least one invoice.'
        WHEN EXISTS (SELECT 1 FROM @Pay WHERE dblAmount < 0) THEN 'Amounts can''t be negative.'
        WHEN EXISTS (SELECT 1 FROM @Pay WHERE ISNULL(bOk, 0) = 0) THEN 'Only approved invoices in the portfolio''s currency can be paid this way.'
        WHEN EXISTS (SELECT 1 FROM @Pay WHERE dblAmount > dblOwing) THEN 'One of the amounts is more than is owing on its invoice.'
        WHEN (SELECT SUM(dblAmount) FROM @Pay) <> @dblDeposit THEN 'The amounts must add up to the whole deposit.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iPaymentID;
        RETURN;
    END

    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;
    DECLARE @strDescription VARCHAR(500) = LEFT('Payment for invoices ' + STUFF((SELECT ', ' + strNumber FROM @Pay ORDER BY strNumber
        FOR XML PATH(''), TYPE).value('.', 'VARCHAR(MAX)'), 1, 2, ''), 500);

    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM tblJournalLines WITH (UPDLOCK, HOLDLOCK) WHERE iJournalLineID = @iSuspenseLineID AND iMatchedJournalID IS NOT NULL)
        THROW 50111, 'This transaction has already been allocated.', 1;

    INSERT INTO tblInvoicePayments (iInvoiceID, dtmPaymentDate, dblAmount, iAccountID, strReference, iSuspenseLineID)
    SELECT iInvoiceID, @dtDate, dblAmount, ISNULL(@iBankID, @iSuspenseID), 'Bank import (split deposit)', @iSuspenseLineID
    FROM @Pay;
    DECLARE @iFirst INT = (SELECT MIN(iPaymentID) FROM tblInvoicePayments WHERE iSuspenseLineID = @iSuspenseLineID AND iJournalID IS NULL);

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'InvoicePayment', @iFirst, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iSuspenseID, @dblDeposit, 0, @strDescription);
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    SELECT @iJournalID, @iReceivableID, 0, dblAmount, LEFT('Payment for invoice ' + strNumber + ' - ' + strCustomer, 500) FROM @Pay;

    UPDATE tblJournalLines SET iMatchedJournalID = @iJournalID WHERE iJournalLineID = @iSuspenseLineID;
    UPDATE tblInvoicePayments SET iJournalID = @iJournalID WHERE iSuspenseLineID = @iSuspenseLineID AND iJournalID IS NULL;
    COMMIT TRANSACTION;

    SELECT 'Success' AS Result, @iFirst AS iPaymentID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteInvoicePayment]
(
    @iUserId    INT,
    @iPaymentID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Deletes a payment and its journal. Returns Result
--              ('Payment Deleted' or 'Error: ...').
-- Date:        21-Oct-2026
-- Description: An applied credit note's two rows go together; a payment
--              matched to an imported bank line puts the line back in
--              Unallocated transactions.
-- Date:        26-Oct-2026
-- Description: The payments of a split deposit share one journal, so they
--              are all deleted together.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @iPair INT, @bFound BIT = 0;
    SELECT @iJournalID = P.iJournalID, @iPair = P.iPairPaymentID, @bFound = 1
    FROM tblInvoicePayments P JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID
    WHERE P.iPaymentID = @iPaymentID AND I.iUserID = @iUserId;

    IF @bFound = 0
    BEGIN
        SELECT 'Error: That payment was not found.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF @iJournalID IS NOT NULL
    BEGIN
        UPDATE JL SET iMatchedJournalID = NULL
        FROM tblJournalLines JL JOIN tblJournal J ON J.iJournalID = JL.iJournalID
        WHERE JL.iMatchedJournalID = @iJournalID AND J.iUserID = @iUserId;
        DELETE P FROM tblInvoicePayments P JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID
        WHERE P.iJournalID = @iJournalID AND I.iUserID = @iUserId;
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    END
    DELETE FROM tblInvoicePayments WHERE iPaymentID IN (@iPaymentID, @iPair) OR iPairPaymentID = @iPaymentID;
    COMMIT TRANSACTION;

    SELECT 'Payment Deleted' AS Result;
END
GO

PRINT 'Credit note refunds and split deposits migration done.';
GO
