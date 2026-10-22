-- =============================================
-- Migration: inventory
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Business > Inventory.
-- Run it after 2026-10-21-23-foreign-currency.sql and before 2026-10-21-25-bills.sql. Safe to run more than once.
--
-- How it works: an item is either tracked (stock on hand) or just a price list entry. For a tracked item, every
-- approved bill line adds stock at its cost, every approved invoice line takes stock out at the average cost, and a
-- stocktake adjustment moves the count up or down. The movements are worked through in date order (uspRecalcInventoryItem),
-- so a bill entered late changes the average cost of later sales, and each invoice's cost of goods sold journal
-- (source 'InvoiceCost': Dr cost of goods sold, Cr inventory) is posted again when its cost changes. Nothing dated in a
-- locked period is changed.
--
-- What it adds:
--   * System accounts 1300 Inventory (asset), 5700 Cost of Goods Sold and 5710 Inventory Adjustments (expenses).
--   * tblInventoryItems, tblInventoryMovements, tblStockAdjustments.
--   * tblInvoiceLines gets iItemID, iTrackingOption1ID and iTrackingOption2ID (used by 2026-10-21-26-invoice-books.sql).
--   * uspSaveInventoryItem, uspDeleteInventoryItem, uspSyncInventoryMovements, uspRecalcInventoryItem,
--     uspPostInvoiceCostJournal, uspSaveStockAdjustment, uspPostStockAdjustmentJournal, uspDeleteStockAdjustment.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblForeignCurrencyAccounts', 'U') IS NULL OR OBJECT_ID('dbo.fnLockedTo', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-lock-periods.sql and 2026-10-21-23-foreign-currency.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 1300, 'Inventory', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 5700, 'Cost of Goods Sold', 'Expense', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 5710, 'Inventory Adjustments', 'Expense', @iAccountID OUTPUT;
GO

IF OBJECT_ID('dbo.tblInventoryItems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblInventoryItems (
        iItemID              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblInventoryItems PRIMARY KEY,
        iUserID              INT NOT NULL,
        strCode              VARCHAR(30) NOT NULL,
        strName              NVARCHAR(200) NOT NULL,
        strDescription       NVARCHAR(500) NULL,         -- goes on invoice and bill lines
        bTracked             BIT NOT NULL,               -- 1: quantity on hand and average cost are kept
        dblSalePrice         DECIMAL(19,4) NULL,
        dblPurchasePrice     DECIMAL(19,4) NULL,
        iSalesAccountID      INT NULL,
        iPurchaseAccountID   INT NULL,                   -- for an item that isn't tracked
        iInventoryAccountID  INT NULL,                   -- for a tracked item
        iCogsAccountID       INT NULL,                   -- for a tracked item
        iSalesTaxCodeID      INT NULL,
        iPurchaseTaxCodeID   INT NULL,
        bActive              BIT NOT NULL CONSTRAINT DF_tblInventoryItems_Active DEFAULT (1),
        dblQuantity          DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblInventoryItems_Qty DEFAULT (0),
        dblTotalCost         DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblInventoryItems_Cost DEFAULT (0)
    );
    CREATE UNIQUE INDEX UX_tblInventoryItems_Code ON dbo.tblInventoryItems (iUserID, strCode);
END
GO

