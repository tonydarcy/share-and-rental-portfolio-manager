-- =============================================
-- Migration: income tax workpaper
-- Date:      16-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the site build with
-- the Tax menu. It only adds tables and procedures. Safe to run more than once.
--
-- What it changes:
--   * tblTaxYear (new): one row per portfolio and financial year with the
--     figures the ledger can't supply: tax losses brought forward, capital
--     losses brought forward (NULL = work them out from the trades), the
--     company tax rate, the SMSF exempt current pension income percentage,
--     PAYG instalments paid and the trust distribution resolution date.
--   * tblTaxAdjustment (new): the year's reconciliation items, such as
--     non-deductible expenses added back or non-assessable income taken off.
--   * uspGetTaxYear, uspSaveTaxYear, uspSaveTaxAdjustment,
--     uspDeleteTaxAdjustment (new). The saves refuse a locked year.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblTaxYear', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTaxYear (
        iUserID                       INT NOT NULL,
        iYear                         INT NOT NULL,   -- the year the financial year ends in
        dblTaxLossesBroughtForward    DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblTaxYear_TaxLosses DEFAULT (0),
        dblCapitalLossesBroughtForward DECIMAL(18, 2) NULL,  -- NULL: worked out from the trades
        dblTaxRate                    DECIMAL(9, 4) NULL,    -- company rate, percent; NULL = 30
        dblEcpiPercent                DECIMAL(9, 4) NULL,    -- SMSF exempt current pension income, percent
        dblPaygInstalments            DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblTaxYear_Payg DEFAULT (0),
        dtResolutionDate              DATE NULL,             -- trust distribution resolution
        dtmChangedDate                DATETIME NOT NULL CONSTRAINT DF_tblTaxYear_Changed DEFAULT (GETDATE()),
        CONSTRAINT PK_tblTaxYear PRIMARY KEY (iUserID, iYear)
    );
END
GO

IF OBJECT_ID('dbo.tblTaxAdjustment', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTaxAdjustment (
        iAdjustmentID  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblTaxAdjustment PRIMARY KEY,
        iUserID        INT NOT NULL,
        iYear          INT NOT NULL,
        strKind        VARCHAR(20) NOT NULL,   -- AddBack, Assessable, NonAssessable, Deduction
        strDescription VARCHAR(200) NOT NULL,
        dblAmount      DECIMAL(18, 2) NOT NULL,
        dtmCreatedDate DATETIME NOT NULL CONSTRAINT DF_tblTaxAdjustment_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblTaxAdjustment_User ON dbo.tblTaxAdjustment (iUserID, iYear);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetTaxYear]
(
    @iUserId INT,
    @iYear   INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: The year's tax settings (no row if never saved), then its
--              reconciliation adjustments.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT dblTaxLossesBroughtForward, dblCapitalLossesBroughtForward, dblTaxRate, dblEcpiPercent,
           dblPaygInstalments, dtResolutionDate
    FROM tblTaxYear WHERE iUserID = @iUserId AND iYear = @iYear;

    SELECT iAdjustmentID, strKind, strDescription, dblAmount
    FROM tblTaxAdjustment WHERE iUserID = @iUserId AND iYear = @iYear
    ORDER BY iAdjustmentID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveTaxYear]
(
    @iUserId                        INT,
    @iYear                          INT,
    @dtYearEnd                      DATE,   -- the year's last day, for the lock check
    @dblTaxLossesBroughtForward     DECIMAL(18, 2),
    @dblCapitalLossesBroughtForward DECIMAL(18, 2) = NULL,
    @dblTaxRate                     DECIMAL(9, 4) = NULL,
    @dblEcpiPercent                 DECIMAL(9, 4) = NULL,
    @dblPaygInstalments             DECIMAL(18, 2) = 0,
    @dtResolutionDate               DATE = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds or replaces the year's tax settings.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its tax figures can''t change. Unlock it under Accounting > Lock periods first.' AS Result;
        RETURN;
    END
    IF @dblTaxLossesBroughtForward < 0 OR ISNULL(@dblCapitalLossesBroughtForward, 0) < 0 OR ISNULL(@dblPaygInstalments, 0) < 0
    BEGIN
        SELECT 'Error: Losses brought forward and instalments can''t be negative.' AS Result;
        RETURN;
    END
    IF ISNULL(@dblTaxRate, 0) NOT BETWEEN 0 AND 100 OR ISNULL(@dblEcpiPercent, 0) NOT BETWEEN 0 AND 100
    BEGIN
        SELECT 'Error: Percentages must be between 0 and 100.' AS Result;
        RETURN;
    END

    UPDATE tblTaxYear
    SET dblTaxLossesBroughtForward = @dblTaxLossesBroughtForward,
        dblCapitalLossesBroughtForward = @dblCapitalLossesBroughtForward,
        dblTaxRate = @dblTaxRate,
        dblEcpiPercent = @dblEcpiPercent,
        dblPaygInstalments = ISNULL(@dblPaygInstalments, 0),
        dtResolutionDate = @dtResolutionDate,
        dtmChangedDate = GETDATE()
    WHERE iUserID = @iUserId AND iYear = @iYear;

    IF @@ROWCOUNT = 0
        INSERT INTO tblTaxYear (iUserID, iYear, dblTaxLossesBroughtForward, dblCapitalLossesBroughtForward,
                                dblTaxRate, dblEcpiPercent, dblPaygInstalments, dtResolutionDate)
        VALUES (@iUserId, @iYear, @dblTaxLossesBroughtForward, @dblCapitalLossesBroughtForward,
                @dblTaxRate, @dblEcpiPercent, ISNULL(@dblPaygInstalments, 0), @dtResolutionDate);

    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveTaxAdjustment]
(
    @iUserId        INT,
    @iYear          INT,
    @dtYearEnd      DATE,
    @strKind        VARCHAR(20),
    @strDescription VARCHAR(200),
    @dblAmount      DECIMAL(18, 2)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds a reconciliation adjustment to the year.
--              Returns one row: Result = 'Success', AdjustmentId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @strDescription = LTRIM(RTRIM(ISNULL(@strDescription, '')));
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its tax figures can''t change.' AS Result, NULL AS AdjustmentId;
        RETURN;
    END
    IF ISNULL(@strKind, '') NOT IN ('AddBack', 'Assessable', 'NonAssessable', 'Deduction')
    BEGIN
        SELECT 'Error: Choose the kind of adjustment.' AS Result, NULL AS AdjustmentId;
        RETURN;
    END
    IF @strDescription = '' OR ISNULL(@dblAmount, 0) <= 0
    BEGIN
        SELECT 'Error: Enter a description and an amount more than zero.' AS Result, NULL AS AdjustmentId;
        RETURN;
    END

    INSERT INTO tblTaxAdjustment (iUserID, iYear, strKind, strDescription, dblAmount)
    VALUES (@iUserId, @iYear, @strKind, @strDescription, @dblAmount);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS AdjustmentId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteTaxAdjustment]
(
    @iUserId       INT,
    @iYear         INT,
    @dtYearEnd     DATE,
    @iAdjustmentID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Deletes one of the year's reconciliation adjustments.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its tax figures can''t change.' AS Result;
        RETURN;
    END
    DELETE FROM tblTaxAdjustment WHERE iAdjustmentID = @iAdjustmentID AND iUserID = @iUserId AND iYear = @iYear;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That adjustment was not found in this portfolio and year.' AS Result;
    ELSE
        SELECT 'Success' AS Result;
END
GO
