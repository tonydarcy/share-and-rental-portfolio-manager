-- =============================================
-- Migration: invoicing extras
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with credit notes, repeating
-- invoices, matching bank deposits to invoices, customer statements, aged receivables and a logo on invoices.
-- Run it after 2026-10-18-invoicing.sql and 2026-10-21-15-attachments-audit.sql. Safe to run more than once.
--
-- What it changes:
--   * tblInvoices gets strKind (Invoice or CreditNote) and iCreditedInvoiceID (the invoice a credit note is for).
--   * tblInvoicePayments gets iCreditNoteID (a credit note applied instead of money), iPairPaymentID (the matching
--     row on the credit note) and iSuspenseLineID (the imported bank line it was matched to).
--   * tblInvoiceSettings gets binLogo and strLogoType.
--   * tblRepeatingInvoices: an invoice's lines kept with a schedule.
--   * uspPostInvoiceJournal posts a credit note the other way round (Dr income and tax, Cr Accounts Receivable).
--   * uspAddInvoicePayment refuses a payment on a credit note.
--   * uspDeleteInvoicePayment also removes the credit note's side of an applied credit, and puts a matched bank
--     line back in Unallocated transactions.
--   * New: uspSetInvoiceKind, uspMatchInvoiceDeposit, uspApplyCreditNote, uspSaveInvoiceLogo,
--     uspSaveRepeatingInvoice, uspMarkRepeatingInvoice, uspSetRepeatingInvoiceActive, uspDeleteRepeatingInvoice.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblInvoices', 'U') IS NULL OR OBJECT_ID('dbo.tblAuditLog', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-18-invoicing.sql and 2026-10-21-15-attachments-audit.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF COL_LENGTH('dbo.tblInvoices', 'strKind') IS NULL
    ALTER TABLE dbo.tblInvoices ADD strKind VARCHAR(10) NOT NULL CONSTRAINT DF_tblInvoices_Kind DEFAULT ('Invoice'),
        iCreditedInvoiceID INT NULL;
GO
IF COL_LENGTH('dbo.tblInvoicePayments', 'iCreditNoteID') IS NULL
    ALTER TABLE dbo.tblInvoicePayments ADD iCreditNoteID INT NULL, iPairPaymentID INT NULL, iSuspenseLineID INT NULL;
GO
IF COL_LENGTH('dbo.tblInvoiceSettings', 'binLogo') IS NULL
    ALTER TABLE dbo.tblInvoiceSettings ADD binLogo VARBINARY(MAX) NULL, strLogoType VARCHAR(50) NULL;
GO

IF OBJECT_ID('dbo.tblRepeatingInvoices', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRepeatingInvoices (
        iRepeatingInvoiceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRepeatingInvoices PRIMARY KEY,
        iUserID             INT NOT NULL,
        iCustomerID         INT NOT NULL,
        strName             NVARCHAR(200) NOT NULL,
        strFrequency        VARCHAR(20) NOT NULL,       -- Weekly, Fortnightly, Monthly, Quarterly, HalfYearly, Yearly
        dtmStart            DATE NOT NULL,
        dtmEnd              DATE NULL,
        iPostedCount        INT NOT NULL CONSTRAINT DF_tblRepeatingInvoices_Posted DEFAULT (0),
        iDueDays            INT NOT NULL CONSTRAINT DF_tblRepeatingInvoices_Due DEFAULT (14),
        bApprove            BIT NOT NULL CONSTRAINT DF_tblRepeatingInvoices_Approve DEFAULT (0),   -- 0 leaves each one as a draft
        bAmountsIncludeTax  BIT NOT NULL CONSTRAINT DF_tblRepeatingInvoices_Incl DEFAULT (0),
        strReference        VARCHAR(200) NULL,
        strNotes            VARCHAR(1000) NULL,
        strLinesXml         NVARCHAR(MAX) NOT NULL,     -- <lines><l d="description" q="quantity" p="price" a="account" t="tax code"/></lines>
        bActive             BIT NOT NULL CONSTRAINT DF_tblRepeatingInvoices_Active DEFAULT (1),
        dtmLastCreated      DATE NULL
    );
    CREATE INDEX IX_tblRepeatingInvoices_User ON dbo.tblRepeatingInvoices (iUserID);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspPostInvoiceJournal]
