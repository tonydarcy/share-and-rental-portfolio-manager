-- =============================================
-- Migration: customer invoicing
-- Date:      18-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Invoices. Run it AFTER 2026-10-15-ledger-fixes.sql and 2026-10-17-sales-tax.sql.
-- Safe to run more than once. Nothing changes for a portfolio until it raises an invoice.
--
-- What it adds:
--   * System accounts 1200 Accounts Receivable (asset) and 4800 Sales and Fee Income
--     (revenue), if they aren't there already.
--   * tblCustomers, tblInvoiceSettings, tblInvoices, tblInvoiceLines, tblInvoicePayments.
--   * An approved invoice posts a journal (source 'Invoice') on its issue date:
--     Dr Accounts Receivable for the total, Cr each line's income account, with any sales
--     tax split to the tax control account by uspAddJournalLineWithTax, so it shows on the
--     sales tax report. A payment posts a journal (source 'InvoicePayment'):
--     Dr the bank account, Cr Accounts Receivable. Drafts post nothing.
--   * uspGetInvoiceSettings, uspSaveInvoiceSettings, uspGetCustomers, uspSaveCustomer,
--     uspDeleteCustomer, uspGetInvoices, uspGetInvoice, uspSaveInvoice, uspPostInvoiceJournal,
--     uspVoidInvoice, uspAddInvoicePayment, uspDeleteInvoicePayment.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.uspGetLedgerAccount', 'P') IS NULL OR OBJECT_ID('dbo.uspAddJournalLineWithTax', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-ledger-fixes.sql and 2026-10-17-sales-tax.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 1200, 'Accounts Receivable', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4800, 'Sales and Fee Income', 'Revenue', @iAccountID OUTPUT;
GO