IF OBJECT_ID('dbo.tblInventoryMovements', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblInventoryMovements (
        iMovementID   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblInventoryMovements PRIMARY KEY,
        iUserID       INT NOT NULL,
        iItemID       INT NOT NULL CONSTRAINT FK_tblInventoryMovements_Item REFERENCES dbo.tblInventoryItems (iItemID),
        dtmDate       DATE NOT NULL,
        strSource     VARCHAR(10) NOT NULL,              -- Invoice, Bill or Adjust
        iSourceID     INT NOT NULL,
        iLineID       INT NULL,                          -- the invoice or bill line
        dblQuantity   DECIMAL(19,4) NOT NULL,            -- in positive, out negative
        dblCost       DECIMAL(19,4) NOT NULL,            -- in the portfolio's currency, same sign as the quantity
        bFixedCost    BIT NOT NULL                       -- 1: a purchase at its own cost; 0: at the average cost
    );
    CREATE INDEX IX_tblInventoryMovements_Item ON dbo.tblInventoryMovements (iItemID, dtmDate);
    CREATE INDEX IX_tblInventoryMovements_Source ON dbo.tblInventoryMovements (strSource, iSourceID);
END
GO

IF OBJECT_ID('dbo.tblStockAdjustments', 'U') IS NULL
    CREATE TABLE dbo.tblStockAdjustments (
        iAdjustmentID  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblStockAdjustments PRIMARY KEY,
        iUserID        INT NOT NULL,
        dtmDate        DATE NOT NULL,
        strNote        NVARCHAR(500) NULL,
        iAccountID     INT NOT NULL,                     -- the other side, usually 5710 Inventory Adjustments
        iJournalID     INT NULL,
        dtmCreatedDate DATETIME NOT NULL CONSTRAINT DF_tblStockAdjustments_Created DEFAULT (GETDATE())
    );
GO

IF COL_LENGTH('dbo.tblInvoiceLines', 'iItemID') IS NULL
    ALTER TABLE dbo.tblInvoiceLines ADD iItemID INT NULL, iTrackingOption1ID INT NULL, iTrackingOption2ID INT NULL;
GO

-- =============================================
-- Items
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveInventoryItem
    @iUserId             INT,
    @iItemID             INT = 0,
    @strCode             VARCHAR(30),
    @strName             NVARCHAR(200),
    @strDescription      NVARCHAR(500) = NULL,
    @bTracked            BIT,
    @dblSalePrice        DECIMAL(19,4) = NULL,
    @dblPurchasePrice    DECIMAL(19,4) = NULL,
    @iSalesAccountID     INT = NULL,
    @iPurchaseAccountID  INT = NULL,
    @iInventoryAccountID INT = NULL,
    @iCogsAccountID      INT = NULL,
    @iSalesTaxCodeID     INT = NULL,
    @iPurchaseTaxCodeID  INT = NULL,
    @bActive             BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET @strCode = UPPER(LTRIM(RTRIM(ISNULL(@strCode, ''))));
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    SET @iSalesTaxCodeID = NULLIF(@iSalesTaxCodeID, 0);
    SET @iPurchaseTaxCodeID = NULLIF(@iPurchaseTaxCodeID, 0);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @strCode = '' OR @strName = '' THEN 'Enter the item''s code and name.'
        WHEN EXISTS (SELECT 1 FROM tblInventoryItems WHERE iUserID = @iUserId AND strCode = @strCode AND iItemID <> ISNULL(@iItemID, 0))
            THEN 'There is already an item with that code.'
        WHEN ISNULL(@bTracked, 0) = 1 AND (NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iInventoryAccountID AND (iUserID = @iUserId OR iUserID = 0) AND strAccountType = 'Asset')
                                        OR NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iCogsAccountID AND (iUserID = @iUserId OR iUserID = 0) AND strAccountType = 'Expense'))
            THEN 'A tracked item needs an inventory (asset) account and a cost of goods sold (expense) account.'
        WHEN @iSalesAccountID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iSalesAccountID AND (iUserID = @iUserId OR iUserID = 0))
            THEN 'Choose the sales account from the list.'
        WHEN @iPurchaseAccountID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iPurchaseAccountID AND (iUserID = @iUserId OR iUserID = 0))
            THEN 'Choose the purchases account from the list.'
        WHEN (@iSalesTaxCodeID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iSalesTaxCodeID AND iUserID = @iUserId))
          OR (@iPurchaseTaxCodeID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iPurchaseTaxCodeID AND iUserID = @iUserId))
            THEN 'Choose tax codes from the list.'
        WHEN ISNULL(@iItemID, 0) > 0 AND NOT EXISTS (SELECT 1 FROM tblInventoryItems WHERE iItemID = @iItemID AND iUserID = @iUserId)
            THEN 'That item isn''t in this portfolio.'
        WHEN ISNULL(@iItemID, 0) > 0 AND EXISTS (SELECT 1 FROM tblInventoryMovements WHERE iItemID = @iItemID)
             AND EXISTS (SELECT 1 FROM tblInventoryItems WHERE iItemID = @iItemID AND (bTracked <> ISNULL(@bTracked, 0)
                         OR iInventoryAccountID <> @iInventoryAccountID OR iCogsAccountID <> @iCogsAccountID))
            THEN 'This item already has stock movements, so whether it is tracked and its inventory and cost accounts can''t change.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END

    IF ISNULL(@bTracked, 0) = 0
        SELECT @iInventoryAccountID = NULL, @iCogsAccountID = NULL;

    IF ISNULL(@iItemID, 0) = 0
    BEGIN
        INSERT tblInventoryItems (iUserID, strCode, strName, strDescription, bTracked, dblSalePrice, dblPurchasePrice, iSalesAccountID,
            iPurchaseAccountID, iInventoryAccountID, iCogsAccountID, iSalesTaxCodeID, iPurchaseTaxCodeID, bActive)
        VALUES (@iUserId, @strCode, @strName, NULLIF(LTRIM(RTRIM(@strDescription)), ''), ISNULL(@bTracked, 0), @dblSalePrice, @dblPurchasePrice,
            @iSalesAccountID, @iPurchaseAccountID, @iInventoryAccountID, @iCogsAccountID, @iSalesTaxCodeID, @iPurchaseTaxCodeID, ISNULL(@bActive, 1));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END

    UPDATE tblInventoryItems
    SET strCode = @strCode, strName = @strName, strDescription = NULLIF(LTRIM(RTRIM(@strDescription)), ''), bTracked = ISNULL(@bTracked, 0),
        dblSalePrice = @dblSalePrice, dblPurchasePrice = @dblPurchasePrice, iSalesAccountID = @iSalesAccountID,
        iPurchaseAccountID = @iPurchaseAccountID, iInventoryAccountID = @iInventoryAccountID, iCogsAccountID = @iCogsAccountID,
        iSalesTaxCodeID = @iSalesTaxCodeID, iPurchaseTaxCodeID = @iPurchaseTaxCodeID, bActive = ISNULL(@bActive, 1)
    WHERE iItemID = @iItemID AND iUserID = @iUserId;
    SELECT 'Success' AS Result, @iItemID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteInventoryItem
    @iUserId INT,
    @iItemID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM tblInventoryMovements WHERE iItemID = @iItemID)
       OR EXISTS (SELECT 1 FROM tblInvoiceLines WHERE iItemID = @iItemID)
       OR (OBJECT_ID('dbo.tblBillLines', 'U') IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.tblBillLines WHERE iItemID = @iItemID))
    BEGIN
        SELECT 'Error: This item is on invoices, bills or stock adjustments. Make it inactive instead.' AS Result;
        RETURN;
    END
    DELETE FROM tblInventoryItems WHERE iItemID = @iItemID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That item isn''t in this portfolio.' END AS Result;
