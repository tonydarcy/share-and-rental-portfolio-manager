-- =============================================
-- Migration: attachments and audit trail
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Attachments and
-- Accounting > Audit trail. Run it after 2026-10-18-invoicing.sql. Safe to run more than once. It only adds things.
--
-- What it adds:
--   * tblAttachments: files (contract notes, statements, resolutions, workpapers) kept in the database against a
--     trade, journal, the client or a year. uspAddAttachment and uspDeleteAttachment.
--   * tblAuditLog and triggers on tblJournal, tblShareTrades, tblPayouts, tblCorporateActions, tblBankStatement and
--     tblInvoices that record every row added, changed or deleted: when, by which login (the site passes it in the
--     session context as pm_user) and a one-line summary. The triggers never refuse a change.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblInvoices', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-18-invoicing.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblAttachments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAttachments (
        iAttachmentID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblAttachments PRIMARY KEY,
        iUserID           INT NOT NULL,                -- the portfolio
        strKind           VARCHAR(20) NOT NULL,        -- Trade, Journal, Client, Year, Workpaper, Query
        iTargetID         INT NULL,                    -- the trade, journal, workpaper or query
        iYear             INT NULL,                    -- the financial year (its end year) it belongs to
        strFileName       NVARCHAR(260) NOT NULL,
        strContentType    VARCHAR(100) NOT NULL,
        iSize             INT NOT NULL,
        binData           VARBINARY(MAX) NOT NULL,
        strNote           NVARCHAR(500) NULL,
        iUploadedByUserID INT NULL,
        dtmUploaded       DATETIME NOT NULL CONSTRAINT DF_tblAttachments_Uploaded DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblAttachments_Target ON dbo.tblAttachments (iUserID, strKind, iTargetID);
    CREATE INDEX IX_tblAttachments_Year ON dbo.tblAttachments (iUserID, iYear);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspAddAttachment
    @iUserID           INT,
    @strKind           VARCHAR(20),
    @iTargetID         INT = NULL,
    @iYear             INT = NULL,
    @strFileName       NVARCHAR(260),
    @strContentType    VARCHAR(100),
    @binData           VARBINARY(MAX),
    @strNote           NVARCHAR(500) = NULL,
    @iUploadedByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strKind NOT IN ('Trade', 'Journal', 'Client', 'Year', 'Workpaper', 'Query') OR LTRIM(RTRIM(ISNULL(@strFileName, ''))) = ''
        OR @binData IS NULL OR DATALENGTH(@binData) = 0 OR DATALENGTH(@binData) > 10485760
    BEGIN
        SELECT 'Error: Choose a file of up to 10 MB.' AS Result, NULL AS Id;
        RETURN;
    END
    -- The record it's attached to must be in the same portfolio
    IF (@strKind = 'Journal' AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal WHERE iJournalID = @iTargetID AND iUserID = @iUserID))
        OR (@strKind = 'Trade' AND NOT EXISTS (SELECT 1 FROM dbo.tblShareTrades WHERE iShareTradeId = @iTargetID AND iUserId = @iUserID))
    BEGIN
        SELECT 'Error: That record isn''t in this portfolio.' AS Result, NULL AS Id;
        RETURN;
    END
    INSERT dbo.tblAttachments (iUserID, strKind, iTargetID, iYear, strFileName, strContentType, iSize, binData, strNote, iUploadedByUserID)
    VALUES (@iUserID, @strKind, @iTargetID, @iYear, LTRIM(RTRIM(@strFileName)), @strContentType, DATALENGTH(@binData), @binData,
        NULLIF(LTRIM(RTRIM(@strNote)), ''), @iUploadedByUserID);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteAttachment
    @iUserID       INT,
    @iAttachmentID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.tblAttachments WHERE iAttachmentID = @iAttachmentID AND iUserID = @iUserID;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That file isn''t in this portfolio.' END AS Result;
END
GO

IF OBJECT_ID('dbo.tblAuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAuditLog (
        iAuditID         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblAuditLog PRIMARY KEY,
        iUserID          INT NULL,                     -- the portfolio
        dtmChanged       DATETIME NOT NULL CONSTRAINT DF_tblAuditLog_Changed DEFAULT (GETDATE()),
        iChangedByUserID INT NULL,                     -- the login, from SESSION_CONTEXT pm_user; NULL for scripts and the price updater
        strApp           NVARCHAR(128) NULL,
        strTable         VARCHAR(30) NOT NULL,
        iRecordID        INT NOT NULL,
        strAction        VARCHAR(10) NOT NULL,         -- Added, Changed, Deleted
        dtmRecordDate    DATE NULL,                    -- the record's own date, to see changes to a year after it was locked
        strSummary       NVARCHAR(1000) NULL
    );
    CREATE INDEX IX_tblAuditLog_User ON dbo.tblAuditLog (iUserID, dtmChanged);
