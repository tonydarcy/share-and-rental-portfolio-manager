-- =============================================
-- Migration: account security (password reset, email check, two-factor sign-in)
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- password reset by email and two-factor sign-in. Run it AFTER 2026-10-20-api-keys.sql.
-- Safe to run more than once.
--
-- What it changes:
--   * tblUsers gets strTwoFactorSecret and strTwoFactorRecoveryCodes (both empty until someone
--     turns on two-factor sign-in).
--   * The first time it runs, every existing login is marked as having checked its email
--     (dtmDateAccountConfirmed), so nobody already using the site is locked out. Logins made
--     after that confirm their email from a link, once the site has email settings.
--   * uspGetLoginDetails also returns the email check date and two-factor settings.
--   * New: uspGetLoginById, uspSetLoginPassword, uspConfirmLoginEmail, uspSetTwoFactor.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblApiKeys', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-20-api-keys.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF COL_LENGTH('dbo.tblUsers', 'strTwoFactorSecret') IS NULL
BEGIN
    ALTER TABLE dbo.tblUsers ADD strTwoFactorSecret VARCHAR(500) NULL, strTwoFactorRecoveryCodes VARCHAR(2000) NULL;

    -- Only on the first run: everyone who already has a login keeps signing in as before
    UPDATE dbo.tblUsers
    SET dtmDateAccountConfirmed = ISNULL(dtmDateAdded, CAST(GETDATE() AS DATE))
    WHERE dtmDateAccountConfirmed IS NULL;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetLoginDetails]
(
    @strLoginEmail VarChar(500)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        14-Oct-2026, 21-Oct-2026 (email check and two-factor settings)
-- Description: Returns the login account for an email address and its
--              stored password (a PBKDF2 hash, or legacy plain text).
--              Extra portfolios share the owner's email, so the oldest
--              row (the account the person registered) is the login.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT TOP 1 iUserId, strLoginEmail, strLoginPassword, dtmDateAccountConfirmed,
        strTwoFactorSecret, strTwoFactorRecoveryCodes
    FROM tblUsers
    WHERE strLoginEmail = @strLoginEmail
    ORDER BY iUserId ASC;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetLoginById]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        21-Oct-2026
-- Description: The same details as uspGetLoginDetails, for a login id
--              (password reset links, email checks and two-factor sign-in).
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT iUserId, strLoginEmail, strLoginPassword, dtmDateAccountConfirmed,
        strTwoFactorSecret, strTwoFactorRecoveryCodes
    FROM tblUsers
    WHERE iUserId = @iUserId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSetLoginPassword]
(
    @iUserId        INT,
    @strNewPassword VarChar(500)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        21-Oct-2026
-- Description: Sets a new password (a PBKDF2 hash) after a password reset.
--              AddNewPortfolio copies the owner's password onto each extra
--              portfolio row, so every row with the login's email changes.
--              Following the emailed link also proves the email works, so
--              the email check is marked done.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @strLoginEmail VARCHAR(500) = (SELECT strLoginEmail FROM tblUsers WHERE iUserId = @iUserId);
    IF @strLoginEmail IS NULL
        RETURN;

    UPDATE tblUsers
    SET strLoginPassword = @strNewPassword
    WHERE strLoginEmail = @strLoginEmail;

    UPDATE tblUsers
    SET dtmDateAccountConfirmed = CAST(GETDATE() AS DATE)
    WHERE iUserId = @iUserId AND dtmDateAccountConfirmed IS NULL;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspConfirmLoginEmail]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        21-Oct-2026
-- Description: Marks a login's email as checked (the person followed the emailed link).
-- =============================================
BEGIN
    SET NOCOUNT ON;

    UPDATE tblUsers
    SET dtmDateAccountConfirmed = CAST(GETDATE() AS DATE)
    WHERE iUserId = @iUserId AND dtmDateAccountConfirmed IS NULL;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSetTwoFactor]
(
    @iUserId                   INT,
    @strTwoFactorSecret        VarChar(500)  = NULL,
    @strTwoFactorRecoveryCodes VarChar(2000) = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        21-Oct-2026
-- Description: Turns two-factor sign-in on (a protected authenticator secret
--              and hashed recovery codes) or off (both NULL), or replaces the
--              recovery codes. Only the login row itself changes.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    UPDATE tblUsers
    SET strTwoFactorSecret = @strTwoFactorSecret,
        strTwoFactorRecoveryCodes = @strTwoFactorRecoveryCodes
    WHERE iUserId = @iUserId;
END
GO

PRINT 'Account security migration done.';
GO
