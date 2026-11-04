-- Outside Australia a client's business and company numbers (VAT number, GSTIN, EIN, NZBN and so on) can be longer
-- than an ABN (11) or ACN (9), so the columns and uspSaveClientDetails take up to 20 characters. Safe to run twice.
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('dbo.tblClientDetails', 'strABN') < 20
    ALTER TABLE dbo.tblClientDetails ALTER COLUMN strABN VARCHAR(20) NULL;
IF COL_LENGTH('dbo.tblClientDetails', 'strACN') < 20
    ALTER TABLE dbo.tblClientDetails ALTER COLUMN strACN VARCHAR(20) NULL;
IF COL_LENGTH('dbo.tblClientDetails', 'strTrusteeACN') < 20
    ALTER TABLE dbo.tblClientDetails ALTER COLUMN strTrusteeACN VARCHAR(20) NULL;
GO

-- As in 2026-10-15-financial-statements.sql, with the number parameters widened
CREATE OR ALTER PROCEDURE [dbo].[uspSaveClientDetails]
(
    @iUserId           INT,
    @strLegalName      VARCHAR(500),
    @strEntityType     VARCHAR(20),
    @strABN            VARCHAR(20) = NULL,
    @strACN            VARCHAR(20) = NULL,
    @strTrusteeName    VARCHAR(500) = NULL,
    @strTrusteeType    VARCHAR(20) = NULL,
    @strTrusteeACN     VARCHAR(20) = NULL,
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