END
GO

CREATE OR ALTER TRIGGER dbo.trgJournalAudit ON dbo.tblJournal
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.tblAuditLog (iUserID, iChangedByUserID, strApp, strTable, iRecordID, strAction, dtmRecordDate, strSummary)
    SELECT ISNULL(I.iUserID, D.iUserID), TRY_CAST(SESSION_CONTEXT(N'pm_user') AS INT), LEFT(APP_NAME(), 128), 'Journal',
        ISNULL(I.iJournalID, D.iJournalID),
        CASE WHEN D.iJournalID IS NULL THEN 'Added' WHEN I.iJournalID IS NULL THEN 'Deleted' ELSE 'Changed' END,
        ISNULL(I.dtmTransactionDate, D.dtmTransactionDate),
        LEFT(CONCAT(ISNULL(I.strSourceModule, D.strSourceModule), ': ', ISNULL(I.strDescription, D.strDescription),
            CASE WHEN I.dtmTransactionDate <> D.dtmTransactionDate THEN CONCAT(' (date was ', CONVERT(VARCHAR(10), D.dtmTransactionDate, 120), ')') END,
            CASE WHEN I.strDescription <> D.strDescription THEN CONCAT(' (was: ', D.strDescription, ')') END), 1000)
    FROM inserted I FULL JOIN deleted D ON D.iJournalID = I.iJournalID;
END
GO

CREATE OR ALTER TRIGGER dbo.trgShareTradesAudit ON dbo.tblShareTrades
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.tblAuditLog (iUserID, iChangedByUserID, strApp, strTable, iRecordID, strAction, dtmRecordDate, strSummary)
    SELECT ISNULL(I.iUserId, D.iUserId), TRY_CAST(SESSION_CONTEXT(N'pm_user') AS INT), LEFT(APP_NAME(), 128), 'Trade',
        ISNULL(I.iShareTradeId, D.iShareTradeId),
        CASE WHEN D.iShareTradeId IS NULL THEN 'Added' WHEN I.iShareTradeId IS NULL THEN 'Deleted' ELSE 'Changed' END,
        ISNULL(I.dtmDateOfTransaction, D.dtmDateOfTransaction),
        LEFT(CONCAT(ISNULL(I.strType, D.strType), ' ', ISNULL(I.iQuantity, D.iQuantity), ' ', ISNULL(I.strStockExchangeCode, D.strStockExchangeCode), ':',
            ISNULL(I.strStockCode, D.strStockCode), ' for ', ISNULL(I.dblOrderTotal, D.dblOrderTotal),
            CASE WHEN ISNULL(I.dblOrderTotal, 0) <> ISNULL(D.dblOrderTotal, 0) AND I.iShareTradeId IS NOT NULL AND D.iShareTradeId IS NOT NULL
                 THEN CONCAT(' (was ', D.dblOrderTotal, ')') END,
            CASE WHEN I.dtmDateOfTransaction <> D.dtmDateOfTransaction THEN CONCAT(' (date was ', CONVERT(VARCHAR(10), D.dtmDateOfTransaction, 120), ')') END), 1000)
    FROM inserted I FULL JOIN deleted D ON D.iShareTradeId = I.iShareTradeId;
END
GO

CREATE OR ALTER TRIGGER dbo.trgPayoutsAudit ON dbo.tblPayouts
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.tblAuditLog (iUserID, iChangedByUserID, strApp, strTable, iRecordID, strAction, dtmRecordDate, strSummary)
    SELECT ISNULL(I.iUserID, D.iUserID), TRY_CAST(SESSION_CONTEXT(N'pm_user') AS INT), LEFT(APP_NAME(), 128), 'Payout',
        ISNULL(I.iPayoutID, D.iPayoutID),
        CASE WHEN D.iPayoutID IS NULL THEN 'Added' WHEN I.iPayoutID IS NULL THEN 'Deleted' ELSE 'Changed' END,
        CAST(ISNULL(I.dtmTransactionDate, D.dtmTransactionDate) AS DATE),
        LEFT(CONCAT(ISNULL(I.strStockExchangeCode, D.strStockExchangeCode), ':', ISNULL(I.strStockCode, D.strStockCode), ' payout of ',
            CAST(ROUND(ISNULL(I.dblTotal, D.dblTotal), 2) AS DECIMAL(19,2)),
            CASE WHEN ISNULL(I.dblTotal, 0) <> ISNULL(D.dblTotal, 0) AND I.iPayoutID IS NOT NULL AND D.iPayoutID IS NOT NULL
                 THEN CONCAT(' (was ', CAST(ROUND(D.dblTotal, 2) AS DECIMAL(19,2)), ')') END), 1000)
    FROM inserted I FULL JOIN deleted D ON D.iPayoutID = I.iPayoutID;
