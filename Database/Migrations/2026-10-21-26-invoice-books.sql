-- =============================================
-- Migration: invoices with items, tracking and other currencies
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with inventory, tracking
-- categories and foreign currency invoices. Run it after 2026-10-21-17-invoicing-extras.sql, 2026-10-21-24-inventory.sql
-- and 2026-10-21-25-bills.sql. Safe to run more than once. Existing invoices post exactly as before.
--
-- What it changes:
--   * uspGetInvoices and uspGetInvoice also return the currency, rate and amount posted, and each line's item and
--     tracking options.
--   * uspSaveInvoice takes @strCurrency and @dblExchangeRate, and an item (i) and tracking options (t1, t2) on each line.
--   * uspPostInvoiceJournal posts a foreign currency invoice's lines divided by its rate, puts the tracking options on
--     the journal lines, then takes tracked items out of stock and posts the cost of goods sold.
--   * uspVoidInvoice puts a voided invoice's stock back.
--   * uspAddInvoicePayment takes @dblExchangeRate for a foreign currency invoice: the bank gets the amount divided by
--     the day's rate, Accounts Receivable is cleared at the invoice's rate, and the difference goes to 4950.
--   * uspMatchInvoiceDeposit and uspApplyCreditNote refuse foreign currency invoices (record the payment instead).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF COL_LENGTH('dbo.tblInvoices', 'strKind') IS NULL OR COL_LENGTH('dbo.tblInvoices', 'strCurrency') IS NULL
   OR COL_LENGTH('dbo.tblInvoiceLines', 'iItemID') IS NULL OR OBJECT_ID('dbo.tblBills', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-17-invoicing-extras.sql, 2026-10-21-23-foreign-currency.sql, 2026-10-21-24-inventory.sql and 2026-10-21-25-bills.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

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
-- Date:        21-Oct-2026
-- Description: Also the currency, rate and total posted.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT I.iInvoiceID, I.strInvoiceNumber, I.iCustomerID, C.strName AS CustomerName, I.dtmIssueDate, I.dtmDueDate,
           I.strReference, I.strStatus, I.dblNet, I.dblTax, I.dblTotal,
           ISNULL((SELECT SUM(P.dblAmount) FROM tblInvoicePayments P WHERE P.iInvoiceID = I.iInvoiceID), 0) AS AmountPaid,
           I.strCurrency, I.dblExchangeRate, I.dblHomeTotal
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
-- Date:        21-Oct-2026
-- Description: Also the currency, each line's item and tracking options, and
--              each payment's rate.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT I.iInvoiceID, I.strInvoiceNumber, I.iCustomerID, I.dtmIssueDate, I.dtmDueDate, I.strReference, I.strNotes,
           I.bAmountsIncludeTax, I.strStatus, I.dblNet, I.dblTax, I.dblTotal, I.iJournalID,
           C.strName AS CustomerName, C.strContactName AS CustomerContact, C.strEmail AS CustomerEmail,
           C.strAddress AS CustomerAddress, C.strTaxNumber AS CustomerTaxNumber,
           I.strCurrency, I.dblExchangeRate, I.dblHomeTotal
    FROM tblInvoices I
    JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;

    SELECT L.iInvoiceLineID, L.iLineNumber, L.strDescription, L.dblQuantity, L.dblUnitPrice, L.iAccountID, L.iTaxCodeID,
           T.strCode AS TaxCode, L.dblNet, L.dblTax, L.dblTotal, L.iItemID, L.iTrackingOption1ID, L.iTrackingOption2ID
    FROM tblInvoiceLines L
    JOIN tblInvoices I ON I.iInvoiceID = L.iInvoiceID
    LEFT JOIN tblTaxCodes T ON T.iTaxCodeID = L.iTaxCodeID
    WHERE L.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId
    ORDER BY L.iLineNumber;

    SELECT P.iPaymentID, P.dtmPaymentDate, P.dblAmount, P.iAccountID, A.strAccountName, P.strReference, P.iJournalID,
           P.dblExchangeRate, P.dblHomeAmount
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
-- Date:        21-Oct-2026
-- Description: A credit note posts the other way round: Cr Accounts
--              Receivable, Dr each line's account and its tax.
-- Date:        21-Oct-2026
-- Description: An invoice in another currency posts each line and its tax
--              divided by the rate, to the cent. Lines carry their tracking
--              options. Tracked items are then taken out of stock and the cost
--              of goods sold posted (uspSyncInventoryMovements).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @dtDate DATE, @strDescription VARCHAR(500), @strStatus VARCHAR(10), @bCredit BIT, @dblRate DECIMAL(19,8);
    SELECT @iJournalID = I.iJournalID, @dtDate = I.dtmIssueDate, @strStatus = I.strStatus,
           @bCredit = CASE WHEN I.strKind = 'CreditNote' THEN 1 ELSE 0 END,
           @dblRate = CASE WHEN I.strCurrency IS NULL THEN 1 ELSE I.dblExchangeRate END,
           @strDescription = LEFT(CASE WHEN I.strKind = 'CreditNote' THEN 'Credit note ' ELSE 'Invoice ' END + I.strInvoiceNumber + ' - ' + C.strName, 500)
    FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;

    IF @strStatus IS NULL
        THROW 50501, 'That invoice was not found in this portfolio.', 1;
    IF @strStatus <> 'Approved'
        THROW 50502, 'Only an approved invoice is posted to the ledger.', 1;
    IF ISNULL(@dblRate, 0) <= 0
        THROW 50504, 'The invoice needs an exchange rate.', 1;

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

    DECLARE @dblHomeTotal DECIMAL(19,4) = 0, @iAfter INT;
    DECLARE @iAccountID INT, @iTaxCodeID INT, @dblGross DECIMAL(19,4), @dblTax DECIMAL(19,4), @strLine VARCHAR(500), @t1 INT, @t2 INT;
    DECLARE @dblDebit DECIMAL(19,4), @dblCredit DECIMAL(19,4);
    DECLARE curLines CURSOR LOCAL FAST_FORWARD FOR
        SELECT iAccountID, iTaxCodeID, ROUND(dblTotal / @dblRate, 2), ROUND(dblTax / @dblRate, 2), strDescription, iTrackingOption1ID, iTrackingOption2ID
        FROM tblInvoiceLines WHERE iInvoiceID = @iInvoiceID AND dblTotal <> 0 ORDER BY iLineNumber;
    OPEN curLines;
    FETCH NEXT FROM curLines INTO @iAccountID, @iTaxCodeID, @dblGross, @dblTax, @strLine, @t1, @t2;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @dblHomeTotal += @dblGross;
        IF @bCredit = 1
            SET @dblGross = -@dblGross;
        SET @dblCredit = CASE WHEN @dblGross > 0 THEN @dblGross ELSE 0 END;
        SET @dblDebit = CASE WHEN @dblGross < 0 THEN -@dblGross ELSE 0 END;
        SET @iAfter = ISNULL((SELECT MAX(iJournalLineID) FROM tblJournalLines WHERE iJournalID = @iJournalID), 0);
        IF @iTaxCodeID IS NOT NULL
        BEGIN
            SET @dblTax = ABS(@dblTax);
            EXEC dbo.uspAddJournalLineWithTax @iUserId, @iJournalID, @iAccountID, @dblDebit, @dblCredit, @strLine, @iTaxCodeID, @dblTax;
        END
        ELSE
            INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
            VALUES (@iJournalID, @iAccountID, @dblDebit, @dblCredit, @strLine);
        IF @t1 IS NOT NULL OR @t2 IS NOT NULL
            UPDATE tblJournalLines SET iTrackingOption1ID = @t1, iTrackingOption2ID = @t2
            WHERE iJournalID = @iJournalID AND iJournalLineID > @iAfter;
        FETCH NEXT FROM curLines INTO @iAccountID, @iTaxCodeID, @dblGross, @dblTax, @strLine, @t1, @t2;
    END
    CLOSE curLines;
    DEALLOCATE curLines;

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iReceivableID, CASE WHEN @bCredit = 1 THEN 0 ELSE @dblHomeTotal END, CASE WHEN @bCredit = 1 THEN @dblHomeTotal ELSE 0 END, @strDescription);
    UPDATE tblInvoices SET dblHomeTotal = @dblHomeTotal WHERE iInvoiceID = @iInvoiceID;

    IF (SELECT SUM(dblDebit) - SUM(dblCredit) FROM tblJournalLines WHERE iJournalID = @iJournalID) <> 0
        THROW 50503, 'The invoice journal did not balance, so nothing was saved.', 1;

    EXEC dbo.uspSyncInventoryMovements @iUserId, 'Invoice', @iInvoiceID;
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
    @xmlLines           XML,            -- <lines><l d="desc" q="1" p="100" a="acct" t="taxcode" n="net" x="tax" g="gross" i="item" t1="option" t2="option"/></lines>
    @strCurrency        CHAR(3) = NULL, -- NULL: the portfolio's currency
    @dblExchangeRate    DECIMAL(19,8) = NULL
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
-- Date:        21-Oct-2026
-- Description: Lines can carry an inventory item and tracking options; the
--              invoice can be in another currency with its exchange rate.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @strCurrency = NULLIF(UPPER(LTRIM(RTRIM(@strCurrency))), '');
    IF @strCurrency IS NULL
        SET @dblExchangeRate = NULL;

    DECLARE @Lines TABLE (iLineNumber INT IDENTITY(1,1), strDescription VARCHAR(500), dblQuantity DECIMAL(19,4), dblUnitPrice DECIMAL(19,4),
                          iAccountID INT, iTaxCodeID INT, dblNet DECIMAL(19,4), dblTax DECIMAL(19,4), dblTotal DECIMAL(19,4),
                          iItemID INT, iTrackingOption1ID INT, iTrackingOption2ID INT);
    INSERT INTO @Lines (strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal, iItemID, iTrackingOption1ID, iTrackingOption2ID)
    SELECT LTRIM(RTRIM(X.l.value('@d', 'VARCHAR(500)'))), X.l.value('@q', 'DECIMAL(19,4)'), X.l.value('@p', 'DECIMAL(19,4)'),
           X.l.value('@a', 'INT'), NULLIF(X.l.value('@t', 'INT'), 0),
           X.l.value('@n', 'DECIMAL(19,4)'), X.l.value('@x', 'DECIMAL(19,4)'), X.l.value('@g', 'DECIMAL(19,4)'),
           NULLIF(X.l.value('@i', 'INT'), 0), NULLIF(X.l.value('@t1', 'INT'), 0), NULLIF(X.l.value('@t2', 'INT'), 0)
    FROM @xmlLines.nodes('/lines/l') AS X(l);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN NOT EXISTS (SELECT 1 FROM tblCustomers WHERE iCustomerID = @iCustomerID AND iUserID = @iUserId)
            THEN 'Choose a customer.'
        WHEN @dtIssueDate IS NULL OR @dtDueDate IS NULL OR @dtDueDate < @dtIssueDate
            THEN 'Check the dates: the due date can''t be before the invoice date.'
        WHEN @strCurrency IS NOT NULL AND (LEN(@strCurrency) <> 3 OR ISNULL(@dblExchangeRate, 0) <= 0)
            THEN 'Enter the currency code and its exchange rate.'
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
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE L.iItemID IS NOT NULL AND NOT EXISTS (
                         SELECT 1 FROM tblInventoryItems I WHERE I.iItemID = L.iItemID AND I.iUserID = @iUserId))
            THEN 'That item was not found in this portfolio.'
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE (L.iTrackingOption1ID IS NOT NULL AND NOT EXISTS (
                         SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
                         WHERE O.iOptionID = L.iTrackingOption1ID AND C.iUserID = @iUserId AND C.iPosition = 1))
                      OR (L.iTrackingOption2ID IS NOT NULL AND NOT EXISTS (
                         SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
                         WHERE O.iOptionID = L.iTrackingOption2ID AND C.iUserID = @iUserId AND C.iPosition = 2)))
            THEN 'Choose tracking options from this portfolio''s categories.'
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
        ELSE IF @dblPaid > 0 AND EXISTS (SELECT 1 FROM tblInvoices WHERE iInvoiceID = @iInvoiceID
                    AND (ISNULL(strCurrency, '') <> ISNULL(@strCurrency, '') OR ISNULL(dblExchangeRate, 0) <> ISNULL(@dblExchangeRate, 0)))
            SET @Error = 'The currency and rate can''t change once something has been paid.';
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

        INSERT INTO tblInvoices (iUserID, iCustomerID, strInvoiceNumber, dtmIssueDate, dtmDueDate, strReference, strNotes, bAmountsIncludeTax, strStatus,
            strCurrency, dblExchangeRate)
        VALUES (@iUserId, @iCustomerID, @strNumber, @dtIssueDate, @dtDueDate, NULLIF(LTRIM(RTRIM(@strReference)), ''),
                NULLIF(LTRIM(RTRIM(@strNotes)), ''), ISNULL(@bAmountsIncludeTax, 0), @NewStatus, @strCurrency, @dblExchangeRate);
        SET @iInvoiceID = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE tblInvoices
        SET iCustomerID = @iCustomerID, dtmIssueDate = @dtIssueDate, dtmDueDate = @dtDueDate,
            strReference = NULLIF(LTRIM(RTRIM(@strReference)), ''), strNotes = NULLIF(LTRIM(RTRIM(@strNotes)), ''),
            bAmountsIncludeTax = ISNULL(@bAmountsIncludeTax, 0), strStatus = @NewStatus,
            strCurrency = @strCurrency, dblExchangeRate = @dblExchangeRate, dtmUpdatedDate = GETDATE()
        WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId;
        DELETE FROM tblInvoiceLines WHERE iInvoiceID = @iInvoiceID;
    END

    INSERT INTO tblInvoiceLines (iInvoiceID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal,
        iItemID, iTrackingOption1ID, iTrackingOption2ID)
    SELECT @iInvoiceID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal,
        iItemID, iTrackingOption1ID, iTrackingOption2ID
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
-- Date:        21-Oct-2026
-- Description: A voided invoice's stock goes back and its cost of goods sold
--              journal is removed.
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
    EXEC dbo.uspSyncInventoryMovements @iUserId, 'Invoice', @iInvoiceID;
    COMMIT TRANSACTION;

    SELECT 'Invoice Voided' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddInvoicePayment]
