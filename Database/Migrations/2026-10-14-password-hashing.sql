-- =============================================
-- Migration: password hashing for Login.aspx
-- Date:      14-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE uploading the new
-- Login.aspx, Login.aspx.vb and App_Code/PasswordHasher.vb.
-- It only adds procedures, so the current site keeps working until then.
--
-- Existing plain text passwords are not changed here. Each one is
-- replaced with a hash the next time that person logs in successfully.
-- =============================================
IF OBJECT_ID('dbo.uspGetLoginDetails', 'P') IS NOT NULL DROP PROCEDURE dbo.uspGetLoginDetails
GO
CREATE PROCEDURE [dbo].[uspGetLoginDetails]
(
    @strLoginEmail VarChar(500)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        14-Oct-2026
-- Description: Returns the login account for an email address and its
--              stored password (a PBKDF2 hash, or legacy plain text).
--              Extra portfolios share the owner's email, so the oldest
--              row (the account the person registered) is the login.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT TOP 1 iUserId, strLoginPassword
    FROM tblUsers
    WHERE strLoginEmail = @strLoginEmail
    ORDER BY iUserId ASC;
END
GO

IF OBJECT_ID('dbo.uspRecordLoginAttempt', 'P') IS NOT NULL DROP PROCEDURE dbo.uspRecordLoginAttempt
GO
CREATE PROCEDURE [dbo].[uspRecordLoginAttempt]
(
    @iUserId     Integer = NULL,
    @bSuccessful Bit
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        14-Oct-2026
-- Description: Logs a login attempt to tblLog (same entries ValidateUser wrote)
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF @bSuccessful = 1
        INSERT INTO tblLog ([dtmDateAccessed], [iUserID], [strBreifComment], [strDetailedComment])
        VALUES (GETDATE(), @iUserId, 'Successful Login', 'Successful Login - Login.aspx')
    ELSE
        INSERT INTO tblLog ([dtmDateAccessed], [iUserID], [strBreifComment], [strDetailedComment])
        VALUES (GETDATE(), @iUserId, 'Failed Login', 'Failed Login - Login.aspx')
END
GO

IF OBJECT_ID('dbo.uspUpdateLoginPassword', 'P') IS NOT NULL DROP PROCEDURE dbo.uspUpdateLoginPassword
GO
CREATE PROCEDURE [dbo].[uspUpdateLoginPassword]
(
    @strLoginEmail  VarChar(500),
    @strOldPassword VarChar(500),
    @strNewPassword VarChar(500)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        14-Oct-2026
-- Description: Replaces a stored password with a new hash. Updates every
--              row for the email that still holds the old value, because
--              AddNewPortfolio copies the owner's password onto each
--              extra portfolio row.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    UPDATE tblUsers
    SET strLoginPassword = @strNewPassword
    WHERE strLoginEmail = @strLoginEmail
    AND strLoginPassword = @strOldPassword;
END
GO

-- Optional check after deploying: how many accounts still have plain text passwords
-- SELECT COUNT(*) AS PlainTextPasswords FROM tblUsers WHERE strLoginPassword NOT LIKE 'PBKDF2$%';
