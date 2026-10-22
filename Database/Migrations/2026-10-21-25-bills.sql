-- =============================================
-- Migration: bills and payables
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Bills. Run it after
-- 2026-10-21-24-inventory.sql. Safe to run more than once. Nothing changes for a portfolio until it enters a bill.
--
-- What it adds:
--   * System account 2100 Accounts Payable (liability), if it isn't there already.
--   * tblSuppliers, tblBills (bills and supplier credit notes), tblBillLines, tblBillPayments, tblRepeatingBills.
--   * An approved bill posts a journal (source 'Bill') on its date: Cr Accounts Payable for the total, Dr each line's
--     account, with any GST, VAT or sales tax split to the tax control account by uspAddJournalLineWithTax. A supplier
--     credit note posts the other way round. A payment (source 'BillPayment') posts Dr Accounts Payable, Cr the bank.
--     A bill in another currency posts each line divided by its rate; paying it at a different rate posts the
--     difference to 4950 Foreign Exchange Gains and Losses. Lines can carry an inventory item and tracking options.
--   * uspSaveSupplier, uspDeleteSupplier, uspSaveBill, uspPostBillJournal, uspVoidBill, uspAddBillPayment,
--     uspDeleteBillPayment, uspApplySupplierCredit, uspSaveRepeatingBill, uspMarkRepeatingBill,
--     uspSetRepeatingBillActive, uspDeleteRepeatingBill.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.uspAddJournalLineWithTax', 'P') IS NULL OR OBJECT_ID('dbo.tblForeignCurrencyAccounts', 'U') IS NULL OR OBJECT_ID('dbo.uspSyncInventoryMovements', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-17-sales-tax.sql, 2026-10-21-23-foreign-currency.sql and 2026-10-21-24-inventory.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 2100, 'Accounts Payable', 'Liability', @iAccountID OUTPUT;
GO

