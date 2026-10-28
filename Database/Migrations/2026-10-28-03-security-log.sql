-- =============================================
-- Security log: sign-ins (good and bad), two-factor codes, password and two-factor changes, API keys,
-- with the internet address and browser. It pauses an email address after too many wrong attempts,
-- spots sign-ins from a new device, and fills Account > Recent sign-ins. Safe to run twice.
-- =============================================
IF OBJECT_ID('dbo.tblSecurityEvents', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSecurityEvents
    (
        iSecurityEventId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblSecurityEvents PRIMARY KEY,
        dtmWhenUtc       DATETIME2(0)  NOT NULL CONSTRAINT DF_tblSecurityEvents_When DEFAULT SYSUTCDATETIME(),
        iUserId          INT           NULL,
        -- Lower case; kept even when no login has this email, so guesses at unknown emails are paused too
        strEmail         VARCHAR(500)  NULL,
        strEvent         VARCHAR(40)   NOT NULL,
        bSuccess         BIT           NOT NULL,
        strIpAddress     VARCHAR(45)   NULL,
        strUserAgent     NVARCHAR(300) NULL,
        -- SHA-256 of the browser's device cookie (first 32 hex characters), never the cookie itself
        strDevice        VARCHAR(32)   NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblSecurityEvents_Email' AND object_id = OBJECT_ID('dbo.tblSecurityEvents'))
    CREATE INDEX IX_tblSecurityEvents_Email ON dbo.tblSecurityEvents (strEmail, dtmWhenUtc) INCLUDE (strEvent, bSuccess);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblSecurityEvents_User' AND object_id = OBJECT_ID('dbo.tblSecurityEvents'))
    CREATE INDEX IX_tblSecurityEvents_User ON dbo.tblSecurityEvents (iUserId, dtmWhenUtc) INCLUDE (strEvent, bSuccess, strDevice);
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddSecurityEvent]
(
    @iUserId      INT = NULL,
    @strEmail     VARCHAR(500) = NULL,
    @strEvent     VARCHAR(40),
    @bSuccess     BIT,
    @strIpAddress VARCHAR(45) = NULL,
    @strUserAgent NVARCHAR(300) = NULL,
    @strDevice    VARCHAR(32) = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description: Adds one entry to the security log.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.tblSecurityEvents (iUserId, strEmail, strEvent, bSuccess, strIpAddress, strUserAgent, strDevice)
    VALUES (@iUserId, LOWER(LTRIM(RTRIM(@strEmail))), @strEvent, @bSuccess, @strIpAddress, LEFT(@strUserAgent, 300), @strDevice);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspCountRecentSignInFailures]
(
    @strEmail VARCHAR(500),
    @iMinutes INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description: Wrong passwords and two-factor codes for an email in the last @iMinutes,
--              counting only those since its last successful sign-in or password reset.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    DECLARE @email VARCHAR(500) = LOWER(LTRIM(RTRIM(@strEmail)));
    DECLARE @since DATETIME2(0) = DATEADD(MINUTE, -@iMinutes, SYSUTCDATETIME());
    DECLARE @lastSuccess DATETIME2(0) = (
        SELECT MAX(dtmWhenUtc) FROM dbo.tblSecurityEvents
        WHERE strEmail = @email AND strEvent IN ('Signed in', 'Password reset') AND bSuccess = 1 AND dtmWhenUtc >= @since);

    SELECT COUNT(*) AS iFailures
    FROM dbo.tblSecurityEvents
    WHERE strEmail = @email
      AND strEvent IN ('Password', 'Two-factor code')
      AND bSuccess = 0
      AND dtmWhenUtc >= @since
      AND (@lastSuccess IS NULL OR dtmWhenUtc > @lastSuccess);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetSecurityEvents]
(
    @iUserId INT,
    @iCount  INT = 50
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description: The newest security log entries for one login, including wrong passwords typed against its email.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    DECLARE @email VARCHAR(500) = (SELECT LOWER(LTRIM(RTRIM(strLoginEmail))) FROM dbo.tblUsers WHERE iUserId = @iUserId);

    SELECT TOP (@iCount) dtmWhenUtc, strEvent, bSuccess, strIpAddress, strUserAgent
    FROM dbo.tblSecurityEvents
    WHERE iUserId = @iUserId OR (@email IS NOT NULL AND strEmail = @email)
    ORDER BY dtmWhenUtc DESC, iSecurityEventId DESC;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetSignInHistory]
(
    @iUserId   INT,
    @strDevice VARCHAR(32)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description: Whether a login has signed in before, and before from this device, so a new device can be emailed about.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT
        CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.tblSecurityEvents
            WHERE iUserId = @iUserId AND strEvent = 'Signed in' AND bSuccess = 1) THEN 1 ELSE 0 END AS BIT) AS bSignedInBefore,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.tblSecurityEvents
            WHERE iUserId = @iUserId AND strEvent = 'Signed in' AND bSuccess = 1 AND strDevice = @strDevice) THEN 1 ELSE 0 END AS BIT) AS bKnownDevice;
END
GO
