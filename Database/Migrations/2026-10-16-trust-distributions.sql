-- =============================================
-- Migration: trust distribution statement
-- Date:      16-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the site build with
-- Tax > Trust distribution. Run it after 2026-10-16-tax-workpaper.sql. It only
-- adds a table and procedures. Safe to run more than once.
--
-- What it changes:
--   * tblTrustBeneficiary (new): each year's beneficiaries and the share of
--     the trust's income each is entitled to. No TFNs are stored.
--   * uspGetTrustBeneficiaries, uspSaveTrustBeneficiary,
--     uspDeleteTrustBeneficiary, uspCopyTrustBeneficiaries (new). The
--     changes refuse a locked year.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblTrustBeneficiary', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTrustBeneficiary (
        iBeneficiaryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblTrustBeneficiary PRIMARY KEY,
        iUserID        INT NOT NULL,
        iYear          INT NOT NULL,
        strName        VARCHAR(200) NOT NULL,
        strType        VARCHAR(20) NOT NULL CONSTRAINT DF_tblTrustBeneficiary_Type DEFAULT ('Individual'),
        dblPercent     DECIMAL(9, 4) NOT NULL,
        dtmCreatedDate DATETIME NOT NULL CONSTRAINT DF_tblTrustBeneficiary_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblTrustBeneficiary_User ON dbo.tblTrustBeneficiary (iUserID, iYear);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetTrustBeneficiaries]
(
    @iUserId INT,
    @iYear   INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: The year's beneficiaries, largest share first.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT iBeneficiaryID, strName, strType, dblPercent
    FROM tblTrustBeneficiary
    WHERE iUserID = @iUserId AND iYear = @iYear
    ORDER BY dblPercent DESC, strName;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveTrustBeneficiary]
(
    @iUserId        INT,
    @iYear          INT,
    @dtYearEnd      DATE,
    @iBeneficiaryID INT = NULL,     -- NULL adds one
    @strName        VARCHAR(200),
    @strType        VARCHAR(20),
    @dblPercent     DECIMAL(9, 4)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds or changes a beneficiary. The year's shares can't add
--              up to more than 100%.
--              Returns one row: Result = 'Success', BeneficiaryId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its distribution can''t change.' AS Result, NULL AS BeneficiaryId;
        RETURN;
    END
    IF @strName = ''
    BEGIN
        SELECT 'Error: Enter the beneficiary''s name.' AS Result, NULL AS BeneficiaryId;
        RETURN;
    END
    IF ISNULL(@strType, '') NOT IN ('Individual', 'Company', 'Trust', 'Other')
    BEGIN
        SELECT 'Error: Choose the kind of beneficiary.' AS Result, NULL AS BeneficiaryId;
        RETURN;
    END
    IF ISNULL(@dblPercent, 0) <= 0 OR @dblPercent > 100
    BEGIN
        SELECT 'Error: The share must be more than 0% and no more than 100%.' AS Result, NULL AS BeneficiaryId;
        RETURN;
    END
    IF @dblPercent + ISNULL((SELECT SUM(dblPercent) FROM tblTrustBeneficiary
                             WHERE iUserID = @iUserId AND iYear = @iYear AND iBeneficiaryID <> ISNULL(@iBeneficiaryID, 0)), 0) > 100
    BEGIN
        SELECT 'Error: The shares would add up to more than 100%.' AS Result, NULL AS BeneficiaryId;
        RETURN;
    END

    IF @iBeneficiaryID IS NULL
    BEGIN
        INSERT INTO tblTrustBeneficiary (iUserID, iYear, strName, strType, dblPercent)
        VALUES (@iUserId, @iYear, @strName, @strType, @dblPercent);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS BeneficiaryId;
        RETURN;
    END

    UPDATE tblTrustBeneficiary
    SET strName = @strName, strType = @strType, dblPercent = @dblPercent
    WHERE iBeneficiaryID = @iBeneficiaryID AND iUserID = @iUserId AND iYear = @iYear;

    IF @@ROWCOUNT = 0
        SELECT 'Error: That beneficiary was not found in this portfolio and year.' AS Result, NULL AS BeneficiaryId;
    ELSE
        SELECT 'Success' AS Result, @iBeneficiaryID AS BeneficiaryId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteTrustBeneficiary]
(
    @iUserId        INT,
    @iYear          INT,
    @dtYearEnd      DATE,
    @iBeneficiaryID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Removes a beneficiary from the year.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its distribution can''t change.' AS Result;
        RETURN;
    END
    DELETE FROM tblTrustBeneficiary WHERE iBeneficiaryID = @iBeneficiaryID AND iUserID = @iUserId AND iYear = @iYear;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That beneficiary was not found in this portfolio and year.' AS Result;
    ELSE
        SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspCopyTrustBeneficiaries]
(
    @iUserId   INT,
    @iYear     INT,
    @dtYearEnd DATE
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Copies last year's beneficiaries and shares into a year that
--              has none yet.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its distribution can''t change.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblTrustBeneficiary WHERE iUserID = @iUserId AND iYear = @iYear)
    BEGIN
        SELECT 'Error: This year already has beneficiaries.' AS Result;
        RETURN;
    END
    INSERT INTO tblTrustBeneficiary (iUserID, iYear, strName, strType, dblPercent)
    SELECT iUserID, @iYear, strName, strType, dblPercent
    FROM tblTrustBeneficiary WHERE iUserID = @iUserId AND iYear = @iYear - 1;
    IF @@ROWCOUNT = 0
        SELECT 'Error: Last year has no beneficiaries to copy.' AS Result;
    ELSE
        SELECT 'Success' AS Result;
END
GO