IF OBJECT_ID('dbo.tblSuppliers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSuppliers (
        iSupplierID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblSuppliers PRIMARY KEY,
        iUserID            INT NOT NULL,
        strName            VARCHAR(200) NOT NULL,
        strContactName     VARCHAR(200) NULL,
        strEmail           VARCHAR(500) NULL,
        strPhone           VARCHAR(50) NULL,
        strAddress         VARCHAR(500) NULL,
        strTaxNumber       VARCHAR(50) NULL,           -- ABN, VAT number, ...
        strPaymentDetails  VARCHAR(500) NULL,          -- their bank details
        iDefaultAccountID  INT NULL,                   -- the expense account their bills usually go to
        iDefaultTaxCodeID  INT NULL,
        iDueDays           INT NULL,                   -- their payment terms
        bIsActive          BIT NOT NULL CONSTRAINT DF_tblSuppliers_Active DEFAULT (1),
        dtmCreatedDate     DATETIME NOT NULL CONSTRAINT DF_tblSuppliers_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblSuppliers_User ON dbo.tblSuppliers (iUserID, strName);
END
GO

IF OBJECT_ID('dbo.tblBills', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblBills (
        iBillID             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblBills PRIMARY KEY,
        iUserID             INT NOT NULL,
        iSupplierID         INT NOT NULL,
        strKind             VARCHAR(20) NOT NULL CONSTRAINT DF_tblBills_Kind DEFAULT ('Bill'),   -- Bill or SupplierCredit
        strBillNumber       VARCHAR(60) NULL,           -- the supplier's invoice number
        strReference        VARCHAR(200) NULL,          -- ours, e.g. the purchase order
        dtmIssueDate        DATE NOT NULL,
        dtmDueDate          DATE NOT NULL,
        strNotes            VARCHAR(1000) NULL,
        bAmountsIncludeTax  BIT NOT NULL,
        strStatus           VARCHAR(10) NOT NULL,       -- Draft, Approved or Void
        dblNet              DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblBills_Net DEFAULT (0),
        dblTax              DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblBills_Tax DEFAULT (0),
        dblTotal            DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblBills_Total DEFAULT (0),
        strCurrency         CHAR(3) NULL,               -- NULL: the portfolio's currency
        dblExchangeRate     DECIMAL(19,8) NULL,         -- foreign units per one unit of the portfolio's currency
        dblHomeTotal        DECIMAL(19,4) NULL,         -- the total as posted to Accounts Payable
        iPurchaseOrderID    INT NULL,
        iJournalID          INT NULL,
        dtmCreatedDate      DATETIME NOT NULL CONSTRAINT DF_tblBills_Created DEFAULT (GETDATE()),
        dtmUpdatedDate      DATETIME NOT NULL CONSTRAINT DF_tblBills_Updated DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblBills_User ON dbo.tblBills (iUserID, dtmDueDate);
    CREATE INDEX IX_tblBills_Supplier ON dbo.tblBills (iSupplierID);
END
GO

IF OBJECT_ID('dbo.tblBillLines', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblBillLines (
        iBillLineID         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblBillLines PRIMARY KEY,
        iBillID             INT NOT NULL CONSTRAINT FK_tblBillLines_Bill REFERENCES dbo.tblBills (iBillID) ON DELETE CASCADE,
        iLineNumber         INT NOT NULL,
        strDescription      VARCHAR(500) NOT NULL,
        dblQuantity         DECIMAL(19,4) NOT NULL,
        dblUnitPrice        DECIMAL(19,4) NOT NULL,
        iAccountID          INT NOT NULL,
        iTaxCodeID          INT NULL,
        dblNet              DECIMAL(19,4) NOT NULL,
        dblTax              DECIMAL(19,4) NOT NULL,
        dblTotal            DECIMAL(19,4) NOT NULL,
        iItemID             INT NULL,                   -- an inventory item
        iTrackingOption1ID  INT NULL,
        iTrackingOption2ID  INT NULL
    );
    CREATE INDEX IX_tblBillLines_Bill ON dbo.tblBillLines (iBillID);
END
GO

IF OBJECT_ID('dbo.tblBillPayments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblBillPayments (
        iPaymentID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblBillPayments PRIMARY KEY,
        iBillID             INT NOT NULL CONSTRAINT FK_tblBillPayments_Bill REFERENCES dbo.tblBills (iBillID),
        dtmPaymentDate      DATE NOT NULL,
        dblAmount           DECIMAL(19,4) NOT NULL,     -- in the bill's currency
        iAccountID          INT NOT NULL,               -- the bank account it was paid from
        strReference        VARCHAR(200) NULL,
        iJournalID          INT NULL,
        iCreditID           INT NULL,                   -- a supplier credit note applied instead of money
        iPairPaymentID      INT NULL,                   -- the matching row on the credit note
        dblExchangeRate     DECIMAL(19,8) NULL,
        dblHomeAmount       DECIMAL(19,4) NULL,         -- what left the bank, in the portfolio's currency
        dblPayableHome      DECIMAL(19,4) NULL,         -- what it cleared from Accounts Payable
        dtmCreatedDate      DATETIME NOT NULL CONSTRAINT DF_tblBillPayments_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblBillPayments_Bill ON dbo.tblBillPayments (iBillID);
END
GO

IF OBJECT_ID('dbo.tblRepeatingBills', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRepeatingBills (
        iRepeatingBillID    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRepeatingBills PRIMARY KEY,
        iUserID             INT NOT NULL,
        iSupplierID         INT NOT NULL,
        strName             NVARCHAR(200) NOT NULL,
        strFrequency        VARCHAR(20) NOT NULL,
        dtmStart            DATE NOT NULL,
        dtmEnd              DATE NULL,
        iPostedCount        INT NOT NULL CONSTRAINT DF_tblRepeatingBills_Posted DEFAULT (0),
        iDueDays            INT NOT NULL CONSTRAINT DF_tblRepeatingBills_Due DEFAULT (14),
        bApprove            BIT NOT NULL CONSTRAINT DF_tblRepeatingBills_Approve DEFAULT (0),
        bAmountsIncludeTax  BIT NOT NULL CONSTRAINT DF_tblRepeatingBills_Incl DEFAULT (0),
        strReference        VARCHAR(200) NULL,
        strNotes            VARCHAR(1000) NULL,
        strLinesXml         NVARCHAR(MAX) NOT NULL,
        bActive             BIT NOT NULL CONSTRAINT DF_tblRepeatingBills_Active DEFAULT (1),
        dtmLastCreated      DATE NULL
    );
    CREATE INDEX IX_tblRepeatingBills_User ON dbo.tblRepeatingBills (iUserID);
END
GO

-- =============================================
-- Suppliers
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveSupplier
    @iUserId           INT,
    @iSupplierID       INT = NULL,
    @strName           VARCHAR(200),
    @strContactName    VARCHAR(200) = NULL,
    @strEmail          VARCHAR(500) = NULL,
    @strPhone          VARCHAR(50) = NULL,
    @strAddress        VARCHAR(500) = NULL,
    @strTaxNumber      VARCHAR(50) = NULL,
    @strPaymentDetails VARCHAR(500) = NULL,
    @iDefaultAccountID INT = NULL,
    @iDefaultTaxCodeID INT = NULL,
    @iDueDays          INT = NULL,
    @bIsActive         BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    SET @iDefaultAccountID = NULLIF(@iDefaultAccountID, 0);
    SET @iDefaultTaxCodeID = NULLIF(@iDefaultTaxCodeID, 0);
    DECLARE @Error VARCHAR(200) = CASE
        WHEN @strName = '' THEN 'Enter the supplier''s name.'
        WHEN @iDefaultAccountID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iDefaultAccountID AND (iUserID = @iUserId OR iUserID = 0))
            THEN 'Choose the usual account from the list.'
        WHEN @iDefaultTaxCodeID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iDefaultTaxCodeID AND iUserID = @iUserId)
            THEN 'Choose the usual tax code from the list.'
        WHEN @iDueDays IS NOT NULL AND @iDueDays NOT BETWEEN 0 AND 365 THEN 'Days to pay must be between 0 and 365.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iSupplierID;
        RETURN;
    END

    IF ISNULL(@iSupplierID, 0) = 0
    BEGIN
        INSERT tblSuppliers (iUserID, strName, strContactName, strEmail, strPhone, strAddress, strTaxNumber, strPaymentDetails,
            iDefaultAccountID, iDefaultTaxCodeID, iDueDays, bIsActive)
        VALUES (@iUserId, @strName, NULLIF(LTRIM(RTRIM(@strContactName)), ''), NULLIF(LTRIM(RTRIM(@strEmail)), ''),
            NULLIF(LTRIM(RTRIM(@strPhone)), ''), NULLIF(LTRIM(RTRIM(@strAddress)), ''), NULLIF(LTRIM(RTRIM(@strTaxNumber)), ''),
            NULLIF(LTRIM(RTRIM(@strPaymentDetails)), ''), @iDefaultAccountID, @iDefaultTaxCodeID, @iDueDays, ISNULL(@bIsActive, 1));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS iSupplierID;
        RETURN;
    END

    UPDATE tblSuppliers
    SET strName = @strName, strContactName = NULLIF(LTRIM(RTRIM(@strContactName)), ''), strEmail = NULLIF(LTRIM(RTRIM(@strEmail)), ''),
        strPhone = NULLIF(LTRIM(RTRIM(@strPhone)), ''), strAddress = NULLIF(LTRIM(RTRIM(@strAddress)), ''),
        strTaxNumber = NULLIF(LTRIM(RTRIM(@strTaxNumber)), ''), strPaymentDetails = NULLIF(LTRIM(RTRIM(@strPaymentDetails)), ''),
        iDefaultAccountID = @iDefaultAccountID, iDefaultTaxCodeID = @iDefaultTaxCodeID, iDueDays = @iDueDays, bIsActive = ISNULL(@bIsActive, 1)
    WHERE iSupplierID = @iSupplierID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That supplier isn''t in this portfolio.' END AS Result, @iSupplierID AS iSupplierID;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteSupplier
    @iUserId     INT,
    @iSupplierID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM tblBills WHERE iSupplierID = @iSupplierID)
       OR EXISTS (SELECT 1 FROM tblRepeatingBills WHERE iSupplierID = @iSupplierID)
    BEGIN
        SELECT 'Error: This supplier has bills. Mark them inactive instead.' AS Result;
        RETURN;
    END
    DELETE FROM tblSuppliers WHERE iSupplierID = @iSupplierID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Supplier Deleted' ELSE 'Error: That supplier isn''t in this portfolio.' END AS Result;
END
GO

-- =============================================
-- Bills
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspPostBillJournal
    @iUserId INT,
    @iBillID INT
AS
-- Posts (or reposts) an approved bill's journal from its saved lines: Cr Accounts Payable for the total, Dr each line's
-- account with its tax split to the tax control account. A supplier credit note posts the other way round. A bill in
-- another currency posts each line and its tax divided by the rate, to the cent. Errors are thrown, so the caller's
-- transaction rolls everything back.
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @dtDate DATE, @strDescription VARCHAR(500), @strStatus VARCHAR(10), @bCredit BIT, @dblRate DECIMAL(19,8);
    SELECT @iJournalID = B.iJournalID, @dtDate = B.dtmIssueDate, @strStatus = B.strStatus,
           @bCredit = CASE WHEN B.strKind = 'SupplierCredit' THEN 1 ELSE 0 END,
           @dblRate = CASE WHEN B.strCurrency IS NULL THEN 1 ELSE B.dblExchangeRate END,
           @strDescription = LEFT(CASE WHEN B.strKind = 'SupplierCredit' THEN 'Supplier credit ' ELSE 'Bill ' END
                + ISNULL(B.strBillNumber + ' ', '') + '- ' + S.strName, 500)
    FROM tblBills B JOIN tblSuppliers S ON S.iSupplierID = B.iSupplierID
    WHERE B.iBillID = @iBillID AND B.iUserID = @iUserId;

    IF @strStatus IS NULL
        THROW 50801, 'That bill was not found in this portfolio.', 1;
    IF @strStatus <> 'Approved'
        THROW 50802, 'Only an approved bill is posted to the ledger.', 1;
    IF ISNULL(@dblRate, 0) <= 0
        THROW 50803, 'The bill needs an exchange rate.', 1;

    DECLARE @iPayableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 2100, 'Accounts Payable', 'Liability', @iPayableID OUTPUT;

    IF @iJournalID IS NOT NULL AND EXISTS (SELECT 1 FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId)
    BEGIN
        UPDATE tblJournal SET dtmTransactionDate = @dtDate, strDescription = @strDescription WHERE iJournalID = @iJournalID;
        DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID;
    END
    ELSE
    BEGIN
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
        VALUES (@iUserId, @dtDate, @strDescription, 'Bill', @iBillID, GETDATE());
        SET @iJournalID = SCOPE_IDENTITY();
        UPDATE tblBills SET iJournalID = @iJournalID WHERE iBillID = @iBillID;
    END

    DECLARE @dblHomeTotal DECIMAL(19,4) = 0, @iAfter INT;
    DECLARE @iAccountID INT, @iTaxCodeID INT, @dblGross DECIMAL(19,4), @dblTax DECIMAL(19,4), @strLine VARCHAR(500), @t1 INT, @t2 INT;
    DECLARE @dblDebit DECIMAL(19,4), @dblCredit DECIMAL(19,4);
    DECLARE curLines CURSOR LOCAL FAST_FORWARD FOR
        SELECT iAccountID, iTaxCodeID, ROUND(dblTotal / @dblRate, 2), ROUND(dblTax / @dblRate, 2), strDescription, iTrackingOption1ID, iTrackingOption2ID
        FROM tblBillLines WHERE iBillID = @iBillID AND dblTotal <> 0 ORDER BY iLineNumber;
    OPEN curLines;
    FETCH NEXT FROM curLines INTO @iAccountID, @iTaxCodeID, @dblGross, @dblTax, @strLine, @t1, @t2;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @dblHomeTotal += @dblGross;
        IF @bCredit = 1
            SET @dblGross = -@dblGross;
        SET @dblDebit = CASE WHEN @dblGross > 0 THEN @dblGross ELSE 0 END;
        SET @dblCredit = CASE WHEN @dblGross < 0 THEN -@dblGross ELSE 0 END;
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
    VALUES (@iJournalID, @iPayableID, CASE WHEN @bCredit = 1 THEN @dblHomeTotal ELSE 0 END, CASE WHEN @bCredit = 1 THEN 0 ELSE @dblHomeTotal END, @strDescription);
    UPDATE tblBills SET dblHomeTotal = @dblHomeTotal WHERE iBillID = @iBillID;

    IF (SELECT SUM(dblDebit) - SUM(dblCredit) FROM tblJournalLines WHERE iJournalID = @iJournalID) <> 0
        THROW 50804, 'The bill journal did not balance, so nothing was saved.', 1;

    IF OBJECT_ID('dbo.uspSyncInventoryMovements', 'P') IS NOT NULL
        EXEC dbo.uspSyncInventoryMovements @iUserId, 'Bill', @iBillID;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveBill
    @iUserId            INT,
    @iBillID            INT = NULL,     -- NULL adds one
    @iSupplierID        INT,
    @strKind            VARCHAR(20) = 'Bill',
    @strBillNumber      VARCHAR(60) = NULL,
    @dtIssueDate        DATE,
    @dtDueDate          DATE,
    @strReference       VARCHAR(200) = NULL,
    @strNotes           VARCHAR(1000) = NULL,
    @bAmountsIncludeTax BIT,
    @bApprove           BIT = 0,
    @strCurrency        CHAR(3) = NULL, -- NULL: the portfolio's currency
    @dblExchangeRate    DECIMAL(19,8) = NULL,
    @iPurchaseOrderID   INT = NULL,
    @xmlLines           XML             -- <lines><l d q p a t n x g i t1 t2/></lines>, as invoices plus item and tracking
AS
-- Saves a bill or supplier credit note and its lines (worked out by the site, checked here). An approved one's journal
-- is posted again, so the ledger always matches it. A tracked inventory item's line always goes to the item's
-- inventory account. Returns Result ('Success' or 'Error: ...') and iBillID.
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

    -- A tracked item's cost goes to its inventory account
    IF OBJECT_ID('dbo.tblInventoryItems', 'U') IS NOT NULL
        UPDATE L SET iAccountID = I.iInventoryAccountID
        FROM @Lines L JOIN tblInventoryItems I ON I.iItemID = L.iItemID AND I.iUserID = @iUserId
        WHERE I.bTracked = 1;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN NOT EXISTS (SELECT 1 FROM tblSuppliers WHERE iSupplierID = @iSupplierID AND iUserID = @iUserId)
            THEN 'Choose a supplier.'
        WHEN ISNULL(@strKind, '') NOT IN ('Bill', 'SupplierCredit')
            THEN 'Choose a bill or a supplier credit note.'
        WHEN @dtIssueDate IS NULL OR @dtDueDate IS NULL OR @dtDueDate < @dtIssueDate
            THEN 'Check the dates: the due date can''t be before the bill date.'
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
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE L.iItemID IS NOT NULL AND (OBJECT_ID('dbo.tblInventoryItems', 'U') IS NULL OR NOT EXISTS (
                         SELECT 1 FROM tblInventoryItems I WHERE I.iItemID = L.iItemID AND I.iUserID = @iUserId)))
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
            THEN 'The total must be more than zero.'
        END;

    IF @Error IS NULL AND EXISTS (SELECT 1 FROM @Lines WHERE iTaxCodeID IS NOT NULL)
       AND NOT EXISTS (SELECT 1 FROM tblSalesTaxSetup WHERE iUserID = @iUserId)
        SET @Error = 'Set up sales tax for this portfolio before using tax codes.';

    DECLARE @strStatus VARCHAR(10), @dblPaid DECIMAL(19,4) = 0;
    IF @Error IS NULL AND @iBillID IS NOT NULL
    BEGIN
        SELECT @strStatus = strStatus FROM tblBills WHERE iBillID = @iBillID AND iUserID = @iUserId;
        SELECT @dblPaid = ISNULL(SUM(dblAmount), 0) FROM tblBillPayments WHERE iBillID = @iBillID;
        IF @strStatus IS NULL
            SET @Error = 'That bill was not found in this portfolio.';
        ELSE IF @strStatus = 'Void'
            SET @Error = 'This bill has been voided, so it can''t be changed.';
        ELSE IF (SELECT SUM(dblTotal) FROM @Lines) < @dblPaid
            SET @Error = 'The total can''t be less than what has already been paid.';
        ELSE IF @dblPaid > 0 AND EXISTS (SELECT 1 FROM tblBills WHERE iBillID = @iBillID
                    AND (ISNULL(strCurrency, '') <> ISNULL(@strCurrency, '') OR ISNULL(dblExchangeRate, 0) <> ISNULL(@dblExchangeRate, 0)))
            SET @Error = 'The currency and rate can''t change once something has been paid.';
    END

    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, @iBillID AS iBillID;
        RETURN;
    END

    DECLARE @NewStatus VARCHAR(10) = CASE WHEN @strStatus = 'Approved' OR @bApprove = 1 THEN 'Approved' ELSE 'Draft' END;

    BEGIN TRANSACTION;
    IF @iBillID IS NULL
    BEGIN
        INSERT INTO tblBills (iUserID, iSupplierID, strKind, strBillNumber, dtmIssueDate, dtmDueDate, strReference, strNotes, bAmountsIncludeTax,
            strStatus, strCurrency, dblExchangeRate, iPurchaseOrderID)
        VALUES (@iUserId, @iSupplierID, @strKind, NULLIF(LTRIM(RTRIM(@strBillNumber)), ''), @dtIssueDate, @dtDueDate,
            NULLIF(LTRIM(RTRIM(@strReference)), ''), NULLIF(LTRIM(RTRIM(@strNotes)), ''), ISNULL(@bAmountsIncludeTax, 0), @NewStatus,
            @strCurrency, @dblExchangeRate, NULLIF(@iPurchaseOrderID, 0));
        SET @iBillID = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE tblBills
        SET iSupplierID = @iSupplierID, strKind = @strKind, strBillNumber = NULLIF(LTRIM(RTRIM(@strBillNumber)), ''),
            dtmIssueDate = @dtIssueDate, dtmDueDate = @dtDueDate, strReference = NULLIF(LTRIM(RTRIM(@strReference)), ''),
            strNotes = NULLIF(LTRIM(RTRIM(@strNotes)), ''), bAmountsIncludeTax = ISNULL(@bAmountsIncludeTax, 0), strStatus = @NewStatus,
            strCurrency = @strCurrency, dblExchangeRate = @dblExchangeRate, dtmUpdatedDate = GETDATE()
        WHERE iBillID = @iBillID AND iUserID = @iUserId;
        DELETE FROM tblBillLines WHERE iBillID = @iBillID;
    END

    INSERT INTO tblBillLines (iBillID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal,
        iItemID, iTrackingOption1ID, iTrackingOption2ID)
    SELECT @iBillID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal,
        iItemID, iTrackingOption1ID, iTrackingOption2ID
    FROM @Lines;

    UPDATE tblBills
    SET dblNet = (SELECT SUM(dblNet) FROM @Lines), dblTax = (SELECT SUM(dblTax) FROM @Lines), dblTotal = (SELECT SUM(dblTotal) FROM @Lines)
    WHERE iBillID = @iBillID;

    IF @NewStatus = 'Approved'
        EXEC dbo.uspPostBillJournal @iUserId, @iBillID;

    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iBillID AS iBillID;
END
GO

-- A draft is deleted. An approved bill with no payments is voided: its journal is deleted and it stays, marked Void.
-- Returns Result ('Bill Deleted', 'Bill Voided' or 'Error: ...').
CREATE OR ALTER PROCEDURE dbo.uspVoidBill
    @iUserId INT,
    @iBillID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strStatus VARCHAR(10), @iJournalID INT;
    SELECT @strStatus = strStatus, @iJournalID = iJournalID FROM tblBills WHERE iBillID = @iBillID AND iUserID = @iUserId;

    IF @strStatus IS NULL
    BEGIN
        SELECT 'Error: That bill was not found in this portfolio.' AS Result;
        RETURN;
    END
    IF @strStatus = 'Void'
    BEGIN
        SELECT 'Error: This bill has already been voided.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblBillPayments WHERE iBillID = @iBillID)
    BEGIN
        SELECT 'Error: This bill has payments. Delete them first.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF @strStatus = 'Draft'
    BEGIN
        DELETE FROM tblBills WHERE iBillID = @iBillID AND iUserID = @iUserId;
        COMMIT TRANSACTION;
        SELECT 'Bill Deleted' AS Result;
        RETURN;
    END

    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    UPDATE tblBills SET strStatus = 'Void', iJournalID = NULL, dtmUpdatedDate = GETDATE() WHERE iBillID = @iBillID;
    IF OBJECT_ID('dbo.uspSyncInventoryMovements', 'P') IS NOT NULL
        EXEC dbo.uspSyncInventoryMovements @iUserId, 'Bill', @iBillID;
    COMMIT TRANSACTION;
    SELECT 'Bill Voided' AS Result;
END
GO

-- =============================================
-- Payments
-- =============================================
-- Records money paid on an approved bill: Dr Accounts Payable, Cr the bank account. For a bill in another currency the
-- amount is in that currency: the bank gets it divided by @dblExchangeRate (the rate on the day it was paid), Accounts
-- Payable is cleared at the bill's own rate and the difference goes to 4950 Foreign Exchange Gains and Losses.
-- Returns Result ('Success' or 'Error: ...') and iPaymentID.
CREATE OR ALTER PROCEDURE dbo.uspAddBillPayment
    @iUserId         INT,
    @iBillID         INT,
    @dtDate          DATE,
    @dblAmount       DECIMAL(19,4),
    @iAccountID      INT,
    @strReference    VARCHAR(200) = NULL,
    @dblExchangeRate DECIMAL(19,8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strStatus VARCHAR(10), @strKind VARCHAR(20), @dblTotal DECIMAL(19,4), @dblHomeTotal DECIMAL(19,4), @strCurrency CHAR(3),
            @dblBillRate DECIMAL(19,8), @strDescription VARCHAR(500);
    SELECT @strStatus = B.strStatus, @strKind = B.strKind, @dblTotal = B.dblTotal, @dblHomeTotal = ISNULL(B.dblHomeTotal, B.dblTotal),
           @strCurrency = B.strCurrency, @dblBillRate = B.dblExchangeRate,
           @strDescription = LEFT('Payment of bill ' + ISNULL(B.strBillNumber + ' ', '') + '- ' + S.strName, 500)
    FROM tblBills B JOIN tblSuppliers S ON S.iSupplierID = B.iSupplierID
    WHERE B.iBillID = @iBillID AND B.iUserID = @iUserId;

    DECLARE @dblPaid DECIMAL(19,4) = ISNULL((SELECT SUM(dblAmount) FROM tblBillPayments WHERE iBillID = @iBillID), 0);
    DECLARE @dblClearedHome DECIMAL(19,4) = ISNULL((SELECT SUM(ISNULL(dblPayableHome, dblAmount)) FROM tblBillPayments WHERE iBillID = @iBillID), 0);
    DECLARE @iPayableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 2100, 'Accounts Payable', 'Liability', @iPayableID OUTPUT;
    DECLARE @strBankCurrency CHAR(3) = (SELECT strCurrency FROM tblForeignCurrencyAccounts WHERE iAccountID = @iAccountID AND iUserID = @iUserId);
    IF @strCurrency IS NULL
        SET @dblExchangeRate = 1;
    ELSE
        SET @dblExchangeRate = ISNULL(NULLIF(@dblExchangeRate, 0), @dblBillRate);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @strStatus IS NULL THEN 'That bill was not found in this portfolio.'
        WHEN @strKind = 'SupplierCredit' THEN 'A supplier credit isn''t paid. Apply it to one of their bills instead.'
        WHEN @strStatus <> 'Approved' THEN 'Only an approved bill can be paid.'
        WHEN @dtDate IS NULL THEN 'Enter the date it was paid.'
        WHEN ISNULL(@dblAmount, 0) <= 0 THEN 'Enter the amount paid.'
        WHEN @dblAmount > @dblTotal - @dblPaid THEN 'That is more than is owing on this bill.'
        WHEN ISNULL(@dblExchangeRate, 0) <= 0 THEN 'Enter the exchange rate on the day it was paid.'
        WHEN @iAccountID = @iPayableID OR NOT EXISTS (
                SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                  AND strAccountType IN ('Asset', 'Liability') AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the bank account it was paid from.'
        WHEN @strBankCurrency IS NOT NULL AND @strBankCurrency <> ISNULL(@strCurrency, '')
            THEN 'That account is in ' + @strBankCurrency + ', but the bill isn''t. Pay it from an account in the bill''s currency.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iPaymentID;
        RETURN;
    END

    DECLARE @dblBankHome DECIMAL(19,4) = ROUND(@dblAmount / @dblExchangeRate, 2);
    -- The last payment clears whatever is left in Accounts Payable, so rounding never leaves cents behind
    DECLARE @dblPayableHome DECIMAL(19,4) = CASE WHEN @dblAmount = @dblTotal - @dblPaid THEN @dblHomeTotal - @dblClearedHome
                                                 ELSE ROUND(@dblAmount * @dblHomeTotal / @dblTotal, 2) END;
    DECLARE @dblLoss DECIMAL(19,4) = @dblBankHome - @dblPayableHome;

    BEGIN TRANSACTION;
    INSERT INTO tblBillPayments (iBillID, dtmPaymentDate, dblAmount, iAccountID, strReference, dblExchangeRate, dblHomeAmount, dblPayableHome)
    VALUES (@iBillID, @dtDate, @dblAmount, @iAccountID, NULLIF(LTRIM(RTRIM(@strReference)), ''),
            CASE WHEN @strCurrency IS NULL THEN NULL ELSE @dblExchangeRate END, @dblBankHome, @dblPayableHome);
    DECLARE @iPaymentID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'BillPayment', @iPaymentID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription, dblForeignAmount)
    VALUES (@iJournalID, @iPayableID, @dblPayableHome, 0, @strDescription, NULL),
           (@iJournalID, @iAccountID, 0, @dblBankHome, @strDescription, CASE WHEN @strBankCurrency IS NOT NULL THEN -@dblAmount END);
    IF @dblLoss <> 0
    BEGIN
        DECLARE @iFxID INT;
        EXEC dbo.uspGetLedgerAccount @iUserId, 4950, 'Foreign Exchange Gains and Losses', 'Revenue', @iFxID OUTPUT;
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iFxID, CASE WHEN @dblLoss > 0 THEN @dblLoss ELSE 0 END, CASE WHEN @dblLoss < 0 THEN -@dblLoss ELSE 0 END,
                LEFT('Exchange difference: ' + @strDescription, 500));
    END

    UPDATE tblBillPayments SET iJournalID = @iJournalID WHERE iPaymentID = @iPaymentID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iPaymentID AS iPaymentID;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteBillPayment
    @iUserId    INT,
    @iPaymentID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @iPair INT, @bFound BIT = 0;
    SELECT @iJournalID = P.iJournalID, @iPair = P.iPairPaymentID, @bFound = 1
    FROM tblBillPayments P JOIN tblBills B ON B.iBillID = P.iBillID
    WHERE P.iPaymentID = @iPaymentID AND B.iUserID = @iUserId;

    IF @bFound = 0
    BEGIN
        SELECT 'Error: That payment was not found.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    DELETE FROM tblBillPayments WHERE iPaymentID IN (@iPaymentID, @iPair) OR iPairPaymentID = @iPaymentID;
    COMMIT TRANSACTION;
    SELECT 'Payment Deleted' AS Result;
END
GO

-- Applies an approved supplier credit note to an approved bill from the same supplier. No journal: both already went
-- through Accounts Payable. A row on each records it. Both must be in the portfolio's currency.
CREATE OR ALTER PROCEDURE dbo.uspApplySupplierCredit
    @iUserId   INT,
    @iCreditID INT,
    @iBillID   INT,
    @dtDate    DATE,
    @dblAmount DECIMAL(19,4)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @cSupplier INT, @cTotal DECIMAL(19,4), @cNumber VARCHAR(60), @cCurrency CHAR(3), @cOk BIT = 0;
    SELECT @cSupplier = iSupplierID, @cTotal = dblTotal, @cNumber = ISNULL(strBillNumber, CAST(iBillID AS VARCHAR(20))), @cCurrency = strCurrency, @cOk = 1
    FROM tblBills WHERE iBillID = @iCreditID AND iUserID = @iUserId AND strKind = 'SupplierCredit' AND strStatus = 'Approved';
    DECLARE @bSupplier INT, @bTotal DECIMAL(19,4), @bNumber VARCHAR(60), @bCurrency CHAR(3), @bOk BIT = 0;
    SELECT @bSupplier = iSupplierID, @bTotal = dblTotal, @bNumber = ISNULL(strBillNumber, CAST(iBillID AS VARCHAR(20))), @bCurrency = strCurrency, @bOk = 1
    FROM tblBills WHERE iBillID = @iBillID AND iUserID = @iUserId AND strKind = 'Bill' AND strStatus = 'Approved';
    DECLARE @cLeft DECIMAL(19,4) = @cTotal - ISNULL((SELECT SUM(dblAmount) FROM tblBillPayments WHERE iBillID = @iCreditID), 0);
    DECLARE @bLeft DECIMAL(19,4) = @bTotal - ISNULL((SELECT SUM(dblAmount) FROM tblBillPayments WHERE iBillID = @iBillID), 0);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @cOk = 0 THEN 'Only an approved supplier credit can be applied.'
        WHEN @bOk = 0 THEN 'Choose an approved bill.'
        WHEN @cSupplier <> @bSupplier THEN 'The bill is from a different supplier.'
        WHEN @cCurrency IS NOT NULL OR @bCurrency IS NOT NULL THEN 'Credits in another currency can''t be applied yet. Record the refund and the payment instead.'
        WHEN @dtDate IS NULL OR ISNULL(@dblAmount, 0) <= 0 THEN 'Enter the date and an amount more than nil.'
        WHEN @dblAmount > @cLeft THEN 'That is more than is left on the credit.'
        WHEN @dblAmount > @bLeft THEN 'That is more than is owing on the bill.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result;
        RETURN;
    END

    DECLARE @iPayableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 2100, 'Accounts Payable', 'Liability', @iPayableID OUTPUT;

    BEGIN TRANSACTION;
    INSERT tblBillPayments (iBillID, dtmPaymentDate, dblAmount, iAccountID, strReference, iCreditID, dblPayableHome)
    VALUES (@iBillID, @dtDate, @dblAmount, @iPayableID, LEFT('Supplier credit ' + @cNumber, 200), @iCreditID, @dblAmount);
    DECLARE @a INT = SCOPE_IDENTITY();
    INSERT tblBillPayments (iBillID, dtmPaymentDate, dblAmount, iAccountID, strReference, iCreditID, iPairPaymentID, dblPayableHome)
    VALUES (@iCreditID, @dtDate, @dblAmount, @iPayableID, LEFT('Applied to bill ' + @bNumber, 200), @iCreditID, @a, @dblAmount);
    UPDATE tblBillPayments SET iPairPaymentID = SCOPE_IDENTITY() WHERE iPaymentID = @a;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- Repeating bills
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveRepeatingBill
    @iUserId            INT,
    @iRepeatingBillID   INT = 0,
    @iSupplierID        INT,
    @strName            NVARCHAR(200),
    @strFrequency       VARCHAR(20),
    @dtmStart           DATE,
    @dtmEnd             DATE = NULL,
    @iDueDays           INT = 14,
    @bApprove           BIT = 0,
    @bAmountsIncludeTax BIT = 0,
    @strReference       VARCHAR(200) = NULL,
    @strNotes           VARCHAR(1000) = NULL,
    @strLinesXml        NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblSuppliers WHERE iSupplierID = @iSupplierID AND iUserID = @iUserId)
        OR @strFrequency NOT IN ('Weekly', 'Fortnightly', 'Monthly', 'Quarterly', 'HalfYearly', 'Yearly') OR @dtmStart IS NULL
        OR (@dtmEnd IS NOT NULL AND @dtmEnd < @dtmStart) OR ISNULL(@iDueDays, -1) NOT BETWEEN 0 AND 365 OR ISNULL(@strLinesXml, '') = ''
    BEGIN
        SELECT 'Error: Check the supplier, how often, the dates and the lines.' AS Result, NULL AS Id;
        RETURN;
    END
    IF ISNULL(@iRepeatingBillID, 0) > 0
    BEGIN
        UPDATE dbo.tblRepeatingBills
        SET iSupplierID = @iSupplierID, strName = LTRIM(RTRIM(@strName)), strFrequency = @strFrequency, dtmStart = @dtmStart, dtmEnd = @dtmEnd,
            iDueDays = @iDueDays, bApprove = ISNULL(@bApprove, 0), bAmountsIncludeTax = ISNULL(@bAmountsIncludeTax, 0),
            strReference = NULLIF(LTRIM(RTRIM(@strReference)), ''), strNotes = NULLIF(LTRIM(RTRIM(@strNotes)), ''), strLinesXml = @strLinesXml
        WHERE iRepeatingBillID = @iRepeatingBillID AND iUserID = @iUserId;
        SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating bill isn''t in this portfolio.' END AS Result, @iRepeatingBillID AS Id;
        RETURN;
    END
    INSERT dbo.tblRepeatingBills (iUserID, iSupplierID, strName, strFrequency, dtmStart, dtmEnd, iDueDays, bApprove, bAmountsIncludeTax,
        strReference, strNotes, strLinesXml)
    VALUES (@iUserId, @iSupplierID, LTRIM(RTRIM(@strName)), @strFrequency, @dtmStart, @dtmEnd, @iDueDays, ISNULL(@bApprove, 0), ISNULL(@bAmountsIncludeTax, 0),
        NULLIF(LTRIM(RTRIM(@strReference)), ''), NULLIF(LTRIM(RTRIM(@strNotes)), ''), @strLinesXml);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

-- Moves the count by @iBy (1 before creating a bill, -1 if it then couldn't be made), only from @iPostedCount.
CREATE OR ALTER PROCEDURE dbo.uspMarkRepeatingBill
    @iUserId          INT,
    @iRepeatingBillID INT,
    @iPostedCount     INT,
    @iBy              INT,
    @dtmCreated       DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblRepeatingBills SET iPostedCount = iPostedCount + @iBy, dtmLastCreated = ISNULL(@dtmCreated, dtmLastCreated)
    WHERE iRepeatingBillID = @iRepeatingBillID AND iUserID = @iUserId AND iPostedCount = @iPostedCount;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: It has already been created.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSetRepeatingBillActive
    @iUserId          INT,
    @iRepeatingBillID INT,
    @bActive          BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblRepeatingBills SET bActive = @bActive WHERE iRepeatingBillID = @iRepeatingBillID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating bill isn''t in this portfolio.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRepeatingBill
    @iUserId          INT,
    @iRepeatingBillID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.tblRepeatingBills WHERE iRepeatingBillID = @iRepeatingBillID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating bill isn''t in this portfolio.' END AS Result;
END
GO

PRINT 'Bills and payables migration done.';
GO
