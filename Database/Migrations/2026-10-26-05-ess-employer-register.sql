-- =============================================
-- Migration: employee share scheme register, for an employer's ESS annual report
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Tax > ESS annual report.
-- Run it after 2026-10-21-10-employee-shares-options.sql. Safe to run more than once. It only adds things.
--
-- A company (or other employer) that gives its employees shares, rights or options under an employee share scheme must
-- give each employee a statement by 14 July and lodge an ESS annual report with the ATO by 14 August.
--   * tblEssGrants: each grant of ESS interests to an employee (the employee's name and own reference, never their
--     TFN), its scheme type, when it was acquired, its market value and what the employee paid, and for a deferral
--     scheme the deferred taxing point and the market value then.
--   * uspSaveEssGrant and uspDeleteEssGrant (both refuse a locked year).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblEssStatements', 'U') IS NULL OR OBJECT_ID('dbo.fnLockedTo', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-10-employee-shares-options.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblEssGrants', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblEssGrants (
        iEssGrantID             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblEssGrants PRIMARY KEY,
        iUserID                 INT NOT NULL,                -- the employer's portfolio
        strEmployee             NVARCHAR(200) NOT NULL,
        strEmployeeRef          NVARCHAR(50) NULL,           -- payroll number or similar, never a TFN
        strScheme               VARCHAR(20) NOT NULL,        -- UpfrontEligible, UpfrontNotEligible, Deferral, StartUp
        strInterest             VARCHAR(20) NOT NULL,        -- Shares, Rights, Options
        dtmAcquired             DATE NOT NULL,
        dblInterests            FLOAT NOT NULL,
        dblMarketValue          FLOAT NOT NULL DEFAULT (0),  -- total market value when acquired
        dblAmountPaid           FLOAT NOT NULL DEFAULT (0),  -- total paid by the employee
        dblExercisePrice        FLOAT NULL,                  -- per right or option
        dtmTaxingPoint          DATE NULL,                   -- a deferral scheme's deferred taxing point, once it happens
        dblTaxingPointValue     FLOAT NULL,                  -- total market value at the deferred taxing point
        bTfnQuoted              BIT NOT NULL DEFAULT (1),    -- whether the employee gave their TFN (or ABN)
        dblTfnWithheld          FLOAT NOT NULL DEFAULT (0),  -- tax withheld because no TFN was given
        bForeignSource          BIT NOT NULL DEFAULT (0),    -- the discount is from foreign employment
        strComment              NVARCHAR(500) NULL,
        dtmSaved                DATETIME NOT NULL CONSTRAINT DF_tblEssGrants_Saved DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblEssGrants_User ON dbo.tblEssGrants (iUserID, dtmAcquired);
END
GO

-- Adds (@iEssGrantID 0) or changes a grant. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveEssGrant
    @iUserID             INT,
    @iEssGrantID         INT = 0,
    @strEmployee         NVARCHAR(200),
    @strEmployeeRef      NVARCHAR(50) = NULL,
    @strScheme           VARCHAR(20),
    @strInterest         VARCHAR(20),
    @dtmAcquired         DATE,
    @dblInterests        FLOAT,
    @dblMarketValue      FLOAT = 0,
    @dblAmountPaid       FLOAT = 0,
    @dblExercisePrice    FLOAT = NULL,
    @dtmTaxingPoint      DATE = NULL,
    @dblTaxingPointValue FLOAT = NULL,
    @bTfnQuoted          BIT = 1,
    @dblTfnWithheld      FLOAT = 0,
    @bForeignSource      BIT = 0,
    @strComment          NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @OldDate DATE = (SELECT ISNULL(dtmTaxingPoint, dtmAcquired) FROM dbo.tblEssGrants WHERE iEssGrantID = @iEssGrantID AND iUserID = @iUserID);
    DECLARE @Error NVARCHAR(200) = CASE
        WHEN ISNULL(@iEssGrantID, 0) > 0 AND @OldDate IS NULL THEN 'That grant isn''t in this portfolio.'
        WHEN @Locked IS NOT NULL AND (ISNULL(@dtmTaxingPoint, @dtmAcquired) <= @Locked OR @OldDate <= @Locked) THEN 'That year is locked.'
        WHEN LTRIM(RTRIM(ISNULL(@strEmployee, ''))) = '' THEN 'Enter the employee.'
        WHEN @strScheme NOT IN ('UpfrontEligible', 'UpfrontNotEligible', 'Deferral', 'StartUp') THEN 'Pick the kind of scheme.'
        WHEN @strInterest NOT IN ('Shares', 'Rights', 'Options') THEN 'Pick shares, rights or options.'
        WHEN ISNULL(@dblInterests, 0) <= 0 THEN 'Enter how many were granted.'
        WHEN @dblMarketValue < 0 OR @dblAmountPaid < 0 OR @dblTfnWithheld < 0 OR @dblTaxingPointValue < 0 THEN 'Amounts can''t be negative.'
        WHEN @dtmTaxingPoint IS NOT NULL AND @strScheme <> 'Deferral' THEN 'Only a deferral scheme has a deferred taxing point.'
        WHEN @dtmTaxingPoint < @dtmAcquired THEN 'The deferred taxing point can''t be before the grant.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, NULL AS Id;
        RETURN;
    END

    IF ISNULL(@iEssGrantID, 0) > 0
        UPDATE dbo.tblEssGrants
        SET strEmployee = LTRIM(RTRIM(@strEmployee)), strEmployeeRef = NULLIF(LTRIM(RTRIM(@strEmployeeRef)), ''), strScheme = @strScheme,
            strInterest = @strInterest, dtmAcquired = @dtmAcquired, dblInterests = @dblInterests, dblMarketValue = ISNULL(@dblMarketValue, 0),
            dblAmountPaid = ISNULL(@dblAmountPaid, 0), dblExercisePrice = @dblExercisePrice, dtmTaxingPoint = @dtmTaxingPoint,
            dblTaxingPointValue = @dblTaxingPointValue, bTfnQuoted = ISNULL(@bTfnQuoted, 1), dblTfnWithheld = ISNULL(@dblTfnWithheld, 0),
            bForeignSource = ISNULL(@bForeignSource, 0), strComment = NULLIF(LTRIM(RTRIM(@strComment)), ''), dtmSaved = GETDATE()
        WHERE iEssGrantID = @iEssGrantID AND iUserID = @iUserID;
    ELSE
    BEGIN
        INSERT INTO dbo.tblEssGrants (iUserID, strEmployee, strEmployeeRef, strScheme, strInterest, dtmAcquired, dblInterests, dblMarketValue,
            dblAmountPaid, dblExercisePrice, dtmTaxingPoint, dblTaxingPointValue, bTfnQuoted, dblTfnWithheld, bForeignSource, strComment)
        VALUES (@iUserID, LTRIM(RTRIM(@strEmployee)), NULLIF(LTRIM(RTRIM(@strEmployeeRef)), ''), @strScheme, @strInterest, @dtmAcquired,
            @dblInterests, ISNULL(@dblMarketValue, 0), ISNULL(@dblAmountPaid, 0), @dblExercisePrice, @dtmTaxingPoint, @dblTaxingPointValue,
            ISNULL(@bTfnQuoted, 1), ISNULL(@dblTfnWithheld, 0), ISNULL(@bForeignSource, 0), NULLIF(LTRIM(RTRIM(@strComment)), ''));
        SET @iEssGrantID = SCOPE_IDENTITY();
    END
    SELECT 'Success' AS Result, @iEssGrantID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteEssGrant
    @iUserID     INT,
    @iEssGrantID INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Date DATE = (SELECT ISNULL(dtmTaxingPoint, dtmAcquired) FROM dbo.tblEssGrants WHERE iEssGrantID = @iEssGrantID AND iUserID = @iUserID);
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF @Date IS NULL
        SELECT 'Error: That grant isn''t in this portfolio.' AS Result;
    ELSE IF @Locked IS NOT NULL AND @Date <= @Locked
        SELECT 'Error: That year is locked.' AS Result;
    ELSE
    BEGIN
        DELETE dbo.tblEssGrants WHERE iEssGrantID = @iEssGrantID AND iUserID = @iUserID;
        SELECT 'Success' AS Result;
    END
END
GO

SET NOEXEC OFF;
GO
