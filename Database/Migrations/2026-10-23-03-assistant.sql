-- The assistant in the site (roadmap 42, 23 Oct 2026): a chat panel that uses the same tools as the API and MCP server,
-- with every change shown to the person for approval first. Each login can keep its own Anthropic API key here
-- (encrypted by the web site before it is saved) and one conversation per portfolio. Changes the assistant makes are
-- written to the agent change log with key id 0, shown as "Site assistant". Safe to run twice.
-- Run after 2026-10-20-api-keys.sql. No USE line: pick your database in SSMS first.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblApiAuditLog', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-20-api-keys.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- strApiKeyProtected is encrypted with the site's data protection keys; the database never sees the key itself
IF OBJECT_ID('dbo.tblAssistantSettings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAssistantSettings (
        iLoginID            INT NOT NULL CONSTRAINT PK_tblAssistantSettings PRIMARY KEY,
        strApiKeyProtected  NVARCHAR(2000) NULL,
        strKeyHint          NVARCHAR(20) NULL,            -- the last characters, to tell which key is saved
        strModel            VARCHAR(100) NULL,
        dtmUpdated          DATETIME NOT NULL CONSTRAINT DF_tblAssistantSettings_Updated DEFAULT (GETDATE())
    );
END
GO

-- The conversation, as the messages sent to the model (JSON)
IF OBJECT_ID('dbo.tblAssistantChats', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAssistantChats (
        iLoginID            INT NOT NULL,
        iPortfolioID        INT NOT NULL,
        strTranscriptJson   NVARCHAR(MAX) NOT NULL,
        dtmUpdated          DATETIME NOT NULL CONSTRAINT DF_tblAssistantChats_Updated DEFAULT (GETDATE()),
        CONSTRAINT PK_tblAssistantChats PRIMARY KEY (iLoginID, iPortfolioID)
    );
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetAssistantSettings]
    @iLoginID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT strApiKeyProtected AS ApiKeyProtected, strKeyHint AS KeyHint, strModel AS Model
    FROM tblAssistantSettings WHERE iLoginID = @iLoginID;
END
GO

-- @bClearKey 1 removes the saved key; otherwise a NULL key leaves the saved one as it is
CREATE OR ALTER PROCEDURE [dbo].[uspSaveAssistantSettings]
    @iLoginID INT,
    @strApiKeyProtected NVARCHAR(2000) = NULL,
    @strKeyHint NVARCHAR(20) = NULL,
    @strModel VARCHAR(100) = NULL,
    @bClearKey BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblUsers WHERE iUserId = @iLoginID)
    BEGIN
        SELECT 'Error: That login was not found.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblAssistantSettings WHERE iLoginID = @iLoginID)
        UPDATE tblAssistantSettings
        SET strApiKeyProtected = CASE WHEN @bClearKey = 1 THEN NULL ELSE ISNULL(@strApiKeyProtected, strApiKeyProtected) END,
            strKeyHint = CASE WHEN @bClearKey = 1 THEN NULL ELSE ISNULL(@strKeyHint, strKeyHint) END,
            strModel = @strModel,
            dtmUpdated = GETDATE()
        WHERE iLoginID = @iLoginID;
    ELSE
        INSERT INTO tblAssistantSettings (iLoginID, strApiKeyProtected, strKeyHint, strModel)
        VALUES (@iLoginID, CASE WHEN @bClearKey = 1 THEN NULL ELSE @strApiKeyProtected END, CASE WHEN @bClearKey = 1 THEN NULL ELSE @strKeyHint END, @strModel);
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetAssistantChat]
    @iLoginID INT,
    @iPortfolioID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT strTranscriptJson FROM tblAssistantChats WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID;
END
GO

-- An empty transcript clears the conversation
CREATE OR ALTER PROCEDURE [dbo].[uspSaveAssistantChat]
    @iLoginID INT,
    @iPortfolioID INT,
    @strTranscriptJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    IF @strTranscriptJson IS NULL OR LTRIM(@strTranscriptJson) IN ('', '[]')
        DELETE FROM tblAssistantChats WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID;
    ELSE IF EXISTS (SELECT 1 FROM tblAssistantChats WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID)
        UPDATE tblAssistantChats SET strTranscriptJson = @strTranscriptJson, dtmUpdated = GETDATE()
        WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID;
    ELSE
        INSERT INTO tblAssistantChats (iLoginID, iPortfolioID, strTranscriptJson) VALUES (@iLoginID, @iPortfolioID, @strTranscriptJson);
    SELECT 'Success' AS Result;
END
GO

-- The change log now also shows changes made by the site assistant (key id 0, no tblApiKeys row)
CREATE OR ALTER PROCEDURE [dbo].[uspGetApiAuditLog]
(
    @iUserId INT,
    @iTop INT = 200
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        20-Oct-2026, 23-Oct-2026 (site assistant)
-- Description: Recent agent changes a login may see: those made with its own keys or its site assistant,
--              and those made by anyone in a portfolio it owns. Newest first.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (ISNULL(@iTop, 200))
           A.iApiAuditID, A.dtmCreatedDate, A.iPortfolioID,
           P.strEntityName AS PortfolioName,
           ISNULL(K.strName, 'Site assistant') AS KeyName, U.strLoginEmail AS KeyOwner,
           A.strAction, A.strDetail, A.bSucceeded, A.strResult
    FROM tblApiAuditLog A
    LEFT JOIN tblApiKeys K ON K.iApiKeyID = A.iApiKeyID
    JOIN tblUsers U ON U.iUserId = A.iUserID
    LEFT JOIN tblUsers P ON P.iUserId = A.iPortfolioID
    WHERE A.iUserID = @iUserId OR dbo.fnCanSharePortfolio(@iUserId, A.iPortfolioID) = 1
    ORDER BY A.dtmCreatedDate DESC, A.iApiAuditID DESC;
END
GO

SET NOEXEC OFF;
GO