(
    @iUserId    INT,
    @iInvoiceID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Posts (or reposts) an approved invoice's journal from its saved
--              lines: Dr Accounts Receivable for the total, Cr each line's
--              account, with its tax split to the tax control account. A
--              negative line (a discount) is a debit. Errors are thrown, so the
--              caller's transaction rolls everything back.
-- Date:        21-Oct-2026
-- Description: A credit note posts the other way round: Cr Accounts
--              Receivable, Dr each line's account and its tax.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @dtDate DATE, @dblTotal DECIMAL(19,4), @strDescription VARCHAR(500), @strStatus VARCHAR(10), @bCredit BIT;
    SELECT @iJournalID = I.iJournalID, @dtDate = I.dtmIssueDate, @dblTotal = I.dblTotal, @strStatus = I.strStatus,
           @bCredit = CASE WHEN I.strKind = 'CreditNote' THEN 1 ELSE 0 END,
           @strDescription = LEFT(CASE WHEN I.strKind = 'CreditNote' THEN 'Credit note ' ELSE 'Invoice ' END + I.strInvoiceNumber + ' - ' + C.strName, 500)
    FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;

    IF @strStatus IS NULL
        THROW 50501, 'That invoice was not found in this portfolio.', 1;
    IF @strStatus <> 'Approved'
        THROW 50502, 'Only an approved invoice is posted to the ledger.', 1;

    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;

    IF @iJournalID IS NOT NULL AND EXISTS (SELECT 1 FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId)
    BEGIN
        UPDATE tblJournal SET dtmTransactionDate = @dtDate, strDescription = @strDescription WHERE iJournalID = @iJournalID;
        DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID;
    END
    ELSE
    BEGIN
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
        VALUES (@iUserId, @dtDate, @strDescription, 'Invoice', @iInvoiceID, GETDATE());
        SET @iJournalID = SCOPE_IDENTITY();
        UPDATE tblInvoices SET iJournalID = @iJournalID WHERE iInvoiceID = @iInvoiceID;
    END

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iReceivableID, CASE WHEN @bCredit = 1 THEN 0 ELSE @dblTotal END, CASE WHEN @bCredit = 1 THEN @dblTotal ELSE 0 END, @strDescription);

    DECLARE @iAccountID INT, @iTaxCodeID INT, @dblGross DECIMAL(19,4), @dblTax DECIMAL(19,4), @strLine VARCHAR(500);
    DECLARE @dblDebit DECIMAL(19,4), @dblCredit DECIMAL(19,4);
    DECLARE curLines CURSOR LOCAL FAST_FORWARD FOR
        SELECT iAccountID, iTaxCodeID, dblTotal, dblTax, strDescription
        FROM tblInvoiceLines WHERE iInvoiceID = @iInvoiceID AND dblTotal <> 0 ORDER BY iLineNumber;
    OPEN curLines;
    FETCH NEXT FROM curLines INTO @iAccountID, @iTaxCodeID, @dblGross, @dblTax, @strLine;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @bCredit = 1
            SET @dblGross = -@dblGross;
        SET @dblCredit = CASE WHEN @dblGross > 0 THEN @dblGross ELSE 0 END;
        SET @dblDebit = CASE WHEN @dblGross < 0 THEN -@dblGross ELSE 0 END;
        IF @iTaxCodeID IS NOT NULL
        BEGIN
            SET @dblTax = ABS(@dblTax);
            EXEC dbo.uspAddJournalLineWithTax @iUserId, @iJournalID, @iAccountID, @dblDebit, @dblCredit, @strLine, @iTaxCodeID, @dblTax;
        END
        ELSE
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
            VALUES (@iJournalID, @iAccountID, @dblDebit, @dblCredit, @strLine);
        FETCH NEXT FROM curLines INTO @iAccountID, @iTaxCodeID, @dblGross, @dblTax, @strLine;
    END
    CLOSE curLines;
    DEALLOCATE curLines;

    IF (SELECT SUM(dblDebit) - SUM(dblCredit) FROM tblJournalLines WHERE iJournalID = @iJournalID) <> 0
        THROW 50503, 'The invoice journal did not balance, so nothing was saved.', 1;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddInvoicePayment]
