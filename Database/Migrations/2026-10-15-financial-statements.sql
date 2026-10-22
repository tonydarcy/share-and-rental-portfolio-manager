-- =============================================
-- Migration: client and practice details for financial statements
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the new site build
-- with the Clients, Client details and Practice details pages.
-- The old site keeps working: it only adds tables and procedures; nothing
-- existing is changed, dropped or renamed.
-- Safe to run more than once.
--
-- What it adds:
--   * tblClientDetails: one row per portfolio (tblUsers.iUserId) with the
--     details the financial statements print: legal name, entity type
--     (Individual, Partnership, Company, Trust, SMSF), ABN, ACN, trustee,
--     address and the reporting basis wording.
--   * tblClientOfficeholders: the directors, trustees, partners or members
--     who sign the statements, per portfolio.
--   * tblPracticeDetails: the preparer's practice (practice name, preparer,
--     tax agent number, address), one row per login (tblUsers.iUserId of the
--     signed-in user, not of the client portfolio).
--   * uspGetClientDetails, uspSaveClientDetails, uspDeleteClientOfficeholders,
--     uspAddClientOfficeholder, uspGetClientList, uspGetPracticeDetails and
--     uspSavePracticeDetails.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- 1. Tables
-- =============================================
IF OBJECT_ID('dbo.tblClientDetails', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[tblClientDetails](
        [iUserID] [int] NOT NULL,
        [strLegalName] [varchar](500) NOT NULL,
        [strEntityType] [varchar](20) NOT NULL,
        [strABN] [varchar](11) NULL,
        [strACN] [varchar](9) NULL,
        [strTrusteeName] [varchar](500) NULL,
        [strTrusteeType] [varchar](20) NULL,
        [strTrusteeACN] [varchar](9) NULL,
        [strAddressLine1] [varchar](200) NULL,
        [strAddressLine2] [varchar](200) NULL,
        [strSuburb] [varchar](100) NULL,
        [strState] [varchar](3) NULL,
        [strPostcode] [varchar](4) NULL,
        [strReportingBasis] [varchar](500) NOT NULL CONSTRAINT [DF_tblClientDetails_ReportingBasis] DEFAULT ('special purpose financial report'),
        [dtmUpdated] [datetime] NOT NULL CONSTRAINT [DF_tblClientDetails_Updated] DEFAULT (GETDATE()),
        [iUpdatedByUserID] [int] NULL,
        CONSTRAINT [PK_tblClientDetails] PRIMARY KEY CLUSTERED ([iUserID] ASC),
        CONSTRAINT [FK_tblClientDetails_Users] FOREIGN KEY ([iUserID]) REFERENCES [dbo].[tblUsers] ([iUserId])
    );
END
GO

IF OBJECT_ID('dbo.tblClientOfficeholders', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[tblClientOfficeholders](
        [iOfficeholderID] [int] IDENTITY(1,1) NOT NULL,
        [iUserID] [int] NOT NULL,
        [strName] [varchar](200) NOT NULL,
        [strRole] [varchar](50) NOT NULL,
        [iSortOrder] [int] NOT NULL CONSTRAINT [DF_tblClientOfficeholders_Sort] DEFAULT (0),
        CONSTRAINT [PK_tblClientOfficeholders] PRIMARY KEY CLUSTERED ([iOfficeholderID] ASC),
        CONSTRAINT [FK_tblClientOfficeholders_Users] FOREIGN KEY ([iUserID]) REFERENCES [dbo].[tblUsers] ([iUserId])
    );
    CREATE INDEX [IX_tblClientOfficeholders_User] ON [dbo].[tblClientOfficeholders] ([iUserID], [iSortOrder]);
END
GO

IF OBJECT_ID('dbo.tblPracticeDetails', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[tblPracticeDetails](
        [iUserID] [int] NOT NULL,
        [strPracticeName] [varchar](200) NOT NULL,
        [strPreparerName] [varchar](200) NOT NULL,
        [strTaxAgentNumber] [varchar](20) NULL,
        [strProfessionalBody] [varchar](100) NULL,
        [strAddressLine1] [varchar](200) NULL,
        [strAddressLine2] [varchar](200) NULL,
        [strSuburb] [varchar](100) NULL,
        [strState] [varchar](3) NULL,
        [strPostcode] [varchar](4) NULL,
        [strPhone] [varchar](50) NULL,
        [strEmail] [varchar](200) NULL,
        [dtmUpdated] [datetime] NOT NULL CONSTRAINT [DF_tblPracticeDetails_Updated] DEFAULT (GETDATE()),
        CONSTRAINT [PK_tblPracticeDetails] PRIMARY KEY CLUSTERED ([iUserID] ASC),
        CONSTRAINT [FK_tblPracticeDetails_Users] FOREIGN KEY ([iUserID]) REFERENCES [dbo].[tblUsers] ([iUserId])
    );
END
GO

-- =============================================
-- 2. Client details
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetClientDetails]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: A portfolio's client details for its financial statements.
--              Result 1: one row (none if the portfolio doesn't exist), with
--              the portfolio's own name and type; the details columns are
--              NULL until they are first saved (bSaved = 0).
--              Result 2: its officeholders in order.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT
        U.iUserId,
        U.strEntityName,
        U.strEntityType AS strPortfolioEntityType,
        CAST(CASE WHEN CD.iUserID IS NULL THEN 0 ELSE 1 END AS BIT) AS bSaved,
        CD.strLegalName,
        CD.strEntityType,
        CD.strABN,
        CD.strACN,
        CD.strTrusteeName,
        CD.strTrusteeType,
        CD.strTrusteeACN,
        CD.strAddressLine1,
        CD.strAddressLine2,
        CD.strSuburb,
        CD.strState,
        CD.strPostcode,
        CD.strReportingBasis,
        CD.dtmUpdated
    FROM tblUsers U
    LEFT JOIN tblClientDetails CD ON CD.iUserID = U.iUserId
    WHERE U.iUserId = @iUserId;

    SELECT iOfficeholderID, strName, strRole, iSortOrder
    FROM tblClientOfficeholders
    WHERE iUserID = @iUserId
    ORDER BY iSortOrder, iOfficeholderID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveClientDetails]
(
    @iUserId           INT,
    @strLegalName      VARCHAR(500),
    @strEntityType     VARCHAR(20),
    @strABN            VARCHAR(11) = NULL,
    @strACN            VARCHAR(9) = NULL,
    @strTrusteeName    VARCHAR(500) = NULL,
    @strTrusteeType    VARCHAR(20) = NULL,
    @strTrusteeACN     VARCHAR(9) = NULL,
    @strAddressLine1   VARCHAR(200) = NULL,
    @strAddressLine2   VARCHAR(200) = NULL,
    @strSuburb         VARCHAR(100) = NULL,
    @strState          VARCHAR(3) = NULL,
    @strPostcode       VARCHAR(4) = NULL,
    @strReportingBasis VARCHAR(500) = NULL,
    @iUpdatedByUserID  INT = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Adds or updates a portfolio's client details. The site checks
--              the ABN and ACN check digits first. Returns one row (Result):
--              'Saved' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM tblUsers WHERE iUserId = @iUserId)
    BEGIN
        SELECT 'Error: That portfolio doesn''t exist.' AS Result;
        RETURN;
    END

    IF LTRIM(RTRIM(ISNULL(@strLegalName, ''))) = ''
    BEGIN
        SELECT 'Error: Enter the client''s legal name.' AS Result;
        RETURN;
    END

    IF @strEntityType NOT IN ('Individual', 'Partnership', 'Company', 'Trust', 'SMSF')
    BEGIN
        SELECT 'Error: Choose the entity type.' AS Result;
        RETURN;
    END

    IF @strTrusteeType IS NOT NULL AND @strTrusteeType NOT IN ('Individual', 'Corporate')
    BEGIN
        SELECT 'Error: The trustee type must be Individual or Corporate.' AS Result;
        RETURN;
    END

    SET @strReportingBasis = NULLIF(LTRIM(RTRIM(ISNULL(@strReportingBasis, ''))), '');

    BEGIN TRANSACTION;

    UPDATE tblClientDetails WITH (UPDLOCK, HOLDLOCK)
    SET strLegalName = LTRIM(RTRIM(@strLegalName)),
        strEntityType = @strEntityType,
        strABN = NULLIF(@strABN, ''),
        strACN = NULLIF(@strACN, ''),
        strTrusteeName = NULLIF(LTRIM(RTRIM(ISNULL(@strTrusteeName, ''))), ''),
        strTrusteeType = @strTrusteeType,
        strTrusteeACN = NULLIF(@strTrusteeACN, ''),
        strAddressLine1 = NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine1, ''))), ''),
        strAddressLine2 = NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine2, ''))), ''),
        strSuburb = NULLIF(LTRIM(RTRIM(ISNULL(@strSuburb, ''))), ''),
        strState = NULLIF(@strState, ''),
        strPostcode = NULLIF(@strPostcode, ''),
        strReportingBasis = ISNULL(@strReportingBasis, 'special purpose financial report'),
        dtmUpdated = GETDATE(),
        iUpdatedByUserID = @iUpdatedByUserID
    WHERE iUserID = @iUserId;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO tblClientDetails (
            iUserID, strLegalName, strEntityType, strABN, strACN,
            strTrusteeName, strTrusteeType, strTrusteeACN,
            strAddressLine1, strAddressLine2, strSuburb, strState, strPostcode,
            strReportingBasis, dtmUpdated, iUpdatedByUserID
        )
        VALUES (
            @iUserId, LTRIM(RTRIM(@strLegalName)), @strEntityType, NULLIF(@strABN, ''), NULLIF(@strACN, ''),
            NULLIF(LTRIM(RTRIM(ISNULL(@strTrusteeName, ''))), ''), @strTrusteeType, NULLIF(@strTrusteeACN, ''),
            NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine1, ''))), ''), NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine2, ''))), ''),
            NULLIF(LTRIM(RTRIM(ISNULL(@strSuburb, ''))), ''), NULLIF(@strState, ''), NULLIF(@strPostcode, ''),
            ISNULL(@strReportingBasis, 'special purpose financial report'), GETDATE(), @iUpdatedByUserID
        );
    END

    COMMIT TRANSACTION;

    SELECT 'Saved' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteClientOfficeholders]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Removes a portfolio's officeholders before the site adds the