END
GO

-- =============================================
-- Cost journals
-- =============================================
-- Posts (or reposts, or removes) an invoice's cost of goods sold journal from its stock movements: for each tracked
-- item, Dr its cost of goods sold account and Cr its inventory account with the cost taken out (a credit note's
-- returned stock the other way round), carrying the line's tracking options. Errors are thrown.
CREATE OR ALTER PROCEDURE dbo.uspPostInvoiceCostJournal
    @iUserId    INT,
    @iInvoiceID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Cost TABLE (iCogsAccountID INT, iInventoryAccountID INT, t1 INT, t2 INT, dblCost DECIMAL(19,4));
    INSERT @Cost
    SELECT I.iCogsAccountID, I.iInventoryAccountID, L.iTrackingOption1ID, L.iTrackingOption2ID, -SUM(M.dblCost)
    FROM tblInventoryMovements M
    JOIN tblInventoryItems I ON I.iItemID = M.iItemID
    LEFT JOIN tblInvoiceLines L ON L.iInvoiceLineID = M.iLineID
    WHERE M.strSource = 'Invoice' AND M.iSourceID = @iInvoiceID AND M.iUserID = @iUserId
    GROUP BY I.iCogsAccountID, I.iInventoryAccountID, L.iTrackingOption1ID, L.iTrackingOption2ID;
    DELETE FROM @Cost WHERE dblCost = 0;

    DECLARE @iJournalID INT = (SELECT TOP 1 iJournalID FROM tblJournal WHERE iUserID = @iUserId AND strSourceModule = 'InvoiceCost' AND iSourceID = @iInvoiceID);
    IF NOT EXISTS (SELECT 1 FROM @Cost)
    BEGIN
        IF @iJournalID IS NOT NULL
            DELETE FROM tblJournal WHERE iJournalID = @iJournalID;
        RETURN;
    END

    DECLARE @dtDate DATE, @strDescription VARCHAR(500);
    SELECT @dtDate = dtmIssueDate, @strDescription = LEFT('Cost of goods sold - ' + CASE WHEN strKind = 'CreditNote' THEN 'credit note ' ELSE 'invoice ' END + strInvoiceNumber, 500)
    FROM tblInvoices WHERE iInvoiceID = @iInvoiceID AND iUserID = @iUserId;

    IF @iJournalID IS NULL
    BEGIN
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
        VALUES (@iUserId, @dtDate, @strDescription, 'InvoiceCost', @iInvoiceID, GETDATE());
        SET @iJournalID = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE tblJournal SET dtmTransactionDate = @dtDate, strDescription = @strDescription WHERE iJournalID = @iJournalID;
        DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID;
    END

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription, iTrackingOption1ID, iTrackingOption2ID)
    SELECT @iJournalID, iCogsAccountID, CASE WHEN dblCost > 0 THEN dblCost ELSE 0 END, CASE WHEN dblCost < 0 THEN -dblCost ELSE 0 END, @strDescription, t1, t2 FROM @Cost
    UNION ALL
    SELECT @iJournalID, iInventoryAccountID, CASE WHEN dblCost < 0 THEN -dblCost ELSE 0 END, CASE WHEN dblCost > 0 THEN dblCost ELSE 0 END, @strDescription, NULL, NULL FROM @Cost;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspPostStockAdjustmentJournal
    @iUserId       INT,
    @iAdjustmentID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT, @dtDate DATE, @iAccountID INT, @strDescription VARCHAR(500);
    SELECT @iJournalID = iJournalID, @dtDate = dtmDate, @iAccountID = iAccountID,
           @strDescription = LEFT('Stock adjustment' + ISNULL(' - ' + CAST(strNote AS VARCHAR(400)), ''), 500)
    FROM tblStockAdjustments WHERE iAdjustmentID = @iAdjustmentID AND iUserID = @iUserId;
    IF @dtDate IS NULL
        RETURN;

    DECLARE @Cost TABLE (iInventoryAccountID INT, dblCost DECIMAL(19,4));
    INSERT @Cost
    SELECT I.iInventoryAccountID, SUM(M.dblCost)
    FROM tblInventoryMovements M JOIN tblInventoryItems I ON I.iItemID = M.iItemID
    WHERE M.strSource = 'Adjust' AND M.iSourceID = @iAdjustmentID AND M.iUserID = @iUserId
    GROUP BY I.iInventoryAccountID;
    DELETE FROM @Cost WHERE dblCost = 0;

    IF NOT EXISTS (SELECT 1 FROM @Cost)
    BEGIN
        IF @iJournalID IS NOT NULL
            DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
        UPDATE tblStockAdjustments SET iJournalID = NULL WHERE iAdjustmentID = @iAdjustmentID;
        RETURN;
    END

    IF @iJournalID IS NULL OR NOT EXISTS (SELECT 1 FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId)
    BEGIN
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
        VALUES (@iUserId, @dtDate, @strDescription, 'StockAdjustment', @iAdjustmentID, GETDATE());
        SET @iJournalID = SCOPE_IDENTITY();
        UPDATE tblStockAdjustments SET iJournalID = @iJournalID WHERE iAdjustmentID = @iAdjustmentID;
    END
    ELSE
        DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID;

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    SELECT @iJournalID, iInventoryAccountID, CASE WHEN dblCost > 0 THEN dblCost ELSE 0 END, CASE WHEN dblCost < 0 THEN -dblCost ELSE 0 END, @strDescription FROM @Cost
    UNION ALL
    SELECT @iJournalID, @iAccountID, CASE WHEN SUM(dblCost) < 0 THEN -SUM(dblCost) ELSE 0 END, CASE WHEN SUM(dblCost) > 0 THEN SUM(dblCost) ELSE 0 END, @strDescription
    FROM @Cost HAVING SUM(dblCost) <> 0;