(
    @iUserId      INT,
    @iInvoiceID   INT,
    @dtDate       DATE,
    @dblAmount    DECIMAL(19,4),
    @iAccountID   INT,              -- the bank account it was paid into
    @strReference VARCHAR(200) = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Records money received for an approved invoice and posts it:
--              Dr the bank account, Cr Accounts Receivable. Returns Result
--              ('Success' or 'Error: ...') and iPaymentID.
-- Date:        21-Oct-2026
-- Description: Refuses a credit note (apply it to an invoice instead).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strStatus VARCHAR(10), @dblTotal DECIMAL(19,4), @strNumber VARCHAR(40), @strCustomer VARCHAR(200), @strKind VARCHAR(10);
    SELECT @strStatus = I.strStatus, @dblTotal = I.dblTotal, @strNumber = I.strInvoiceNumber, @strCustomer = C.strName, @strKind = I.strKind
    FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;

    DECLARE @dblPaid DECIMAL(19,4) = ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID), 0);
    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @strStatus IS NULL THEN 'That invoice was not found in this portfolio.'
        WHEN @strKind = 'CreditNote' THEN 'A credit note isn''t paid. Apply it to one of the customer''s invoices instead.'
        WHEN @strStatus <> 'Approved' THEN 'Only an approved invoice can be paid.'
        WHEN @dtDate IS NULL THEN 'Enter the date it was paid.'
        WHEN ISNULL(@dblAmount, 0) <= 0 THEN 'Enter the amount paid.'
        WHEN @dblAmount > @dblTotal - @dblPaid THEN 'That is more than is owing on this invoice.'
        WHEN @iAccountID = @iReceivableID OR NOT EXISTS (
                SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                  AND strAccountType IN ('Asset', 'Liability') AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the bank account it was paid into.'
        END;

    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iPaymentID;
        RETURN;
    END

    DECLARE @strDescription VARCHAR(500) = LEFT('Payment for invoice ' + @strNumber + ' - ' + @strCustomer, 500);

    BEGIN TRANSACTION;

    INSERT INTO tblInvoicePayments (iInvoiceID, dtmPaymentDate, dblAmount, iAccountID, strReference)
    VALUES (@iInvoiceID, @dtDate, @dblAmount, @iAccountID, NULLIF(LTRIM(RTRIM(@strReference)), ''));
    DECLARE @iPaymentID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'InvoicePayment', @iPaymentID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iAccountID, @dblAmount, 0, @strDescription),
           (@iJournalID, @iReceivableID, 0, @dblAmount, @strDescription);

    UPDATE tblInvoicePayments SET iJournalID = @iJournalID WHERE iPaymentID = @iPaymentID;

    COMMIT TRANSACTION;

    SELECT 'Success' AS Result, @iPaymentID AS iPaymentID;
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
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    END
    DELETE FROM tblInvoicePayments WHERE iPaymentID IN (@iPaymentID, @iPair) OR iPairPaymentID = @iPaymentID;
    COMMIT TRANSACTION;

    SELECT 'Payment Deleted' AS Result;
END
GO