--              edited list back (it calls both in one transaction).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    DELETE FROM tblClientOfficeholders WHERE iUserID = @iUserId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddClientOfficeholder]
(
    @iUserId    INT,
    @strName    VARCHAR(200),
    @strRole    VARCHAR(50),
    @iSortOrder INT = 0
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Adds one director, trustee, partner or member to a portfolio.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF LTRIM(RTRIM(ISNULL(@strName, ''))) = '' OR LTRIM(RTRIM(ISNULL(@strRole, ''))) = ''
        RETURN;

    INSERT INTO tblClientOfficeholders (iUserID, strName, strRole, iSortOrder)
    VALUES (@iUserId, LTRIM(RTRIM(@strName)), LTRIM(RTRIM(@strRole)), ISNULL(@iSortOrder, 0));
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetClientList]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: The portfolios a login may open (the same rule as
--              GetPortfoliosUserHasPermissionToAccess) with their client
--              details, for the Clients page. @iUserId is the signed-in login.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @bAll BIT = CASE WHEN EXISTS (
        SELECT 1 FROM tblPortfolioPermissions
        WHERE iUserID = @iUserId AND iUserIDForPortfolioToAccess = 0) THEN 1 ELSE 0 END;

    SELECT
        U.iUserId,
        U.strEntityName,
        U.strEntityType AS strPortfolioEntityType,
        CAST(CASE WHEN CD.iUserID IS NULL THEN 0 ELSE 1 END AS BIT) AS bSaved,
        CD.strLegalName,
        CD.strEntityType,
        CD.strABN,
        CD.strACN,
        CD.dtmUpdated
    FROM tblUsers U
    LEFT JOIN tblClientDetails CD ON CD.iUserID = U.iUserId
    WHERE @bAll = 1
       OR U.iUserId = @iUserId
       OR U.iUserId IN (SELECT iUserIDForPortfolioToAccess FROM tblPortfolioPermissions WHERE iUserID = @iUserId)
    ORDER BY ISNULL(CD.strLegalName, U.strEntityName);