END
GO

-- =============================================
-- Average cost
-- =============================================
-- Works through an item's movements in date order (stock in before stock out on the same day), costing each one taken
-- out at the average cost at that point, and keeps the item's quantity and total cost. Movements in a locked period
-- keep the cost they were posted at. Journals whose cost changed are posted again.
CREATE OR ALTER PROCEDURE dbo.uspRecalcInventoryItem
    @iUserId INT,
    @iItemID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @dtLock DATE = dbo.fnLockedTo(@iUserId);
    DECLARE @dblQty DECIMAL(19,4) = 0, @dblCostSum DECIMAL(19,4) = 0, @dblAvg DECIMAL(19,8);
    SELECT @dblAvg = ISNULL(dblPurchasePrice, 0) FROM tblInventoryItems WHERE iItemID = @iItemID AND iUserID = @iUserId;
    IF @dblAvg IS NULL
        RETURN;

    DECLARE @Changed TABLE (strSource VARCHAR(10), iSourceID INT, PRIMARY KEY (strSource, iSourceID));
    DECLARE @iMovementID INT, @dtDate DATE, @strSource VARCHAR(10), @iSourceID INT, @q DECIMAL(19,4), @c DECIMAL(19,4), @bFixed BIT, @new DECIMAL(19,4);
    DECLARE curMoves CURSOR LOCAL FAST_FORWARD FOR
        SELECT iMovementID, dtmDate, strSource, iSourceID, dblQuantity, dblCost, bFixedCost
        FROM tblInventoryMovements WHERE iItemID = @iItemID AND iUserID = @iUserId
        ORDER BY dtmDate, CASE WHEN dblQuantity > 0 THEN 0 ELSE 1 END, iMovementID;
    OPEN curMoves;
    FETCH NEXT FROM curMoves INTO @iMovementID, @dtDate, @strSource, @iSourceID, @q, @c, @bFixed;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @bFixed = 1 OR (@dtLock IS NOT NULL AND @dtDate <= @dtLock)
            SET @new = @c;
        ELSE IF @q < 0 AND @dblQty + @q = 0
            SET @new = -@dblCostSum;                       -- the last of the stock takes whatever cost is left
        ELSE
            SET @new = ROUND(@q * @dblAvg, 2);

        IF @new <> @c
        BEGIN
            UPDATE tblInventoryMovements SET dblCost = @new WHERE iMovementID = @iMovementID;
            IF @strSource IN ('Invoice', 'Adjust') AND NOT EXISTS (SELECT 1 FROM @Changed WHERE strSource = @strSource AND iSourceID = @iSourceID)
                INSERT @Changed VALUES (@strSource, @iSourceID);
        END

        SET @dblQty += @q;
        SET @dblCostSum += @new;
        IF @dblQty > 0
            SET @dblAvg = @dblCostSum / @dblQty;
        FETCH NEXT FROM curMoves INTO @iMovementID, @dtDate, @strSource, @iSourceID, @q, @c, @bFixed;
    END
    CLOSE curMoves;
    DEALLOCATE curMoves;

    UPDATE tblInventoryItems SET dblQuantity = @dblQty, dblTotalCost = @dblCostSum WHERE iItemID = @iItemID;

    DECLARE curChanged CURSOR LOCAL FAST_FORWARD FOR SELECT strSource, iSourceID FROM @Changed;
    OPEN curChanged;
    FETCH NEXT FROM curChanged INTO @strSource, @iSourceID;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @strSource = 'Invoice'
            EXEC dbo.uspPostInvoiceCostJournal @iUserId, @iSourceID;
        ELSE
            EXEC dbo.uspPostStockAdjustmentJournal @iUserId, @iSourceID;
        FETCH NEXT FROM curChanged INTO @strSource, @iSourceID;
    END
    CLOSE curChanged;
    DEALLOCATE curChanged;