IF OBJECT_ID('dbo.tblCustomers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblCustomers (
        iCustomerID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblCustomers PRIMARY KEY,
        iUserID         INT NOT NULL,                   -- the portfolio
        strName         VARCHAR(200) NOT NULL,
        strContactName  VARCHAR(200) NULL,
        strEmail        VARCHAR(500) NULL,
        strPhone        VARCHAR(50) NULL,
        strAddress      VARCHAR(500) NULL,              -- several lines
        strTaxNumber    VARCHAR(50) NULL,               -- ABN, VAT number, ...
        bIsActive       BIT NOT NULL CONSTRAINT DF_tblCustomers_Active DEFAULT (1),
        dtmCreatedDate  DATETIME NOT NULL CONSTRAINT DF_tblCustomers_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblCustomers_User ON dbo.tblCustomers (iUserID, strName);
END
GO

IF OBJECT_ID('dbo.tblInvoiceSettings', 'U') IS NULL
    CREATE TABLE dbo.tblInvoiceSettings (
        iUserID             INT NOT NULL CONSTRAINT PK_tblInvoiceSettings PRIMARY KEY,
        strNumberPrefix     VARCHAR(20) NOT NULL CONSTRAINT DF_tblInvoiceSettings_Prefix DEFAULT ('INV-'),
        iNextNumber         INT NOT NULL CONSTRAINT DF_tblInvoiceSettings_Next DEFAULT (1),
        iDueDays            INT NOT NULL CONSTRAINT DF_tblInvoiceSettings_Due DEFAULT (14),
        bAmountsIncludeTax  BIT NOT NULL CONSTRAINT DF_tblInvoiceSettings_Incl DEFAULT (0),
        strPaymentDetails   VARCHAR(1000) NULL,         -- bank details shown on each invoice
        strFooter           VARCHAR(1000) NULL,
        dtmUpdatedDate      DATETIME NOT NULL CONSTRAINT DF_tblInvoiceSettings_Updated DEFAULT (GETDATE())
    );
GO

IF OBJECT_ID('dbo.tblInvoices', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblInvoices (
        iInvoiceID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblInvoices PRIMARY KEY,
        iUserID             INT NOT NULL,
        iCustomerID         INT NOT NULL,
        strInvoiceNumber    VARCHAR(40) NOT NULL,
        dtmIssueDate        DATE NOT NULL,
        dtmDueDate          DATE NOT NULL,
        strReference        VARCHAR(200) NULL,          -- the customer's order number etc.
        strNotes            VARCHAR(1000) NULL,
        bAmountsIncludeTax  BIT NOT NULL,
        strStatus           VARCHAR(10) NOT NULL,       -- Draft, Approved or Void
        dblNet              DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblInvoices_Net DEFAULT (0),
        dblTax              DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblInvoices_Tax DEFAULT (0),
        dblTotal            DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblInvoices_Total DEFAULT (0),
        iJournalID          INT NULL,
        dtmCreatedDate      DATETIME NOT NULL CONSTRAINT DF_tblInvoices_Created DEFAULT (GETDATE()),
        dtmUpdatedDate      DATETIME NOT NULL CONSTRAINT DF_tblInvoices_Updated DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_tblInvoices_Number ON dbo.tblInvoices (iUserID, strInvoiceNumber);
    CREATE INDEX IX_tblInvoices_Customer ON dbo.tblInvoices (iCustomerID);
END
GO

IF OBJECT_ID('dbo.tblInvoiceLines', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblInvoiceLines (
        iInvoiceLineID  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblInvoiceLines PRIMARY KEY,
        iInvoiceID      INT NOT NULL CONSTRAINT FK_tblInvoiceLines_Invoice REFERENCES dbo.tblInvoices (iInvoiceID) ON DELETE CASCADE,
        iLineNumber     INT NOT NULL,
        strDescription  VARCHAR(500) NOT NULL,
        dblQuantity     DECIMAL(19,4) NOT NULL,
        dblUnitPrice    DECIMAL(19,4) NOT NULL,
        iAccountID      INT NOT NULL,
        iTaxCodeID      INT NULL,
        dblNet          DECIMAL(19,4) NOT NULL,         -- before tax
        dblTax          DECIMAL(19,4) NOT NULL,
        dblTotal        DECIMAL(19,4) NOT NULL          -- including tax
    );
    CREATE INDEX IX_tblInvoiceLines_Invoice ON dbo.tblInvoiceLines (iInvoiceID);
END
GO

IF OBJECT_ID('dbo.tblInvoicePayments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblInvoicePayments (
        iPaymentID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblInvoicePayments PRIMARY KEY,
        iInvoiceID      INT NOT NULL CONSTRAINT FK_tblInvoicePayments_Invoice REFERENCES dbo.tblInvoices (iInvoiceID),
        dtmPaymentDate  DATE NOT NULL,
        dblAmount       DECIMAL(19,4) NOT NULL,
        iAccountID      INT NOT NULL,                   -- the bank account it was paid into
        strReference    VARCHAR(200) NULL,
        iJournalID      INT NULL,
        dtmCreatedDate  DATETIME NOT NULL CONSTRAINT DF_tblInvoicePayments_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblInvoicePayments_Invoice ON dbo.tblInvoicePayments (iInvoiceID);
END
GO

-- =============================================
-- Settings
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetInvoiceSettings]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: The portfolio's invoice settings, or the defaults when it has
--              none saved: one row.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT ISNULL(S.strNumberPrefix, 'INV-') AS strNumberPrefix,
           ISNULL(S.iNextNumber, 1) AS iNextNumber,
           ISNULL(S.iDueDays, 14) AS iDueDays,
           ISNULL(S.bAmountsIncludeTax, CAST(0 AS BIT)) AS bAmountsIncludeTax,
           S.strPaymentDetails, S.strFooter
    FROM (SELECT @iUserId AS iUserID) X
    LEFT JOIN tblInvoiceSettings S ON S.iUserID = X.iUserID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveInvoiceSettings]
(
    @iUserId            INT,
    @strNumberPrefix    VARCHAR(20),
    @iNextNumber        INT,
    @iDueDays           INT,
    @bAmountsIncludeTax BIT,
    @strPaymentDetails  VARCHAR(1000) = NULL,
    @strFooter          VARCHAR(1000) = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Saves the invoice settings. Returns Result ('Success' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF ISNULL(@iNextNumber, 0) < 1 OR ISNULL(@iDueDays, -1) < 0 OR @iDueDays > 365
    BEGIN
        SELECT 'Error: Check the next invoice number and the days to pay.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM tblInvoiceSettings WITH (UPDLOCK, HOLDLOCK) WHERE iUserID = @iUserId)
        UPDATE tblInvoiceSettings
        SET strNumberPrefix = LTRIM(RTRIM(ISNULL(@strNumberPrefix, ''))), iNextNumber = @iNextNumber, iDueDays = @iDueDays,
            bAmountsIncludeTax = ISNULL(@bAmountsIncludeTax, 0),
            strPaymentDetails = NULLIF(LTRIM(RTRIM(@strPaymentDetails)), ''), strFooter = NULLIF(LTRIM(RTRIM(@strFooter)), ''),
            dtmUpdatedDate = GETDATE()
        WHERE iUserID = @iUserId;
    ELSE
        INSERT INTO tblInvoiceSettings (iUserID, strNumberPrefix, iNextNumber, iDueDays, bAmountsIncludeTax, strPaymentDetails, strFooter)
        VALUES (@iUserId, LTRIM(RTRIM(ISNULL(@strNumberPrefix, ''))), @iNextNumber, @iDueDays, ISNULL(@bAmountsIncludeTax, 0),
                NULLIF(LTRIM(RTRIM(@strPaymentDetails)), ''), NULLIF(LTRIM(RTRIM(@strFooter)), ''));
    COMMIT TRANSACTION;

    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- Customers
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetCustomers]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: The portfolio's customers with how much they owe on approved
--              invoices (Outstanding) and how many invoices they have.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT C.iCustomerID, C.strName, C.strContactName, C.strEmail, C.strPhone, C.strAddress, C.strTaxNumber, C.bIsActive,
           ISNULL(B.InvoiceCount, 0) AS InvoiceCount, ISNULL(B.Outstanding, 0) AS Outstanding
    FROM tblCustomers C
    LEFT JOIN (
        SELECT I.iCustomerID, COUNT(*) AS InvoiceCount,
               SUM(CASE WHEN I.strStatus = 'Approved' THEN I.dblTotal - ISNULL(P.Paid, 0) ELSE 0 END) AS Outstanding
        FROM tblInvoices I
        LEFT JOIN (SELECT iInvoiceID, SUM(dblAmount) AS Paid FROM tblInvoicePayments GROUP BY iInvoiceID) P ON P.iInvoiceID = I.iInvoiceID
        WHERE I.iUserID = @iUserId
        GROUP BY I.iCustomerID
    ) B ON B.iCustomerID = C.iCustomerID
    WHERE C.iUserID = @iUserId
    ORDER BY C.bIsActive DESC, C.strName;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveCustomer]
(
    @iUserId        INT,
    @iCustomerID    INT = NULL,     -- NULL adds one
    @strName        VARCHAR(200),
    @strContactName VARCHAR(200) = NULL,
    @strEmail       VARCHAR(500) = NULL,
    @strPhone       VARCHAR(50) = NULL,
    @strAddress     VARCHAR(500) = NULL,
    @strTaxNumber   VARCHAR(50) = NULL,
    @bIsActive      BIT = 1
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Adds or changes a customer. Returns Result ('Success' or
--              'Error: ...') and iCustomerID.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));

    IF @strName = ''
    BEGIN
        SELECT 'Error: Enter the customer''s name.' AS Result, CAST(NULL AS INT) AS iCustomerID;
        RETURN;
    END

    IF @iCustomerID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblCustomers WHERE iCustomerID = @iCustomerID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That customer was not found.' AS Result, CAST(NULL AS INT) AS iCustomerID;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblCustomers WHERE iUserID = @iUserId AND strName = @strName AND iCustomerID <> ISNULL(@iCustomerID, 0))
    BEGIN
        SELECT 'Error: There is already a customer with that name.' AS Result, CAST(NULL AS INT) AS iCustomerID;
        RETURN;
    END

    IF @iCustomerID IS NULL
    BEGIN
        INSERT INTO tblCustomers (iUserID, strName, strContactName, strEmail, strPhone, strAddress, strTaxNumber, bIsActive)
        VALUES (@iUserId, @strName, NULLIF(LTRIM(RTRIM(@strContactName)), ''), NULLIF(LTRIM(RTRIM(@strEmail)), ''),
                NULLIF(LTRIM(RTRIM(@strPhone)), ''), NULLIF(LTRIM(RTRIM(@strAddress)), ''), NULLIF(LTRIM(RTRIM(@strTaxNumber)), ''),
                ISNULL(@bIsActive, 1));
        SET @iCustomerID = SCOPE_IDENTITY();
    END
    ELSE
        UPDATE tblCustomers
        SET strName = @strName, strContactName = NULLIF(LTRIM(RTRIM(@strContactName)), ''), strEmail = NULLIF(LTRIM(RTRIM(@strEmail)), ''),
            strPhone = NULLIF(LTRIM(RTRIM(@strPhone)), ''), strAddress = NULLIF(LTRIM(RTRIM(@strAddress)), ''),
            strTaxNumber = NULLIF(LTRIM(RTRIM(@strTaxNumber)), ''), bIsActive = ISNULL(@bIsActive, 1)
        WHERE iCustomerID = @iCustomerID AND iUserID = @iUserId;

    SELECT 'Success' AS Result, @iCustomerID AS iCustomerID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteCustomer]
(
    @iUserId     INT,
    @iCustomerID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Deletes a customer with no invoices. Returns Result
--              ('Customer Deleted' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM tblInvoices WHERE iCustomerID = @iCustomerID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: This customer has invoices, so it can''t be deleted. Mark it inactive instead.' AS Result;
        RETURN;
    END
    DELETE FROM tblCustomers WHERE iCustomerID = @iCustomerID AND iUserID = @iUserId;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That customer was not found.' AS Result;
    ELSE
        SELECT 'Customer Deleted' AS Result;
END
GO

-- =============================================
-- Invoices
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetInvoices]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Every invoice of the portfolio, newest first, with the
--              customer's name and how much has been paid.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT I.iInvoiceID, I.strInvoiceNumber, I.iCustomerID, C.strName AS CustomerName, I.dtmIssueDate, I.dtmDueDate,
           I.strReference, I.strStatus, I.dblNet, I.dblTax, I.dblTotal,
           ISNULL((SELECT SUM(P.dblAmount) FROM tblInvoicePayments P WHERE P.iInvoiceID = I.iInvoiceID), 0) AS AmountPaid
    FROM tblInvoices I
    JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iUserID = @iUserId
    ORDER BY I.dtmIssueDate DESC, I.iInvoiceID DESC;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetInvoice]
(
    @iUserId    INT,
    @iInvoiceID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: One invoice of the portfolio as three result sets: the header
--              with its customer, the lines, and the payments. Nothing comes
--              back for another portfolio's invoice.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT I.iInvoiceID, I.strInvoiceNumber, I.iCustomerID, I.dtmIssueDate, I.dtmDueDate, I.strReference, I.strNotes,
           I.bAmountsIncludeTax, I.strStatus, I.dblNet, I.dblTax, I.dblTotal, I.iJournalID,
           C.strName AS CustomerName, C.strContactName AS CustomerContact, C.strEmail AS CustomerEmail,
           C.strAddress AS CustomerAddress, C.strTaxNumber AS CustomerTaxNumber
    FROM tblInvoices I
    JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;

    SELECT L.iInvoiceLineID, L.iLineNumber, L.strDescription, L.dblQuantity, L.dblUnitPrice, L.iAccountID, L.iTaxCodeID,
           T.strCode AS TaxCode, L.dblNet, L.dblTax, L.dblTotal
    FROM tblInvoiceLines L
    JOIN tblInvoices I ON I.iInvoiceID = L.iInvoiceID
    LEFT JOIN tblTaxCodes T ON T.iTaxCodeID = L.iTaxCodeID
    WHERE L.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId
    ORDER BY L.iLineNumber;

    SELECT P.iPaymentID, P.dtmPaymentDate, P.dblAmount, P.iAccountID, A.strAccountName, P.strReference, P.iJournalID
    FROM tblInvoicePayments P
    JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID
    LEFT JOIN tblChartOfAccounts A ON A.iAccountID = P.iAccountID
    WHERE P.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId
    ORDER BY P.dtmPaymentDate, P.iPaymentID;
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
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @dtDate DATE, @dblTotal DECIMAL(19,4), @strDescription VARCHAR(500), @strStatus VARCHAR(10);
    SELECT @iJournalID = I.iJournalID, @dtDate = I.dtmIssueDate, @dblTotal = I.dblTotal, @strStatus = I.strStatus,
           @strDescription = LEFT('Invoice ' + I.strInvoiceNumber + ' - ' + C.strName, 500)
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
    VALUES (@iJournalID, @iReceivableID, @dblTotal, 0, @strDescription);

    DECLARE @iAccountID INT, @iTaxCodeID INT, @dblGross DECIMAL(19,4), @dblTax DECIMAL(19,4), @strLine VARCHAR(500);
    DECLARE @dblDebit DECIMAL(19,4), @dblCredit DECIMAL(19,4);
    DECLARE curLines CURSOR LOCAL FAST_FORWARD FOR
        SELECT iAccountID, iTaxCodeID, dblTotal, dblTax, strDescription
        FROM tblInvoiceLines WHERE iInvoiceID = @iInvoiceID AND dblTotal <> 0 ORDER BY iLineNumber;
    OPEN curLines;
    FETCH NEXT FROM curLines INTO @iAccountID, @iTaxCodeID, @dblGross, @dblTax, @strLine;
    WHILE @@FETCH_STATUS = 0
    BEGIN
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

CREATE OR ALTER PROCEDURE [dbo].[uspSaveInvoice]
(
    @iUserId            INT,
    @iInvoiceID         INT = NULL,     -- NULL adds one, numbered from the settings
    @iCustomerID        INT,
    @dtIssueDate        DATE,
    @dtDueDate          DATE,
    @strReference       VARCHAR(200) = NULL,
    @strNotes           VARCHAR(1000) = NULL,
    @bAmountsIncludeTax BIT,
    @bApprove           BIT = 0,        -- 1 approves a draft and posts it
    @xmlLines           XML             -- <lines><l d="desc" q="1" p="100" a="acct" t="taxcode" n="net" x="tax" g="gross"/></lines>
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Saves an invoice and its lines (worked out by the site, checked
--              here). An approved invoice's journal is posted again, so the
--              ledger always matches it. A void invoice can't be changed, and
--              an approved one can't drop below what has been paid. Returns
--              Result ('Success' or 'Error: ...') and iInvoiceID.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Lines TABLE (iLineNumber INT IDENTITY(1,1), strDescription VARCHAR(500), dblQuantity DECIMAL(19,4), dblUnitPrice DECIMAL(19,4),
                          iAccountID INT, iTaxCodeID INT, dblNet DECIMAL(19,4), dblTax DECIMAL(19,4), dblTotal DECIMAL(19,4));
    INSERT INTO @Lines (strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal)
    SELECT LTRIM(RTRIM(X.l.value('@d', 'VARCHAR(500)'))), X.l.value('@q', 'DECIMAL(19,4)'), X.l.value('@p', 'DECIMAL(19,4)'),
           X.l.value('@a', 'INT'), NULLIF(X.l.value('@t', 'INT'), 0),
           X.l.value('@n', 'DECIMAL(19,4)'), X.l.value('@x', 'DECIMAL(19,4)'), X.l.value('@g', 'DECIMAL(19,4)')
    FROM @xmlLines.nodes('/lines/l') AS X(l);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN NOT EXISTS (SELECT 1 FROM tblCustomers WHERE iCustomerID = @iCustomerID AND iUserID = @iUserId)
            THEN 'Choose a customer.'
        WHEN @dtIssueDate IS NULL OR @dtDueDate IS NULL OR @dtDueDate < @dtIssueDate
            THEN 'Check the dates: the due date can''t be before the invoice date.'
        WHEN NOT EXISTS (SELECT 1 FROM @Lines)
            THEN 'Add at least one line.'
        WHEN EXISTS (SELECT 1 FROM @Lines WHERE ISNULL(strDescription, '') = '' OR dblQuantity IS NULL OR dblUnitPrice IS NULL
                                              OR dblNet IS NULL OR dblTax IS NULL OR dblTotal IS NULL)
            THEN 'Each line needs a description, quantity and price.'
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE NOT EXISTS (
                         SELECT 1 FROM tblChartOfAccounts A WHERE A.iAccountID = L.iAccountID AND (A.iUserID = @iUserId OR A.iUserID = 0)
                           AND ISNULL(A.bIsPostable, 1) = 1))
            THEN 'Choose an account for each line.'
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE L.iTaxCodeID IS NOT NULL AND NOT EXISTS (
                         SELECT 1 FROM tblTaxCodes T WHERE T.iTaxCodeID = L.iTaxCodeID AND T.iUserID = @iUserId))
            THEN 'That tax code was not found in this portfolio.'
        WHEN EXISTS (SELECT 1 FROM @Lines WHERE dblNet + dblTax <> dblTotal OR (iTaxCodeID IS NULL AND dblTax <> 0)
                                              OR ABS(dblTax) > ABS(dblTotal) OR SIGN(dblTax) * SIGN(dblTotal) < 0)
            THEN 'A line''s amounts don''t add up. Check its price and tax.'
        WHEN (SELECT SUM(dblTotal) FROM @Lines) <= 0
            THEN 'The invoice total must be more than zero.'
        END;

    IF @Error IS NULL AND EXISTS (SELECT 1 FROM @Lines WHERE iTaxCodeID IS NOT NULL)
       AND NOT EXISTS (SELECT 1 FROM tblSalesTaxSetup WHERE iUserID = @iUserId)
        SET @Error = 'Set up sales tax for this portfolio before using tax codes.';

    DECLARE @strStatus VARCHAR(10), @dblPaid DECIMAL(19,4) = 0;
    IF @Error IS NULL AND @iInvoiceID IS NOT NULL
    BEGIN
        SELECT @strStatus = strStatus FROM tblInvoices WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId;
        SELECT @dblPaid = ISNULL(SUM(dblAmount), 0) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID;
        IF @strStatus IS NULL
            SET @Error = 'That invoice was not found in this portfolio.';
        ELSE IF @strStatus = 'Void'
            SET @Error = 'This invoice has been voided, so it can''t be changed.';
        ELSE IF (SELECT SUM(dblTotal) FROM @Lines) < @dblPaid
            SET @Error = 'The total can''t be less than what has already been paid.';
    END

    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, @iInvoiceID AS iInvoiceID;
        RETURN;
    END

    DECLARE @NewStatus VARCHAR(10) = CASE WHEN @strStatus = 'Approved' OR @bApprove = 1 THEN 'Approved' ELSE 'Draft' END;

    BEGIN TRANSACTION;

    IF @iInvoiceID IS NULL
    BEGIN
        DECLARE @strPrefix VARCHAR(20) = 'INV-', @iNext INT = 1, @strNumber VARCHAR(40);
        SELECT @strPrefix = strNumberPrefix, @iNext = iNextNumber FROM tblInvoiceSettings WITH (UPDLOCK, HOLDLOCK) WHERE iUserID = @iUserId;
        SET @strNumber = @strPrefix + RIGHT('0000' + CAST(@iNext AS VARCHAR(20)), 4);
        WHILE EXISTS (SELECT 1 FROM tblInvoices WITH (UPDLOCK, HOLDLOCK) WHERE iUserID = @iUserId AND strInvoiceNumber = @strNumber)
        BEGIN
            SET @iNext += 1;
            SET @strNumber = @strPrefix + RIGHT('0000' + CAST(@iNext AS VARCHAR(20)), 4);
        END

        IF EXISTS (SELECT 1 FROM tblInvoiceSettings WHERE iUserID = @iUserId)
            UPDATE tblInvoiceSettings SET iNextNumber = @iNext + 1 WHERE iUserID = @iUserId;
        ELSE
            INSERT INTO tblInvoiceSettings (iUserID, iNextNumber) VALUES (@iUserId, @iNext + 1);

        INSERT INTO tblInvoices (iUserID, iCustomerID, strInvoiceNumber, dtmIssueDate, dtmDueDate, strReference, strNotes, bAmountsIncludeTax, strStatus)
        VALUES (@iUserId, @iCustomerID, @strNumber, @dtIssueDate, @dtDueDate, NULLIF(LTRIM(RTRIM(@strReference)), ''),
                NULLIF(LTRIM(RTRIM(@strNotes)), ''), ISNULL(@bAmountsIncludeTax, 0), @NewStatus);
        SET @iInvoiceID = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE tblInvoices
        SET iCustomerID = @iCustomerID, dtmIssueDate = @dtIssueDate, dtmDueDate = @dtDueDate,
            strReference = NULLIF(LTRIM(RTRIM(@strReference)), ''), strNotes = NULLIF(LTRIM(RTRIM(@strNotes)), ''),
            bAmountsIncludeTax = ISNULL(@bAmountsIncludeTax, 0), strStatus = @NewStatus, dtmUpdatedDate = GETDATE()
        WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId;
        DELETE FROM tblInvoiceLines WHERE iInvoiceID = @iInvoiceID;
    END

    INSERT INTO tblInvoiceLines (iInvoiceID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal)
    SELECT @iInvoiceID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal
    FROM @Lines;

    UPDATE tblInvoices
    SET dblNet = (SELECT SUM(dblNet) FROM @Lines), dblTax = (SELECT SUM(dblTax) FROM @Lines), dblTotal = (SELECT SUM(dblTotal) FROM @Lines)
    WHERE iInvoiceID = @iInvoiceID;

    IF @NewStatus = 'Approved'
        EXEC dbo.uspPostInvoiceJournal @iUserId, @iInvoiceID;

    COMMIT TRANSACTION;

    SELECT 'Success' AS Result, @iInvoiceID AS iInvoiceID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspVoidInvoice]