-- Makes a draft a credit note (for @iCreditedInvoiceID, if given). Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspSetInvoiceKind
    @iUserId            INT,
    @iInvoiceID         INT,
    @strKind            VARCHAR(10),
    @iCreditedInvoiceID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strKind NOT IN ('Invoice', 'CreditNote')
        OR (@iCreditedInvoiceID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblInvoices WHERE iInvoiceID = @iCreditedInvoiceID AND iUserID = @iUserId))
    BEGIN
        SELECT 'Error: That invoice isn''t in this portfolio.' AS Result;
        RETURN;
    END
    UPDATE tblInvoices SET strKind = @strKind, iCreditedInvoiceID = @iCreditedInvoiceID
    WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId AND strStatus = 'Draft';
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: Only a draft can be changed to a credit note.' END AS Result;
END
GO

-- Matches an imported bank deposit (a Bank Import Suspense line) to an approved invoice: the allocation journal moves
-- the money out of suspense against Accounts Receivable, and the payment is recorded with that journal.
CREATE OR ALTER PROCEDURE dbo.uspMatchInvoiceDeposit
    @iUserId         INT,
    @iInvoiceID      INT,
    @iSuspenseLineID INT,
    @dblAmount       DECIMAL(19,4) = NULL      -- NULL: the whole deposit
AS
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

    DECLARE @strStatus VARCHAR(10), @strKind VARCHAR(10), @dblTotal DECIMAL(19,4), @strNumber VARCHAR(40), @strCustomer VARCHAR(200);
    SELECT @strStatus = I.strStatus, @strKind = I.strKind, @dblTotal = I.dblTotal, @strNumber = I.strInvoiceNumber, @strCustomer = C.strName
    FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;
    DECLARE @dblOwing DECIMAL(19,4) = @dblTotal - ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID), 0);
    SET @dblAmount = ISNULL(@dblAmount, @dblDeposit);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @iSuspenseID IS NULL THEN 'That bank transaction wasn''t found.'
        WHEN @iMatched IS NOT NULL THEN 'That bank transaction has already been allocated.'
        WHEN @dblDeposit <= 0 THEN 'Only a deposit can pay an invoice.'
        WHEN @strStatus IS NULL THEN 'That invoice was not found in this portfolio.'
        WHEN @strKind = 'CreditNote' OR @strStatus <> 'Approved' THEN 'Only an approved invoice can be paid.'
        WHEN @dblAmount <> @dblDeposit THEN 'The whole deposit must go to the invoice.'
        WHEN @dblAmount > @dblOwing THEN 'The deposit is more than is owing on the invoice.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iPaymentID;
        RETURN;
    END

    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;
    DECLARE @strDescription VARCHAR(500) = LEFT('Payment for invoice ' + @strNumber + ' - ' + @strCustomer, 500);

    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM tblJournalLines WITH (UPDLOCK, HOLDLOCK) WHERE iJournalLineID = @iSuspenseLineID AND iMatchedJournalID IS NOT NULL)
        THROW 50111, 'This transaction has already been allocated.', 1;

    INSERT INTO tblInvoicePayments (iInvoiceID, dtmPaymentDate, dblAmount, iAccountID, strReference, iSuspenseLineID)
    VALUES (@iInvoiceID, @dtDate, @dblAmount, ISNULL(@iBankID, @iSuspenseID), 'Bank import', @iSuspenseLineID);
    DECLARE @iPaymentID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'InvoicePayment', @iPaymentID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iSuspenseID, @dblAmount, 0, @strDescription),
           (@iJournalID, @iReceivableID, 0, @dblAmount, @strDescription);

    UPDATE tblJournalLines SET iMatchedJournalID = @iJournalID WHERE iJournalLineID = @iSuspenseLineID;
    UPDATE tblInvoicePayments SET iJournalID = @iJournalID WHERE iPaymentID = @iPaymentID;
    COMMIT TRANSACTION;

    SELECT 'Success' AS Result, @iPaymentID AS iPaymentID;
END
GO