(
    @iUserId         INT,
    @iInvoiceID      INT,
    @dtDate          DATE,
    @dblAmount       DECIMAL(19,4),
    @iAccountID      INT,              -- the bank account it was paid into
    @strReference    VARCHAR(200) = NULL,
    @dblExchangeRate DECIMAL(19,8) = NULL
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
-- Date:        21-Oct-2026
-- Description: For an invoice in another currency the amount is in that
--              currency: the bank gets it divided by @dblExchangeRate (the
--              day's rate), Accounts Receivable is cleared at the invoice's
--              rate, and the difference goes to 4950 Foreign Exchange Gains and
--              Losses. A foreign currency bank account keeps the foreign amount.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strStatus VARCHAR(10), @dblTotal DECIMAL(19,4), @strNumber VARCHAR(40), @strCustomer VARCHAR(200), @strKind VARCHAR(10),
            @strCurrency CHAR(3), @dblInvoiceRate DECIMAL(19,8), @dblHomeTotal DECIMAL(19,4);
    SELECT @strStatus = I.strStatus, @dblTotal = I.dblTotal, @strNumber = I.strInvoiceNumber, @strCustomer = C.strName, @strKind = I.strKind,
           @strCurrency = I.strCurrency, @dblInvoiceRate = I.dblExchangeRate, @dblHomeTotal = ISNULL(I.dblHomeTotal, I.dblTotal)
    FROM tblInvoices I JOIN tblCustomers C ON C.iCustomerID = I.iCustomerID
    WHERE I.iInvoiceID = @iInvoiceID AND I.iUserID = @iUserId;

    DECLARE @dblPaid DECIMAL(19,4) = ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID), 0);
    DECLARE @dblClearedHome DECIMAL(19,4) = ISNULL((SELECT SUM(ISNULL(dblReceivableHome, dblAmount)) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID), 0);
    DECLARE @iReceivableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 1200, 'Accounts Receivable', 'Asset', @iReceivableID OUTPUT;
    DECLARE @strBankCurrency CHAR(3) = (SELECT strCurrency FROM tblForeignCurrencyAccounts WHERE iAccountID = @iAccountID AND iUserID = @iUserId);
    IF @strCurrency IS NULL
        SET @dblExchangeRate = 1;
    ELSE
        SET @dblExchangeRate = ISNULL(NULLIF(@dblExchangeRate, 0), @dblInvoiceRate);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @strStatus IS NULL THEN 'That invoice was not found in this portfolio.'
        WHEN @strKind = 'CreditNote' THEN 'A credit note isn''t paid. Apply it to one of the customer''s invoices instead.'
        WHEN @strStatus <> 'Approved' THEN 'Only an approved invoice can be paid.'
        WHEN @dtDate IS NULL THEN 'Enter the date it was paid.'
        WHEN ISNULL(@dblAmount, 0) <= 0 THEN 'Enter the amount paid.'
        WHEN @dblAmount > @dblTotal - @dblPaid THEN 'That is more than is owing on this invoice.'
        WHEN ISNULL(@dblExchangeRate, 0) <= 0 THEN 'Enter the exchange rate on the day it was paid.'
        WHEN @iAccountID = @iReceivableID OR NOT EXISTS (
                SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                  AND strAccountType IN ('Asset', 'Liability') AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the bank account it was paid into.'
        WHEN @strBankCurrency IS NOT NULL AND @strBankCurrency <> ISNULL(@strCurrency, '')
            THEN 'That account is in ' + @strBankCurrency + ', but the invoice isn''t. Choose an account in the invoice''s currency.'
        END;

    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iPaymentID;
        RETURN;
    END

    DECLARE @strDescription VARCHAR(500) = LEFT('Payment for invoice ' + @strNumber + ' - ' + @strCustomer, 500);
    DECLARE @dblBankHome DECIMAL(19,4) = ROUND(@dblAmount / @dblExchangeRate, 2);
    -- The last payment clears whatever is left in Accounts Receivable, so rounding never leaves cents behind
    DECLARE @dblReceivableHome DECIMAL(19,4) = CASE WHEN @dblAmount = @dblTotal - @dblPaid THEN @dblHomeTotal - @dblClearedHome
                                                    ELSE ROUND(@dblAmount * @dblHomeTotal / @dblTotal, 2) END;
    DECLARE @dblGain DECIMAL(19,4) = @dblBankHome - @dblReceivableHome;

    BEGIN TRANSACTION;

    INSERT INTO tblInvoicePayments (iInvoiceID, dtmPaymentDate, dblAmount, iAccountID, strReference, dblExchangeRate, dblHomeAmount, dblReceivableHome)
    VALUES (@iInvoiceID, @dtDate, @dblAmount, @iAccountID, NULLIF(LTRIM(RTRIM(@strReference)), ''),
            CASE WHEN @strCurrency IS NULL THEN NULL ELSE @dblExchangeRate END, @dblBankHome, @dblReceivableHome);
    DECLARE @iPaymentID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'InvoicePayment', @iPaymentID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription, dblForeignAmount)
    VALUES (@iJournalID, @iAccountID, @dblBankHome, 0, @strDescription, CASE WHEN @strBankCurrency IS NOT NULL THEN @dblAmount END),
           (@iJournalID, @iReceivableID, 0, @dblReceivableHome, @strDescription, NULL);
    IF @dblGain <> 0
    BEGIN
        DECLARE @iFxID INT;
        EXEC dbo.uspGetLedgerAccount @iUserId, 4950, 'Foreign Exchange Gains and Losses', 'Revenue', @iFxID OUTPUT;
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iFxID, CASE WHEN @dblGain < 0 THEN -@dblGain ELSE 0 END, CASE WHEN @dblGain > 0 THEN @dblGain ELSE 0 END,
                LEFT('Exchange difference: ' + @strDescription, 500));
    END

    UPDATE tblInvoicePayments SET iJournalID = @iJournalID WHERE iPaymentID = @iPaymentID;

    COMMIT TRANSACTION;

    SELECT 'Success' AS Result, @iPaymentID AS iPaymentID;
