-- =============================================
-- Migration: quotes and purchase orders
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Invoices > Quotes and
-- Bills > Purchase orders. Run it after 2026-10-21-26-invoice-books.sql. Safe to run more than once. It only adds things.
--
-- Quotes and purchase orders post nothing. A quote the customer accepts becomes a draft invoice, and a purchase order
-- becomes a draft bill when the supplier's bill arrives; the site copies the lines across and records which invoice or
-- bill it became.
--
-- What it adds:
--   * tblQuotesOrders (strKind Quote or PurchaseOrder) and tblQuoteOrderLines.
--   * uspSaveQuoteOrder, uspSetQuoteOrderStatus, uspMarkQuoteOrderConverted, uspDeleteQuoteOrder.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblBills', 'U') IS NULL OR COL_LENGTH('dbo.tblInvoiceLines', 'iItemID') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-25-bills.sql and 2026-10-21-26-invoice-books.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblQuotesOrders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblQuotesOrders (
        iDocumentID         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblQuotesOrders PRIMARY KEY,
        iUserID             INT NOT NULL,
        strKind             VARCHAR(20) NOT NULL,       -- Quote or PurchaseOrder
        strNumber           VARCHAR(40) NOT NULL,       -- QU-0001, PO-0001
        iCustomerID         INT NULL,                   -- a quote's customer
        iSupplierID         INT NULL,                   -- a purchase order's supplier
        dtmDate             DATE NOT NULL,
        dtmExpiry           DATE NULL,                  -- a quote's expiry, a purchase order's delivery date
        strTitle            NVARCHAR(200) NULL,
        strReference        VARCHAR(200) NULL,
        strNotes            VARCHAR(2000) NULL,         -- terms, delivery address, ...
        bAmountsIncludeTax  BIT NOT NULL,
        strStatus           VARCHAR(20) NOT NULL,       -- Draft, Sent, Accepted, Declined, Invoiced, Billed, Cancelled
        dblNet              DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblQuotesOrders_Net DEFAULT (0),
        dblTax              DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblQuotesOrders_Tax DEFAULT (0),
        dblTotal            DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblQuotesOrders_Total DEFAULT (0),
        strCurrency         CHAR(3) NULL,
        dblExchangeRate     DECIMAL(19,8) NULL,
        iConvertedID        INT NULL,                   -- the invoice or bill it became
        dtmCreatedDate      DATETIME NOT NULL CONSTRAINT DF_tblQuotesOrders_Created DEFAULT (GETDATE()),
        dtmUpdatedDate      DATETIME NOT NULL CONSTRAINT DF_tblQuotesOrders_Updated DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_tblQuotesOrders_Number ON dbo.tblQuotesOrders (iUserID, strKind, strNumber);
END
GO