-- Applies an approved credit note to an approved invoice of the same customer. No journal: both already went through
-- Accounts Receivable. A row on each records it.
CREATE OR ALTER PROCEDURE dbo.uspApplyCreditNote
    @iUserId       INT,
    @iCreditNoteID INT,
    @iInvoiceID    INT,
    @dtDate        DATE,
    @dblAmount     DECIMAL(19,4)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @cnCustomer INT, @cnTotal DECIMAL(19,4), @cnNumber VARCHAR(40), @cnOk BIT = 0;
    SELECT @cnCustomer = iCustomerID, @cnTotal = dblTotal, @cnNumber = strInvoiceNumber, @cnOk = 1
    FROM tblInvoices WHERE iInvoiceID = @iCreditNoteID AND iUserID = @iUserId AND strKind = 'CreditNote' AND strStatus = 'Approved';
    DECLARE @invCustomer INT, @invTotal DECIMAL(19,4), @invNumber VARCHAR(40), @invOk BIT = 0;
    SELECT @invCustomer = iCustomerID, @invTotal = dblTotal, @invNumber = strInvoiceNumber, @invOk = 1
    FROM tblInvoices WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId AND strKind = 'Invoice' AND strStatus = 'Approved';
    DECLARE @cnLeft DECIMAL(19,4) = @cnTotal - ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iCreditNoteID), 0);
    DECLARE @invLeft DECIMAL(19,4) = @invTotal - ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID), 0);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @cnOk = 0 THEN 'Only an approved credit note can be applied.'
        WHEN @invOk = 0 THEN 'Choose an approved invoice.'
        WHEN @cnCustomer <> @invCustomer THEN 'The invoice is for a different customer.'
        WHEN @dtDate IS NULL OR ISNULL(@dblAmount, 0) <= 0 THEN 'Enter the date and an amount more than nil.'
        WHEN @dblAmount > @cnLeft THEN 'That is more than is left on the credit note.'
        WHEN @dblAmount > @invLeft THEN 'That is more than is owing on the invoice.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result;
        RETURN;
    END

    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;

    BEGIN TRANSACTION;
    INSERT tblInvoicePayments (iInvoiceID, dtmPaymentDate, dblAmount, iAccountID, strReference, iCreditNoteID)
    VALUES (@iInvoiceID, @dtDate, @dblAmount, @iReceivableID, 'Credit note ' + @cnNumber, @iCreditNoteID);
    DECLARE @a INT = SCOPE_IDENTITY();
    INSERT tblInvoicePayments (iInvoiceID, dtmPaymentDate, dblAmount, iAccountID, strReference, iCreditNoteID, iPairPaymentID)
    VALUES (@iCreditNoteID, @dtDate, @dblAmount, @iReceivableID, 'Applied to invoice ' + @invNumber, @iCreditNoteID, @a);
    UPDATE tblInvoicePayments SET iPairPaymentID = SCOPE_IDENTITY() WHERE iPaymentID = @a;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveInvoiceLogo
    @iUserId     INT,
    @binLogo     VARBINARY(MAX) = NULL,     -- NULL removes it
    @strLogoType VARCHAR(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @binLogo IS NOT NULL AND (DATALENGTH(@binLogo) > 1048576 OR @strLogoType NOT IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp'))
    BEGIN
        SELECT 'Error: Use a PNG, JPEG, GIF or WebP picture of up to 1 MB.' AS Result;
        RETURN;
    END
    IF NOT EXISTS (SELECT 1 FROM tblInvoiceSettings WHERE iUserID = @iUserId)
        INSERT INTO tblInvoiceSettings (iUserID) VALUES (@iUserId);
    UPDATE tblInvoiceSettings SET binLogo = @binLogo, strLogoType = CASE WHEN @binLogo IS NULL THEN NULL ELSE @strLogoType END, dtmUpdatedDate = GETDATE()
    WHERE iUserID = @iUserId;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRepeatingInvoice
    @iUserId             INT,
    @iRepeatingInvoiceID INT = 0,
    @iCustomerID         INT,
    @strName             NVARCHAR(200),
    @strFrequency        VARCHAR(20),
    @dtmStart            DATE,
    @dtmEnd              DATE = NULL,
    @iDueDays            INT = 14,
    @bApprove            BIT = 0,
    @bAmountsIncludeTax  BIT = 0,
    @strReference        VARCHAR(200) = NULL,
    @strNotes            VARCHAR(1000) = NULL,
    @strLinesXml         NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblCustomers WHERE iCustomerID = @iCustomerID AND iUserID = @iUserId)
        OR @strFrequency NOT IN ('Weekly', 'Fortnightly', 'Monthly', 'Quarterly', 'HalfYearly', 'Yearly') OR @dtmStart IS NULL
        OR (@dtmEnd IS NOT NULL AND @dtmEnd < @dtmStart) OR ISNULL(@iDueDays, -1) NOT BETWEEN 0 AND 365 OR ISNULL(@strLinesXml, '') = ''
    BEGIN
        SELECT 'Error: Check the customer, how often, the dates and the lines.' AS Result, NULL AS Id;
        RETURN;
    END
    IF ISNULL(@iRepeatingInvoiceID, 0) > 0
    BEGIN
        UPDATE dbo.tblRepeatingInvoices
        SET iCustomerID = @iCustomerID, strName = LTRIM(RTRIM(@strName)), strFrequency = @strFrequency, dtmStart = @dtmStart, dtmEnd = @dtmEnd,
            iDueDays = @iDueDays, bApprove = ISNULL(@bApprove, 0), bAmountsIncludeTax = ISNULL(@bAmountsIncludeTax, 0),
            strReference = NULLIF(LTRIM(RTRIM(@strReference)), ''), strNotes = NULLIF(LTRIM(RTRIM(@strNotes)), ''), strLinesXml = @strLinesXml
        WHERE iRepeatingInvoiceID = @iRepeatingInvoiceID AND iUserID = @iUserId;
        SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating invoice isn''t in this portfolio.' END AS Result, @iRepeatingInvoiceID AS Id;
        RETURN;
    END
    INSERT dbo.tblRepeatingInvoices (iUserID, iCustomerID, strName, strFrequency, dtmStart, dtmEnd, iDueDays, bApprove, bAmountsIncludeTax,
        strReference, strNotes, strLinesXml)
    VALUES (@iUserId, @iCustomerID, LTRIM(RTRIM(@strName)), @strFrequency, @dtmStart, @dtmEnd, @iDueDays, ISNULL(@bApprove, 0), ISNULL(@bAmountsIncludeTax, 0),
        NULLIF(LTRIM(RTRIM(@strReference)), ''), NULLIF(LTRIM(RTRIM(@strNotes)), ''), @strLinesXml);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

-- Moves the count by @iBy (1 before creating an invoice, -1 if it then couldn't be made), only from @iPostedCount.
CREATE OR ALTER PROCEDURE dbo.uspMarkRepeatingInvoice
    @iUserId             INT,
    @iRepeatingInvoiceID INT,
    @iPostedCount        INT,
    @iBy                 INT,
    @dtmCreated          DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblRepeatingInvoices SET iPostedCount = iPostedCount + @iBy, dtmLastCreated = ISNULL(@dtmCreated, dtmLastCreated)
    WHERE iRepeatingInvoiceID = @iRepeatingInvoiceID AND iUserID = @iUserId AND iPostedCount = @iPostedCount;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: It has already been created.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSetRepeatingInvoiceActive
    @iUserId             INT,
    @iRepeatingInvoiceID INT,
    @bActive             BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblRepeatingInvoices SET bActive = @bActive WHERE iRepeatingInvoiceID = @iRepeatingInvoiceID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating invoice isn''t in this portfolio.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRepeatingInvoice
    @iUserId             INT,
    @iRepeatingInvoiceID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.tblRepeatingInvoices WHERE iRepeatingInvoiceID = @iRepeatingInvoiceID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating invoice isn''t in this portfolio.' END AS Result;
END
GO