(
    @iUserId    INT,
    @iInvoiceID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: A draft is deleted. An approved invoice with no payments is
--              voided: its journal is deleted and it stays, marked Void, so
--              its number isn't reused. Returns Result ('Invoice Deleted',
--              'Invoice Voided' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strStatus VARCHAR(10), @iJournalID INT;
    SELECT @strStatus = strStatus, @iJournalID = iJournalID FROM tblInvoices WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId;

    IF @strStatus IS NULL
    BEGIN
        SELECT 'Error: That invoice was not found in this portfolio.' AS Result;
        RETURN;
    END
    IF @strStatus = 'Void'
    BEGIN
        SELECT 'Error: This invoice has already been voided.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID)
    BEGIN
        SELECT 'Error: This invoice has payments. Delete them first.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF @strStatus = 'Draft'
    BEGIN
        DELETE FROM tblInvoices WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId;
        COMMIT TRANSACTION;
        SELECT 'Invoice Deleted' AS Result;
        RETURN;
    END

    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    UPDATE tblInvoices SET strStatus = 'Void', iJournalID = NULL, dtmUpdatedDate = GETDATE() WHERE iInvoiceID = @iInvoiceID;
    COMMIT TRANSACTION;

    SELECT 'Invoice Voided' AS Result;
END
GO

-- =============================================
-- Payments
-- =============================================
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
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strStatus VARCHAR(10), @dblTotal DECIMAL(19,4), @strNumber VARCHAR(40), @strCustomer VARCHAR(200);
    SELECT @strStatus = I.strStatus, @dblTotal = I.dblTotal, @strNumber = I.strInvoiceNumber, @strCustomer = C.strName
    FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;

    DECLARE @dblPaid DECIMAL(19,4) = ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID), 0);
    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @strStatus IS NULL THEN 'That invoice was not found in this portfolio.'
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
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @bFound BIT = 0;
    SELECT @iJournalID = P.iJournalID, @bFound = 1
    FROM tblInvoicePayments P JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID
    WHERE P.iPaymentID = @iPaymentID AND I.iUserID = @iUserId;

    IF @bFound = 0
    BEGIN
        SELECT 'Error: That payment was not found.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    DELETE FROM tblInvoicePayments WHERE iPaymentID = @iPaymentID;
    COMMIT TRANSACTION;

    SELECT 'Payment Deleted' AS Result;
END
GO