IF OBJECT_ID('dbo.tblQuoteOrderLines', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblQuoteOrderLines (
        iLineID             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblQuoteOrderLines PRIMARY KEY,
        iDocumentID         INT NOT NULL CONSTRAINT FK_tblQuoteOrderLines_Document REFERENCES dbo.tblQuotesOrders (iDocumentID) ON DELETE CASCADE,
        iLineNumber         INT NOT NULL,
        strDescription      VARCHAR(500) NOT NULL,
        dblQuantity         DECIMAL(19,4) NOT NULL,
        dblUnitPrice        DECIMAL(19,4) NOT NULL,
        iAccountID          INT NOT NULL,
        iTaxCodeID          INT NULL,
        dblNet              DECIMAL(19,4) NOT NULL,
        dblTax              DECIMAL(19,4) NOT NULL,
        dblTotal            DECIMAL(19,4) NOT NULL,
        iItemID             INT NULL,
        iTrackingOption1ID  INT NULL,
        iTrackingOption2ID  INT NULL
    );
    CREATE INDEX IX_tblQuoteOrderLines_Document ON dbo.tblQuoteOrderLines (iDocumentID);
END
GO

-- Saves a quote or purchase order and its lines (<lines> as invoices and bills). A new one is numbered QU-0001 or
-- PO-0001 onwards. One that has become an invoice or bill can't be changed. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveQuoteOrder
    @iUserId            INT,
    @iDocumentID        INT = NULL,
    @strKind            VARCHAR(20),
    @iContactID         INT,            -- the customer (quote) or supplier (purchase order)
    @dtDate             DATE,
    @dtExpiry           DATE = NULL,
    @strTitle           NVARCHAR(200) = NULL,
    @strReference       VARCHAR(200) = NULL,
    @strNotes           VARCHAR(2000) = NULL,
    @bAmountsIncludeTax BIT,
    @strCurrency        CHAR(3) = NULL,
    @dblExchangeRate    DECIMAL(19,8) = NULL,
    @xmlLines           XML
AS
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
        WHEN ISNULL(@strKind, '') NOT IN ('Quote', 'PurchaseOrder') THEN 'Choose a quote or a purchase order.'
        WHEN @strKind = 'Quote' AND NOT EXISTS (SELECT 1 FROM tblCustomers WHERE iCustomerID = @iContactID AND iUserID = @iUserId)
            THEN 'Choose a customer.'
        WHEN @strKind = 'PurchaseOrder' AND NOT EXISTS (SELECT 1 FROM tblSuppliers WHERE iSupplierID = @iContactID AND iUserID = @iUserId)
            THEN 'Choose a supplier.'
        WHEN @dtDate IS NULL OR (@dtExpiry IS NOT NULL AND @dtExpiry < @dtDate) THEN 'Check the dates.'
        WHEN @strCurrency IS NOT NULL AND (LEN(@strCurrency) <> 3 OR ISNULL(@dblExchangeRate, 0) <= 0)
            THEN 'Enter the currency code and its exchange rate.'
        WHEN NOT EXISTS (SELECT 1 FROM @Lines) THEN 'Add at least one line.'
        WHEN EXISTS (SELECT 1 FROM @Lines WHERE ISNULL(strDescription, '') = '' OR dblQuantity IS NULL OR dblUnitPrice IS NULL
                                              OR dblNet IS NULL OR dblTax IS NULL OR dblTotal IS NULL)
            THEN 'Each line needs a description, quantity and price.'
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE NOT EXISTS (
                         SELECT 1 FROM tblChartOfAccounts A WHERE A.iAccountID = L.iAccountID AND (A.iUserID = @iUserId OR A.iUserID = 0)))
            THEN 'Choose an account for each line.'
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE L.iTaxCodeID IS NOT NULL AND NOT EXISTS (
                         SELECT 1 FROM tblTaxCodes T WHERE T.iTaxCodeID = L.iTaxCodeID AND T.iUserID = @iUserId))
            THEN 'That tax code was not found in this portfolio.'
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE L.iItemID IS NOT NULL AND NOT EXISTS (
                         SELECT 1 FROM tblInventoryItems I WHERE I.iItemID = L.iItemID AND I.iUserID = @iUserId))
            THEN 'That item was not found in this portfolio.'
        WHEN EXISTS (SELECT 1 FROM @Lines WHERE dblNet + dblTax <> dblTotal OR (iTaxCodeID IS NULL AND dblTax <> 0))
            THEN 'A line''s amounts don''t add up. Check its price and tax.'
        WHEN (SELECT SUM(dblTotal) FROM @Lines) <= 0 THEN 'The total must be more than zero.'
        END;

    DECLARE @strStatus VARCHAR(20);
    IF @Error IS NULL AND @iDocumentID IS NOT NULL
    BEGIN
        SELECT @strStatus = strStatus FROM tblQuotesOrders WHERE iDocumentID = @iDocumentID AND iUserID = @iUserId AND strKind = @strKind;
        IF @strStatus IS NULL
            SET @Error = 'That document was not found in this portfolio.';
        ELSE IF @strStatus IN ('Invoiced', 'Billed')
            SET @Error = 'This has already become an invoice or bill, so it can''t be changed.';
    END
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, @iDocumentID AS Id;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF @iDocumentID IS NULL
    BEGIN
        DECLARE @strPrefix VARCHAR(5) = CASE WHEN @strKind = 'Quote' THEN 'QU-' ELSE 'PO-' END;
        DECLARE @iNext INT = 1 + ISNULL((SELECT MAX(TRY_CAST(SUBSTRING(strNumber, 4, 20) AS INT)) FROM tblQuotesOrders WITH (UPDLOCK, HOLDLOCK)
                                         WHERE iUserID = @iUserId AND strKind = @strKind AND strNumber LIKE @strPrefix + '%'), 0);
        INSERT tblQuotesOrders (iUserID, strKind, strNumber, iCustomerID, iSupplierID, dtmDate, dtmExpiry, strTitle, strReference, strNotes,
            bAmountsIncludeTax, strStatus, strCurrency, dblExchangeRate)
        VALUES (@iUserId, @strKind, @strPrefix + RIGHT('0000' + CAST(@iNext AS VARCHAR(20)), 4),
            CASE WHEN @strKind = 'Quote' THEN @iContactID END, CASE WHEN @strKind = 'PurchaseOrder' THEN @iContactID END,
            @dtDate, @dtExpiry, NULLIF(LTRIM(RTRIM(@strTitle)), ''), NULLIF(LTRIM(RTRIM(@strReference)), ''), NULLIF(LTRIM(RTRIM(@strNotes)), ''),
            ISNULL(@bAmountsIncludeTax, 0), 'Draft', @strCurrency, @dblExchangeRate);
        SET @iDocumentID = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE tblQuotesOrders
        SET iCustomerID = CASE WHEN @strKind = 'Quote' THEN @iContactID END, iSupplierID = CASE WHEN @strKind = 'PurchaseOrder' THEN @iContactID END,
            dtmDate = @dtDate, dtmExpiry = @dtExpiry, strTitle = NULLIF(LTRIM(RTRIM(@strTitle)), ''),
            strReference = NULLIF(LTRIM(RTRIM(@strReference)), ''), strNotes = NULLIF(LTRIM(RTRIM(@strNotes)), ''),
            bAmountsIncludeTax = ISNULL(@bAmountsIncludeTax, 0), strCurrency = @strCurrency, dblExchangeRate = @dblExchangeRate, dtmUpdatedDate = GETDATE()
        WHERE iDocumentID = @iDocumentID AND iUserID = @iUserId;
        DELETE FROM tblQuoteOrderLines WHERE iDocumentID = @iDocumentID;
    END

    INSERT tblQuoteOrderLines (iDocumentID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal,
        iItemID, iTrackingOption1ID, iTrackingOption2ID)
    SELECT @iDocumentID, iLineNumber, strDescription, dblQuantity, dblUnitPrice, iAccountID, iTaxCodeID, dblNet, dblTax, dblTotal,
        iItemID, iTrackingOption1ID, iTrackingOption2ID
    FROM @Lines;
    UPDATE tblQuotesOrders
    SET dblNet = (SELECT SUM(dblNet) FROM @Lines), dblTax = (SELECT SUM(dblTax) FROM @Lines), dblTotal = (SELECT SUM(dblTotal) FROM @Lines)
    WHERE iDocumentID = @iDocumentID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iDocumentID AS Id;