END
GO

-- Matches an imported bank deposit (a Bank Import Suspense line) to an approved invoice: the allocation journal moves
-- the money out of suspense against Accounts Receivable, and the payment is recorded with that journal.
-- 21-Oct-2026: an invoice in another currency is refused (record its payment with the day's rate instead).
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

    DECLARE @strStatus VARCHAR(10), @strKind VARCHAR(10), @dblTotal DECIMAL(19,4), @strNumber VARCHAR(40), @strCustomer VARCHAR(200), @strCurrency CHAR(3);
    SELECT @strStatus = I.strStatus, @strKind = I.strKind, @dblTotal = I.dblTotal, @strNumber = I.strInvoiceNumber, @strCustomer = C.strName,
           @strCurrency = I.strCurrency
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
        WHEN @strCurrency IS NOT NULL THEN 'That invoice is in ' + @strCurrency + '. Record its payment on the invoice with the day''s rate.'
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
-- 21-Oct-2026: both must be in the portfolio's currency.
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

    DECLARE @cnCustomer INT, @cnTotal DECIMAL(19,4), @cnNumber VARCHAR(40), @cnCurrency CHAR(3), @cnOk BIT = 0;
    SELECT @cnCustomer = iCustomerID, @cnTotal = dblTotal, @cnNumber = strInvoiceNumber, @cnCurrency = strCurrency, @cnOk = 1
    FROM tblInvoices WHERE iInvoiceID = @iCreditNoteID AND iUserID = @iUserId AND strKind = 'CreditNote' AND strStatus = 'Approved';
    DECLARE @invCustomer INT, @invTotal DECIMAL(19,4), @invNumber VARCHAR(40), @invCurrency CHAR(3), @invOk BIT = 0;
    SELECT @invCustomer = iCustomerID, @invTotal = dblTotal, @invNumber = strInvoiceNumber, @invCurrency = strCurrency, @invOk = 1
    FROM tblInvoices WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId AND strKind = 'Invoice' AND strStatus = 'Approved';
    DECLARE @cnLeft DECIMAL(19,4) = @cnTotal - ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iCreditNoteID), 0);
    DECLARE @invLeft DECIMAL(19,4) = @invTotal - ISNULL((SELECT SUM(dblAmount) FROM tblInvoicePayments WHERE iInvoiceID = @iInvoiceID), 0);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @cnOk = 0 THEN 'Only an approved credit note can be applied.'
        WHEN @invOk = 0 THEN 'Choose an approved invoice.'
        WHEN @cnCustomer <> @invCustomer THEN 'The invoice is for a different customer.'
        WHEN @cnCurrency IS NOT NULL OR @invCurrency IS NOT NULL THEN 'Credit notes in another currency can''t be applied yet. Record the refund and the payment instead.'
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

PRINT 'Invoices with items, tracking and currencies migration done.';
GO
