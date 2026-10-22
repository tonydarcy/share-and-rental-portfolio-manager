-- =============================================
-- Migration: API keys and the agent change log
-- Date:      20-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with the
-- API and MCP server (Settings > API keys). Run it AFTER 2026-10-18-shared-access.sql.
-- Safe to run more than once. It only adds things; nothing existing changes.
--
-- What it adds:
--   * tblApiKeys: per-login API keys. Only a SHA-256 hash of each key is stored, never the key.
--   * tblApiAuditLog: every change an API key or AI agent makes (or tries to make).
--   * uspAddApiKey, uspGetApiKeys, uspRevokeApiKey, uspFindApiKey,
--     uspAddApiAuditLog, uspGetApiAuditLog.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnCanSharePortfolio', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-18-shared-access.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblApiKeys', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblApiKeys (
        iApiKeyID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblApiKeys PRIMARY KEY,
        iUserID            INT NOT NULL,                  -- the login the key acts as
        strName            NVARCHAR(100) NOT NULL,
        strKeyPrefix       VARCHAR(20) NOT NULL,          -- the first characters, to tell keys apart
        strKeyHash         CHAR(64) NOT NULL,             -- SHA-256 of the whole key, hex
        bReadOnly          BIT NOT NULL CONSTRAINT DF_tblApiKeys_ReadOnly DEFAULT (0),
        dtmCreatedDate     DATETIME NOT NULL CONSTRAINT DF_tblApiKeys_Created DEFAULT (GETDATE()),
        dtmLastUsedDate    DATETIME NULL,
        dtmRevokedDate     DATETIME NULL
    );
    CREATE UNIQUE INDEX UX_tblApiKeys_Hash ON dbo.tblApiKeys (strKeyHash);
    CREATE INDEX IX_tblApiKeys_User ON dbo.tblApiKeys (iUserID);
END
GO

IF OBJECT_ID('dbo.tblApiAuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblApiAuditLog (
        iApiAuditID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblApiAuditLog PRIMARY KEY,
        iApiKeyID          INT NOT NULL,
        iUserID            INT NOT NULL,                  -- the login that owns the key
        iPortfolioID       INT NOT NULL,                  -- the tblUsers row changed
        strAction          VARCHAR(100) NOT NULL,         -- e.g. add_trade
        strDetail          NVARCHAR(4000) NULL,           -- what was sent, as JSON
        bSucceeded         BIT NOT NULL,
        strResult          NVARCHAR(1000) NULL,           -- what happened, or why it was refused
        dtmCreatedDate     DATETIME NOT NULL CONSTRAINT DF_tblApiAuditLog_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblApiAuditLog_User ON dbo.tblApiAuditLog (iUserID, dtmCreatedDate);
    CREATE INDEX IX_tblApiAuditLog_Portfolio ON dbo.tblApiAuditLog (iPortfolioID, dtmCreatedDate);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddApiKey]
(
    @iUserId INT,
    @strName NVARCHAR(100),
    @strKeyPrefix VARCHAR(20),
    @strKeyHash CHAR(64),
    @bReadOnly BIT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        20-Oct-2026
-- Description: Saves a new API key for a login (its hash only).
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM tblUsers WHERE iUserId = @iUserId)
    BEGIN
        SELECT 'Error: Login not found.' AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END
    IF LTRIM(RTRIM(ISNULL(@strName, ''))) = ''
    BEGIN
        SELECT 'Error: Give the key a name.' AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END

    INSERT INTO tblApiKeys (iUserID, strName, strKeyPrefix, strKeyHash, bReadOnly)
    VALUES (@iUserId, LTRIM(RTRIM(@strName)), @strKeyPrefix, @strKeyHash, ISNULL(@bReadOnly, 0));

    SELECT 'Key Added' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetApiKeys]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        20-Oct-2026
-- Description: A login's API keys, newest first (never the key itself).
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT iApiKeyID, strName, strKeyPrefix, bReadOnly, dtmCreatedDate, dtmLastUsedDate, dtmRevokedDate
    FROM tblApiKeys
    WHERE iUserID = @iUserId
    ORDER BY CASE WHEN dtmRevokedDate IS NULL THEN 0 ELSE 1 END, dtmCreatedDate DESC;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspRevokeApiKey]
(
    @iUserId INT,
    @iApiKeyID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        20-Oct-2026
-- Description: Stops one of the login's own keys working, at once.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    UPDATE tblApiKeys SET dtmRevokedDate = GETDATE()
    WHERE iApiKeyID = @iApiKeyID AND iUserID = @iUserId AND dtmRevokedDate IS NULL;

    IF @@ROWCOUNT = 0
        SELECT 'Error: That key wasn''t found, or it was already revoked.' AS Result;
    ELSE
        SELECT 'Key Revoked' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspFindApiKey]
(
    @strKeyHash CHAR(64)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        20-Oct-2026
-- Description: The working (not revoked) key with this hash, if any, and notes
--              when it was last used (at most every five minutes, to save writes).
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @iApiKeyID INT;
    SELECT @iApiKeyID = iApiKeyID FROM tblApiKeys WHERE strKeyHash = @strKeyHash AND dtmRevokedDate IS NULL;
    IF @iApiKeyID IS NULL
        RETURN;

    UPDATE tblApiKeys SET dtmLastUsedDate = GETDATE()
    WHERE iApiKeyID = @iApiKeyID
      AND (dtmLastUsedDate IS NULL OR dtmLastUsedDate < DATEADD(MINUTE, -5, GETDATE()));

    SELECT K.iApiKeyID, K.iUserID, K.strName, K.bReadOnly, U.strLoginEmail
    FROM tblApiKeys K
    JOIN tblUsers U ON U.iUserId = K.iUserID
    WHERE K.iApiKeyID = @iApiKeyID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddApiAuditLog]
(
    @iApiKeyID INT,
    @iUserId INT,
    @iPortfolioId INT,
    @strAction VARCHAR(100),
    @strDetail NVARCHAR(4000),
    @bSucceeded BIT,
    @strResult NVARCHAR(1000)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        20-Oct-2026
-- Description: Records one change an API key or AI agent made or tried to make.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    INSERT INTO tblApiAuditLog (iApiKeyID, iUserID, iPortfolioID, strAction, strDetail, bSucceeded, strResult)
    VALUES (@iApiKeyID, @iUserId, @iPortfolioId, @strAction, @strDetail, @bSucceeded, @strResult);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetApiAuditLog]
(
    @iUserId INT,
    @iTop INT = 200
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        20-Oct-2026
-- Description: Recent agent changes a login may see: those made with its own keys,
--              and those made by anyone in a portfolio it owns. Newest first.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (ISNULL(@iTop, 200))
           A.iApiAuditID, A.dtmCreatedDate, A.iPortfolioID,
           P.strEntityName AS PortfolioName,
           K.strName AS KeyName, U.strLoginEmail AS KeyOwner,
           A.strAction, A.strDetail, A.bSucceeded, A.strResult
    FROM tblApiAuditLog A
    JOIN tblApiKeys K ON K.iApiKeyID = A.iApiKeyID
    JOIN tblUsers U ON U.iUserId = A.iUserID
    LEFT JOIN tblUsers P ON P.iUserId = A.iPortfolioID
    WHERE A.iUserID = @iUserId OR dbo.fnCanSharePortfolio(@iUserId, A.iPortfolioID) = 1
    ORDER BY A.dtmCreatedDate DESC, A.iApiAuditID DESC;
END
GO

SET NOEXEC OFF;
GO
