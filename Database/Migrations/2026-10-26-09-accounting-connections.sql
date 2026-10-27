-- =============================================
-- Migration: sending journals straight to Xero and MYOB
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build that connects a portfolio to
-- Xero or MYOB and sends the export journals through their APIs. Run it after 2026-10-21-18-ledger-export.sql.
-- Safe to run more than once.
--
-- What it changes:
--   * tblAccountingConnections: one connection per portfolio and system, holding the organisation (Xero tenant or
--     MYOB business) and the sign-in tokens, encrypted by the web site before they are saved.
--   * tblAccountingSends: each send, with its dates, how many journals went and what came back, so a second send of
--     the same dates can be warned about.
--   * New: uspSaveAccountingConnection, uspDeleteAccountingConnection, uspAddAccountingSend.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblLedgerExportMap', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-18-ledger-export.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblAccountingConnections', 'U') IS NULL
    CREATE TABLE dbo.tblAccountingConnections (
        iConnectionID       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblAccountingConnections PRIMARY KEY,
        iUserID             INT NOT NULL,
        strSystem           VARCHAR(10) NOT NULL,
        strOrganisationID   VARCHAR(100) NOT NULL,
        strOrganisationName NVARCHAR(200) NULL,
        strTokens           NVARCHAR(MAX) NOT NULL,      -- encrypted by the web site; never readable in the database
        dtmConnected        DATETIME NOT NULL CONSTRAINT DF_tblAccountingConnections_Connected DEFAULT (GETDATE()),
        iConnectedByUserID  INT NULL,
        CONSTRAINT UQ_tblAccountingConnections UNIQUE (iUserID, strSystem)
    );
GO

IF OBJECT_ID('dbo.tblAccountingSends', 'U') IS NULL
    CREATE TABLE dbo.tblAccountingSends (
        iSendID         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblAccountingSends PRIMARY KEY,
        iUserID         INT NOT NULL,
        strSystem       VARCHAR(10) NOT NULL,
        strShape        VARCHAR(20) NOT NULL,
        dtmFrom         DATE NULL,
        dtmTo           DATE NOT NULL,
        iJournals       INT NOT NULL,
        iSent           INT NOT NULL,
        strResult       NVARCHAR(2000) NULL,
        dtmSent         DATETIME NOT NULL CONSTRAINT DF_tblAccountingSends_Sent DEFAULT (GETDATE()),
        iSentByUserID   INT NULL
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblAccountingSends_User' AND object_id = OBJECT_ID('dbo.tblAccountingSends'))
    CREATE INDEX IX_tblAccountingSends_User ON dbo.tblAccountingSends (iUserID, strSystem, dtmSent);
GO

-- Saves (or replaces) the portfolio's connection to one system. Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspSaveAccountingConnection
    @iUserID             INT,
    @strSystem           VARCHAR(10),
    @strOrganisationID   VARCHAR(100),
    @strOrganisationName NVARCHAR(200) = NULL,
    @strTokens           NVARCHAR(MAX),
    @iConnectedByUserID  INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strSystem NOT IN ('Xero', 'MYOB') OR ISNULL(@strOrganisationID, '') = '' OR ISNULL(@strTokens, '') = ''
    BEGIN
        SELECT 'Error: The connection details were incomplete.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblAccountingConnections WHERE iUserID = @iUserID AND strSystem = @strSystem)
        UPDATE tblAccountingConnections
        SET strOrganisationID = @strOrganisationID,
            strOrganisationName = ISNULL(@strOrganisationName, CASE WHEN strOrganisationID = @strOrganisationID THEN strOrganisationName END),
            strTokens = @strTokens,
            dtmConnected = CASE WHEN @iConnectedByUserID IS NULL THEN dtmConnected ELSE GETDATE() END,
            iConnectedByUserID = ISNULL(@iConnectedByUserID, iConnectedByUserID)
        WHERE iUserID = @iUserID AND strSystem = @strSystem;
    ELSE
        INSERT tblAccountingConnections (iUserID, strSystem, strOrganisationID, strOrganisationName, strTokens, iConnectedByUserID)
        VALUES (@iUserID, @strSystem, @strOrganisationID, @strOrganisationName, @strTokens, @iConnectedByUserID);
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteAccountingConnection
    @iUserID   INT,
    @strSystem VARCHAR(10)
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM tblAccountingConnections WHERE iUserID = @iUserID AND strSystem = @strSystem;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspAddAccountingSend
    @iUserID       INT,
    @strSystem     VARCHAR(10),
    @strShape      VARCHAR(20),
    @dtFrom        DATE = NULL,
    @dtTo          DATE,
    @iJournals     INT,
    @iSent         INT,
    @strResult     NVARCHAR(2000) = NULL,
    @iSentByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT tblAccountingSends (iUserID, strSystem, strShape, dtmFrom, dtmTo, iJournals, iSent, strResult, iSentByUserID)
    VALUES (@iUserID, @strSystem, @strShape, @dtFrom, @dtTo, @iJournals, @iSent, LEFT(@strResult, 2000), @iSentByUserID);
    SELECT 'Success' AS Result;
END
GO

PRINT 'Xero and MYOB connections migration done.';
GO
