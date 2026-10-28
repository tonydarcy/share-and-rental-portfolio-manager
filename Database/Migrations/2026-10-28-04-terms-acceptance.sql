-- =============================================
-- Terms of use: which version of the terms each login agreed to, when, and from where.
-- Used by the sign-up checkbox and the "please agree to the updated terms" page. Safe to run twice.
-- =============================================
IF OBJECT_ID('dbo.tblTermsAcceptances', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTermsAcceptances
    (
        iTermsAcceptanceId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblTermsAcceptances PRIMARY KEY,
        iUserId            INT          NOT NULL,
        strTermsVersion    VARCHAR(40)  NOT NULL,
        dtmAcceptedUtc     DATETIME2(0) NOT NULL CONSTRAINT DF_tblTermsAcceptances_When DEFAULT SYSUTCDATETIME(),
        strIpAddress       VARCHAR(45)  NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblTermsAcceptances_User' AND object_id = OBJECT_ID('dbo.tblTermsAcceptances'))
    CREATE INDEX IX_tblTermsAcceptances_User ON dbo.tblTermsAcceptances (iUserId, dtmAcceptedUtc) INCLUDE (strTermsVersion);
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddTermsAcceptance]
(
    @iUserId         INT,
    @strTermsVersion VARCHAR(40),
    @strIpAddress    VARCHAR(45) = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description: Records that a login agreed to a version of the terms of use and privacy policy.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.tblTermsAcceptances (iUserId, strTermsVersion, strIpAddress)
    VALUES (@iUserId, @strTermsVersion, @strIpAddress);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetTermsAcceptance]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description: The newest version of the terms a login agreed to, and when (no row if never).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT TOP 1 strTermsVersion, dtmAcceptedUtc
    FROM dbo.tblTermsAcceptances
    WHERE iUserId = @iUserId
    ORDER BY dtmAcceptedUtc DESC, iTermsAcceptanceId DESC;
END
GO