END
GO

CREATE OR ALTER TRIGGER dbo.trgCorporateActionsAudit ON dbo.tblCorporateActions
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.tblAuditLog (iUserID, iChangedByUserID, strApp, strTable, iRecordID, strAction, dtmRecordDate, strSummary)
    SELECT ISNULL(I.iUserID, D.iUserID), TRY_CAST(SESSION_CONTEXT(N'pm_user') AS INT), LEFT(APP_NAME(), 128), 'CorporateAction',
        ISNULL(I.iActionID, D.iActionID),
        CASE WHEN D.iActionID IS NULL THEN 'Added' WHEN I.iActionID IS NULL THEN 'Deleted' ELSE 'Changed' END,
        ISNULL(I.dtmActionDate, D.dtmActionDate),
        LEFT(CONCAT(ISNULL(I.strActionType, D.strActionType), ' ', ISNULL(I.strStockExchangeCode, D.strStockExchangeCode), ':',
            ISNULL(I.strStockCode, D.strStockCode), ' ratio ', ISNULL(I.dblRatio, D.dblRatio)), 1000)
    FROM inserted I FULL JOIN deleted D ON D.iActionID = I.iActionID;
END
GO

CREATE OR ALTER TRIGGER dbo.trgBankStatementAudit ON dbo.tblBankStatement
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.tblAuditLog (iUserID, iChangedByUserID, strApp, strTable, iRecordID, strAction, dtmRecordDate, strSummary)
    SELECT ISNULL(I.iUserId, D.iUserId), TRY_CAST(SESSION_CONTEXT(N'pm_user') AS INT), LEFT(APP_NAME(), 128), 'Cash',
        ISNULL(I.iBankStatementId, D.iBankStatementId),
        CASE WHEN D.iBankStatementId IS NULL THEN 'Added' WHEN I.iBankStatementId IS NULL THEN 'Deleted' ELSE 'Changed' END,
        ISNULL(I.dtmDateOfTransaction, D.dtmDateOfTransaction),
        LEFT(CONCAT(ISNULL(I.strBankAccountName, D.strBankAccountName), ': ',
            CASE WHEN ISNULL(ISNULL(I.dblDepositAmount, D.dblDepositAmount), 0) <> 0 THEN CONCAT('deposit ', ISNULL(I.dblDepositAmount, D.dblDepositAmount))
                 ELSE CONCAT('withdrawal ', ISNULL(I.dblWithdrawalAmount, D.dblWithdrawalAmount)) END,
            ' ', ISNULL(I.strComment, D.strComment)), 1000)
    FROM inserted I FULL JOIN deleted D ON D.iBankStatementId = I.iBankStatementId;
END
GO

CREATE OR ALTER TRIGGER dbo.trgInvoicesAudit ON dbo.tblInvoices
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.tblAuditLog (iUserID, iChangedByUserID, strApp, strTable, iRecordID, strAction, dtmRecordDate, strSummary)
    SELECT ISNULL(I.iUserID, D.iUserID), TRY_CAST(SESSION_CONTEXT(N'pm_user') AS INT), LEFT(APP_NAME(), 128), 'Invoice',
        ISNULL(I.iInvoiceID, D.iInvoiceID),
        CASE WHEN D.iInvoiceID IS NULL THEN 'Added' WHEN I.iInvoiceID IS NULL THEN 'Deleted' ELSE 'Changed' END,
        ISNULL(I.dtmIssueDate, D.dtmIssueDate),
        LEFT(CONCAT(ISNULL(I.strInvoiceNumber, D.strInvoiceNumber), ' ', ISNULL(I.strStatus, D.strStatus), ' ',
            CAST(ISNULL(I.dblTotal, D.dblTotal) AS DECIMAL(19,2)),
            CASE WHEN I.strStatus <> D.strStatus THEN CONCAT(' (was ', D.strStatus, ')') END), 1000)
    FROM inserted I FULL JOIN deleted D ON D.iInvoiceID = I.iInvoiceID;
END
GO