END
GO

-- Rebuilds an invoice's or bill's stock movements from its lines (none unless it is approved), then recosts the items
-- and posts the invoice's cost of goods sold journal. Called by the invoice and bill procs after posting or voiding.
CREATE OR ALTER PROCEDURE dbo.uspSyncInventoryMovements
    @iUserId   INT,
    @strSource VARCHAR(10),
    @iSourceID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Items TABLE (iItemID INT PRIMARY KEY);
    INSERT @Items SELECT DISTINCT iItemID FROM tblInventoryMovements WHERE strSource = @strSource AND iSourceID = @iSourceID AND iUserID = @iUserId;
    DELETE FROM tblInventoryMovements WHERE strSource = @strSource AND iSourceID = @iSourceID AND iUserID = @iUserId;

    IF @strSource = 'Invoice'
        INSERT tblInventoryMovements (iUserID, iItemID, dtmDate, strSource, iSourceID, iLineID, dblQuantity, dblCost, bFixedCost)
        SELECT @iUserId, L.iItemID, I.dtmIssueDate, 'Invoice', I.iInvoiceID, L.iInvoiceLineID,
               CASE WHEN I.strKind = 'CreditNote' THEN L.dblQuantity ELSE -L.dblQuantity END, 0, 0
        FROM tblInvoices I
        JOIN tblInvoiceLines L ON L.iInvoiceID = I.iInvoiceID
        JOIN tblInventoryItems T ON T.iItemID = L.iItemID AND T.iUserID = @iUserId AND T.bTracked = 1
        WHERE I.iInvoiceID = @iSourceID AND I.iUserID = @iUserId AND I.strStatus = 'Approved' AND L.dblQuantity <> 0;
    ELSE IF @strSource = 'Bill' AND OBJECT_ID('dbo.tblBillLines', 'U') IS NOT NULL
        EXEC sp_executesql N'
        INSERT tblInventoryMovements (iUserID, iItemID, dtmDate, strSource, iSourceID, iLineID, dblQuantity, dblCost, bFixedCost)
        SELECT @iUserId, L.iItemID, B.dtmIssueDate, ''Bill'', B.iBillID, L.iBillLineID,
               CASE WHEN B.strKind = ''SupplierCredit'' THEN -L.dblQuantity ELSE L.dblQuantity END,
               (ROUND(L.dblTotal / ISNULL(B.dblExchangeRate, 1), 2) - ROUND(L.dblTax / ISNULL(B.dblExchangeRate, 1), 2))
                   * CASE WHEN B.strKind = ''SupplierCredit'' THEN -1 ELSE 1 END, 1
        FROM tblBills B
        JOIN tblBillLines L ON L.iBillID = B.iBillID
        JOIN tblInventoryItems T ON T.iItemID = L.iItemID AND T.iUserID = @iUserId AND T.bTracked = 1
        WHERE B.iBillID = @iSourceID AND B.iUserID = @iUserId AND B.strStatus = ''Approved'' AND L.dblQuantity <> 0;',
        N'@iUserId INT, @iSourceID INT', @iUserId, @iSourceID;

    INSERT @Items SELECT DISTINCT M.iItemID FROM tblInventoryMovements M
    WHERE M.strSource = @strSource AND M.iSourceID = @iSourceID AND M.iUserID = @iUserId AND NOT EXISTS (SELECT 1 FROM @Items X WHERE X.iItemID = M.iItemID);

    DECLARE @iItemID INT;
    DECLARE curItems CURSOR LOCAL FAST_FORWARD FOR SELECT iItemID FROM @Items;
    OPEN curItems;
    FETCH NEXT FROM curItems INTO @iItemID;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.uspRecalcInventoryItem @iUserId, @iItemID;
        FETCH NEXT FROM curItems INTO @iItemID;
    END
    CLOSE curItems;
    DEALLOCATE curItems;

    IF @strSource = 'Invoice'
        EXEC dbo.uspPostInvoiceCostJournal @iUserId, @iSourceID;