END
GO

-- =============================================
-- 3. Practice details (per login)
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetPracticeDetails]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: The signed-in login's practice details; no row until saved.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT
        iUserID,
        strPracticeName,
        strPreparerName,
        strTaxAgentNumber,
        strProfessionalBody,
        strAddressLine1,
        strAddressLine2,
        strSuburb,
        strState,
        strPostcode,
        strPhone,
        strEmail,
        dtmUpdated
    FROM tblPracticeDetails
    WHERE iUserID = @iUserId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSavePracticeDetails]
(
    @iUserId             INT,
    @strPracticeName     VARCHAR(200),
    @strPreparerName     VARCHAR(200),
    @strTaxAgentNumber   VARCHAR(20) = NULL,
    @strProfessionalBody VARCHAR(100) = NULL,
    @strAddressLine1     VARCHAR(200) = NULL,
    @strAddressLine2     VARCHAR(200) = NULL,
    @strSuburb           VARCHAR(100) = NULL,
    @strState            VARCHAR(3) = NULL,
    @strPostcode         VARCHAR(4) = NULL,
    @strPhone            VARCHAR(50) = NULL,
    @strEmail            VARCHAR(200) = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Adds or updates the signed-in login's practice details.
--              Returns one row (Result): 'Saved' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM tblUsers WHERE iUserId = @iUserId)
    BEGIN
        SELECT 'Error: That login doesn''t exist.' AS Result;
        RETURN;
    END

    IF LTRIM(RTRIM(ISNULL(@strPracticeName, ''))) = '' OR LTRIM(RTRIM(ISNULL(@strPreparerName, ''))) = ''
    BEGIN
        SELECT 'Error: Enter the practice name and the preparer''s name.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    UPDATE tblPracticeDetails WITH (UPDLOCK, HOLDLOCK)
    SET strPracticeName = LTRIM(RTRIM(@strPracticeName)),
        strPreparerName = LTRIM(RTRIM(@strPreparerName)),
        strTaxAgentNumber = NULLIF(LTRIM(RTRIM(ISNULL(@strTaxAgentNumber, ''))), ''),
        strProfessionalBody = NULLIF(LTRIM(RTRIM(ISNULL(@strProfessionalBody, ''))), ''),
        strAddressLine1 = NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine1, ''))), ''),
        strAddressLine2 = NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine2, ''))), ''),
        strSuburb = NULLIF(LTRIM(RTRIM(ISNULL(@strSuburb, ''))), ''),
        strState = NULLIF(@strState, ''),
        strPostcode = NULLIF(@strPostcode, ''),
        strPhone = NULLIF(LTRIM(RTRIM(ISNULL(@strPhone, ''))), ''),
        strEmail = NULLIF(LTRIM(RTRIM(ISNULL(@strEmail, ''))), ''),
        dtmUpdated = GETDATE()
    WHERE iUserID = @iUserId;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO tblPracticeDetails (
            iUserID, strPracticeName, strPreparerName, strTaxAgentNumber, strProfessionalBody,
            strAddressLine1, strAddressLine2, strSuburb, strState, strPostcode, strPhone, strEmail, dtmUpdated
        )
        VALUES (
            @iUserId, LTRIM(RTRIM(@strPracticeName)), LTRIM(RTRIM(@strPreparerName)),
            NULLIF(LTRIM(RTRIM(ISNULL(@strTaxAgentNumber, ''))), ''), NULLIF(LTRIM(RTRIM(ISNULL(@strProfessionalBody, ''))), ''),
            NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine1, ''))), ''), NULLIF(LTRIM(RTRIM(ISNULL(@strAddressLine2, ''))), ''),
            NULLIF(LTRIM(RTRIM(ISNULL(@strSuburb, ''))), ''), NULLIF(@strState, ''), NULLIF(@strPostcode, ''),
            NULLIF(LTRIM(RTRIM(ISNULL(@strPhone, ''))), ''), NULLIF(LTRIM(RTRIM(ISNULL(@strEmail, ''))), ''), GETDATE()
        );
    END

    COMMIT TRANSACTION;

    SELECT 'Saved' AS Result;
END
GO

-- What was added
SELECT name, type_desc
FROM sys.objects
WHERE name IN ('tblClientDetails', 'tblClientOfficeholders', 'tblPracticeDetails',
               'uspGetClientDetails', 'uspSaveClientDetails', 'uspDeleteClientOfficeholders',
               'uspAddClientOfficeholder', 'uspGetClientList', 'uspGetPracticeDetails', 'uspSavePracticeDetails')
ORDER BY type_desc, name;
GO