END
GO

-- Draft, Sent, Accepted, Declined or Cancelled (Invoiced and Billed are set by uspMarkQuoteOrderConverted). Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspSetQuoteOrderStatus
    @iUserId     INT,
    @iDocumentID INT,
    @strStatus   VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    IF @strStatus NOT IN ('Draft', 'Sent', 'Accepted', 'Declined', 'Cancelled')
    BEGIN
        SELECT 'Error: Choose a status from the list.' AS Result;
        RETURN;
    END
    UPDATE tblQuotesOrders SET strStatus = @strStatus, dtmUpdatedDate = GETDATE()
    WHERE iDocumentID = @iDocumentID AND iUserID = @iUserId AND strStatus NOT IN ('Invoiced', 'Billed')
      AND (@strStatus NOT IN ('Accepted', 'Declined') OR strKind = 'Quote');
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That can''t be changed now.' END AS Result;
END
GO

-- Records the invoice (quote) or bill (purchase order) the document became. Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspMarkQuoteOrderConverted
    @iUserId      INT,
    @iDocumentID  INT,
    @iConvertedID INT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE D SET strStatus = CASE WHEN D.strKind = 'Quote' THEN 'Invoiced' ELSE 'Billed' END, iConvertedID = @iConvertedID, dtmUpdatedDate = GETDATE()
    FROM tblQuotesOrders D
    WHERE D.iDocumentID = @iDocumentID AND D.iUserID = @iUserId AND D.strStatus NOT IN ('Invoiced', 'Billed')
      AND ((D.strKind = 'Quote' AND EXISTS (SELECT 1 FROM tblInvoices I WHERE I.iInvoiceID = @iConvertedID AND I.iUserID = @iUserId))
        OR (D.strKind = 'PurchaseOrder' AND EXISTS (SELECT 1 FROM tblBills B WHERE B.iBillID = @iConvertedID AND B.iUserID = @iUserId)));
    IF @@ROWCOUNT = 1
        UPDATE tblBills SET iPurchaseOrderID = @iDocumentID WHERE iBillID = @iConvertedID AND iUserID = @iUserId
            AND EXISTS (SELECT 1 FROM tblQuotesOrders WHERE iDocumentID = @iDocumentID AND strKind = 'PurchaseOrder');
    SELECT CASE WHEN EXISTS (SELECT 1 FROM tblQuotesOrders WHERE iDocumentID = @iDocumentID AND iConvertedID = @iConvertedID)
                THEN 'Success' ELSE 'Error: That has already been converted.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteQuoteOrder
    @iUserId     INT,
    @iDocumentID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM tblQuotesOrders WHERE iDocumentID = @iDocumentID AND iUserID = @iUserId AND strStatus NOT IN ('Invoiced', 'Billed');
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: Only one that hasn''t become an invoice or bill can be deleted.' END AS Result;
END
GO

PRINT 'Quotes and purchase orders migration done.';
GO