END
GO

-- =============================================
-- Stocktakes and adjustments
-- =============================================
-- Changes the stock on hand: <items><i id="item" q="change, + or -" c="unit cost of stock found (optional)"/></items>.
-- Stock taken out (or found, with no cost given) is at the average cost. Posts a journal (source 'StockAdjustment')
-- between the inventory account and @iAccountID. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveStockAdjustment
    @iUserId    INT,
    @dtDate     DATE,
    @iAccountID INT,
    @strNote    NVARCHAR(500) = NULL,
    @xmlItems   XML
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Items TABLE (iItemID INT, dblQuantity DECIMAL(19,4), dblUnitCost DECIMAL(19,4));
    INSERT @Items
    SELECT X.i.value('@id', 'INT'), X.i.value('@q', 'DECIMAL(19,4)'), X.i.value('@c', 'DECIMAL(19,4)')
    FROM @xmlItems.nodes('/items/i') AS X(i);
    DELETE FROM @Items WHERE ISNULL(dblQuantity, 0) = 0;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @dtDate IS NULL THEN 'Enter the date.'
        WHEN NOT EXISTS (SELECT 1 FROM @Items) THEN 'Nothing to adjust: every count matches.'
        WHEN EXISTS (SELECT 1 FROM @Items X WHERE NOT EXISTS (SELECT 1 FROM tblInventoryItems I WHERE I.iItemID = X.iItemID AND I.iUserID = @iUserId AND I.bTracked = 1))
            THEN 'Only tracked items in this portfolio can be adjusted.'
        WHEN EXISTS (SELECT 1 FROM @Items GROUP BY iItemID HAVING COUNT(*) > 1) THEN 'Each item can only be adjusted once.'
        WHEN EXISTS (SELECT 1 FROM @Items WHERE dblUnitCost < 0) THEN 'A cost can''t be negative.'
        WHEN NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0) AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the account for the difference.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END

    BEGIN TRANSACTION;
    INSERT tblStockAdjustments (iUserID, dtmDate, strNote, iAccountID) VALUES (@iUserId, @dtDate, NULLIF(LTRIM(RTRIM(@strNote)), ''), @iAccountID);
    DECLARE @iAdjustmentID INT = SCOPE_IDENTITY();
    INSERT tblInventoryMovements (iUserID, iItemID, dtmDate, strSource, iSourceID, iLineID, dblQuantity, dblCost, bFixedCost)
    SELECT @iUserId, iItemID, @dtDate, 'Adjust', @iAdjustmentID, NULL, dblQuantity,
           CASE WHEN dblQuantity > 0 AND dblUnitCost IS NOT NULL THEN ROUND(dblQuantity * dblUnitCost, 2) ELSE 0 END,
           CASE WHEN dblQuantity > 0 AND dblUnitCost IS NOT NULL THEN 1 ELSE 0 END
    FROM @Items;

    DECLARE @iItemID INT;
    DECLARE curItems CURSOR LOCAL FAST_FORWARD FOR SELECT iItemID FROM @Items;
    OPEN curItems;
    FETCH NEXT FROM curItems INTO @iItemID;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.uspRecalcInventoryItem @iUserId, @iItemID;
        FETCH NEXT FROM curItems INTO @iItemID;
    END
    CLOSE curItems;
    DEALLOCATE curItems;

    EXEC dbo.uspPostStockAdjustmentJournal @iUserId, @iAdjustmentID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iAdjustmentID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteStockAdjustment
    @iUserId       INT,
    @iAdjustmentID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @iJournalID INT, @bFound BIT = 0;
    SELECT @iJournalID = iJournalID, @bFound = 1 FROM tblStockAdjustments WHERE iAdjustmentID = @iAdjustmentID AND iUserID = @iUserId;
    IF @bFound = 0
    BEGIN
        SELECT 'Error: That adjustment wasn''t found.' AS Result;
        RETURN;
    END

    DECLARE @Items TABLE (iItemID INT PRIMARY KEY);
    INSERT @Items SELECT DISTINCT iItemID FROM tblInventoryMovements WHERE strSource = 'Adjust' AND iSourceID = @iAdjustmentID AND iUserID = @iUserId;

    BEGIN TRANSACTION;
    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    DELETE FROM tblInventoryMovements WHERE strSource = 'Adjust' AND iSourceID = @iAdjustmentID AND iUserID = @iUserId;
    DELETE FROM tblStockAdjustments WHERE iAdjustmentID = @iAdjustmentID;

    DECLARE @iItemID INT;
    DECLARE curItems CURSOR LOCAL FAST_FORWARD FOR SELECT iItemID FROM @Items;
    OPEN curItems;
    FETCH NEXT FROM curItems INTO @iItemID;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.uspRecalcInventoryItem @iUserId, @iItemID;
        FETCH NEXT FROM curItems INTO @iItemID;
    END
    CLOSE curItems;
    DEALLOCATE curItems;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Inventory migration done.';
GO
