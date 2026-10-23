-- Rental property reports and tax (roadmap 23 to 33, 23 Oct 2026): each property's agent statement code, state,
-- value and private use; co-owners; loans linked to a property (and private redraws on a loan); the sale of a
-- property; the termination value of a depreciating asset and the low-value pool. Safe to run twice.
-- Run after 2026-10-21-19-rental-properties.sql and 2026-10-21-12-debt-fixed-income.sql.
-- No USE line: pick your database in SSMS first.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblRentalProperties', 'U') IS NULL OR OBJECT_ID('dbo.tblLoans', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-12-debt-fixed-income.sql and 2026-10-21-19-rental-properties.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- strPayoutCode: the property code used on rental payouts (agent statements), so they fill the schedule.
-- strState: NSW, VIC, QLD, WA, SA, TAS, ACT or NT, for compliance reminders. dblValue and dtmValued: the latest valuation.
IF COL_LENGTH('dbo.tblRentalProperties', 'strPayoutCode') IS NULL
    ALTER TABLE dbo.tblRentalProperties ADD strPayoutCode NVARCHAR(50) NULL;
IF COL_LENGTH('dbo.tblRentalProperties', 'strState') IS NULL
    ALTER TABLE dbo.tblRentalProperties ADD strState VARCHAR(3) NULL;
IF COL_LENGTH('dbo.tblRentalProperties', 'dblValue') IS NULL
    ALTER TABLE dbo.tblRentalProperties ADD dblValue DECIMAL(19,4) NULL;
IF COL_LENGTH('dbo.tblRentalProperties', 'dtmValued') IS NULL
    ALTER TABLE dbo.tblRentalProperties ADD dtmValued DATE NULL;
GO

-- What a depreciating asset was sold or scrapped for, for the balancing adjustment
IF COL_LENGTH('dbo.tblRentalAssets', 'dblTerminationValue') IS NULL
    ALTER TABLE dbo.tblRentalAssets ADD dblTerminationValue DECIMAL(19,4) NULL;
GO

-- The other owners of a property. tblRentalProperties.dblOwnershipPct stays the portfolio owner's share.
IF OBJECT_ID('dbo.tblRentalOwners', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalOwners (
        iOwnerID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalOwners PRIMARY KEY,
        iPropertyID  INT NOT NULL,
        strName      NVARCHAR(200) NOT NULL,
        dblPct       DECIMAL(9,4) NOT NULL
    );
    CREATE INDEX IX_tblRentalOwners_Property ON dbo.tblRentalOwners (iPropertyID);
END
GO

-- A loan (Holdings > Margin and investment loans) used for a property. Its interest becomes the property's interest.
IF OBJECT_ID('dbo.tblRentalLoanLinks', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalLoanLinks (
        iLoanID      INT NOT NULL CONSTRAINT PK_tblRentalLoanLinks PRIMARY KEY,
        iPropertyID  INT NOT NULL
    );
    CREATE INDEX IX_tblRentalLoanLinks_Property ON dbo.tblRentalLoanLinks (iPropertyID);
END
GO

-- The sale of a property: the five cost base elements, the main residence days and the market value rule
IF OBJECT_ID('dbo.tblRentalSales', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalSales (
        iPropertyID                INT NOT NULL CONSTRAINT PK_tblRentalSales PRIMARY KEY,
        dtmAcquired                DATE NOT NULL,
        dtmContract                DATE NOT NULL,
        dblProceeds                DECIMAL(19,4) NOT NULL,
        dblPurchasePrice           DECIMAL(19,4) NOT NULL,   -- element 1
        dblAcquisitionCosts        DECIMAL(19,4) NOT NULL,   -- element 2 (stamp duty, legal fees, sale costs too)
        dblHoldingCosts            DECIMAL(19,4) NOT NULL,   -- element 3 (only costs never deducted)
        dblImprovements            DECIMAL(19,4) NOT NULL,   -- element 4
        dblTitleCosts              DECIMAL(19,4) NOT NULL,   -- element 5
        iDaysMainResidence         INT NOT NULL,
        iDaysAbsence               INT NOT NULL,             -- rented while away, treated as the main residence (6-year rule)
        dtmFirstRented             DATE NULL,
        dblMarketValueFirstRented  DECIMAL(19,4) NULL,
        strNote                    NVARCHAR(1000) NULL
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalPropertyDetails
    @iUserID INT, @iPropertyID INT, @strPayoutCode NVARCHAR(50) = NULL, @strState VARCHAR(3) = NULL,
    @dblValue DECIMAL(19,4) = NULL, @dtmValued DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @strPayoutCode = NULLIF(UPPER(LTRIM(RTRIM(@strPayoutCode))), '');
    SET @strState = NULLIF(UPPER(LTRIM(RTRIM(@strState))), '');
    IF @strState IS NOT NULL AND @strState NOT IN ('NSW', 'VIC', 'QLD', 'WA', 'SA', 'TAS', 'ACT', 'NT')
    BEGIN SELECT 'Error: Choose the state or territory.'; RETURN; END
    IF @dblValue < 0 BEGIN SELECT 'Error: The value can''t be negative.'; RETURN; END
    IF @strPayoutCode IS NOT NULL AND EXISTS (SELECT 1 FROM tblRentalProperties
        WHERE iUserID = @iUserID AND strPayoutCode = @strPayoutCode AND iPropertyID <> @iPropertyID)
    BEGIN SELECT 'Error: Another property already uses that agent statement code.'; RETURN; END
    UPDATE tblRentalProperties SET strPayoutCode = @strPayoutCode, strState = @strState, dblValue = @dblValue, dtmValued = @dtmValued
    WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalOwner @iUserID INT, @iPropertyID INT, @strName NVARCHAR(200), @dblPct DECIMAL(9,4)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @mine DECIMAL(9,4) = (SELECT dblOwnershipPct FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID);
    IF @mine IS NULL BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    IF LTRIM(RTRIM(ISNULL(@strName, ''))) = '' BEGIN SELECT 'Error: Enter the other owner''s name.'; RETURN; END
    IF ISNULL(@dblPct, 0) <= 0 BEGIN SELECT 'Error: Enter their share as a percentage.'; RETURN; END
    IF @mine + @dblPct + ISNULL((SELECT SUM(dblPct) FROM tblRentalOwners WHERE iPropertyID = @iPropertyID), 0) > 100.0001
    BEGIN SELECT 'Error: The shares would add up to more than 100%.'; RETURN; END
    INSERT INTO tblRentalOwners (iPropertyID, strName, dblPct) VALUES (@iPropertyID, LTRIM(RTRIM(@strName)), @dblPct);
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalOwner @iUserID INT, @iOwnerID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE O FROM tblRentalOwners O JOIN tblRentalProperties P ON P.iPropertyID = O.iPropertyID
    WHERE O.iOwnerID = @iOwnerID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That owner was not found.'; RETURN; END
    SELECT 'Success';
END
GO

-- Links a loan to a property, or with @iPropertyID 0 unlinks it
CREATE OR ALTER PROCEDURE dbo.uspLinkRentalLoan @iUserID INT, @iLoanID INT, @iPropertyID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblLoans WHERE iLoanID = @iLoanID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That loan was not found.'; RETURN; END
    IF ISNULL(@iPropertyID, 0) <> 0 AND NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    DELETE FROM tblRentalLoanLinks WHERE iLoanID = @iLoanID;
    IF ISNULL(@iPropertyID, 0) <> 0
        INSERT INTO tblRentalLoanLinks (iLoanID, iPropertyID) VALUES (@iLoanID, @iPropertyID);
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalSale
    @iUserID INT, @iPropertyID INT, @dtmAcquired DATE, @dtmContract DATE, @dblProceeds DECIMAL(19,4), @dblPurchasePrice DECIMAL(19,4),
    @dblAcquisitionCosts DECIMAL(19,4) = 0, @dblHoldingCosts DECIMAL(19,4) = 0, @dblImprovements DECIMAL(19,4) = 0, @dblTitleCosts DECIMAL(19,4) = 0,
    @iDaysMainResidence INT = 0, @iDaysAbsence INT = 0, @dtmFirstRented DATE = NULL, @dblMarketValueFirstRented DECIMAL(19,4) = NULL,
    @strNote NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    IF @dtmAcquired IS NULL OR @dtmContract IS NULL OR @dtmContract < @dtmAcquired
    BEGIN SELECT 'Error: Enter the purchase date and a sale contract date after it.'; RETURN; END
    IF ISNULL(@dblProceeds, -1) < 0 OR ISNULL(@dblPurchasePrice, -1) < 0
        OR ISNULL(@dblAcquisitionCosts, 0) < 0 OR ISNULL(@dblHoldingCosts, 0) < 0 OR ISNULL(@dblImprovements, 0) < 0 OR ISNULL(@dblTitleCosts, 0) < 0
    BEGIN SELECT 'Error: Amounts can''t be negative.'; RETURN; END
    IF ISNULL(@iDaysMainResidence, 0) < 0 OR ISNULL(@iDaysAbsence, 0) < 0
        OR ISNULL(@iDaysMainResidence, 0) + ISNULL(@iDaysAbsence, 0) > DATEDIFF(day, @dtmAcquired, @dtmContract) + 1
    BEGIN SELECT 'Error: The main residence days are more than the days the property was owned.'; RETURN; END
    IF @dtmFirstRented IS NOT NULL AND (@dtmFirstRented < @dtmAcquired OR @dtmFirstRented > @dtmContract)
    BEGIN SELECT 'Error: The date first rented must be between the purchase and the sale.'; RETURN; END

    DELETE FROM tblRentalSales WHERE iPropertyID = @iPropertyID;
    INSERT INTO tblRentalSales (iPropertyID, dtmAcquired, dtmContract, dblProceeds, dblPurchasePrice, dblAcquisitionCosts, dblHoldingCosts,
        dblImprovements, dblTitleCosts, iDaysMainResidence, iDaysAbsence, dtmFirstRented, dblMarketValueFirstRented, strNote)
    VALUES (@iPropertyID, @dtmAcquired, @dtmContract, @dblProceeds, @dblPurchasePrice, ISNULL(@dblAcquisitionCosts, 0), ISNULL(@dblHoldingCosts, 0),
        ISNULL(@dblImprovements, 0), ISNULL(@dblTitleCosts, 0), ISNULL(@iDaysMainResidence, 0), ISNULL(@iDaysAbsence, 0),
        @dtmFirstRented, CASE WHEN @dtmFirstRented IS NULL THEN NULL ELSE @dblMarketValueFirstRented END, NULLIF(LTRIM(RTRIM(@strNote)), ''));
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalSale @iUserID INT, @iPropertyID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE S FROM tblRentalSales S JOIN tblRentalProperties P ON P.iPropertyID = S.iPropertyID
    WHERE S.iPropertyID = @iPropertyID AND P.iUserID = @iUserID;
    SELECT 'Success';
END
GO

-- 23-Oct-2026: also the low-value pool (method LV) and the termination value of a disposed asset
CREATE OR ALTER PROCEDURE dbo.uspSaveRentalAsset
    @iUserID INT, @iPropertyID INT, @iAssetID INT = 0, @strKind VARCHAR(10), @strDescription NVARCHAR(200), @dtmStart DATE,
    @dblCost DECIMAL(19,4), @dblLifeYears DECIMAL(9,2) = NULL, @strMethod VARCHAR(2) = NULL, @dblRate DECIMAL(9,4) = NULL, @dtmDisposed DATE = NULL,
    @dblTerminationValue DECIMAL(19,4) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strKind NOT IN ('Plant', 'Building') BEGIN SELECT 'Error: Choose a depreciating asset or capital works.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@dblCost, 0) <= 0 BEGIN SELECT 'Error: Enter the cost.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strKind = 'Plant' AND ISNULL(@strMethod, '') NOT IN ('DV', 'PC', 'LV')
    BEGIN SELECT 'Error: Choose diminishing value, prime cost or the low-value pool.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strKind = 'Plant' AND @strMethod IN ('DV', 'PC') AND ISNULL(@dblLifeYears, 0) <= 0
    BEGIN SELECT 'Error: Enter the effective life.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strKind = 'Building' AND ISNULL(@dblRate, 0) <= 0
    BEGIN SELECT 'Error: Enter the capital works rate (2.5% or 4%).' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @dtmDisposed IS NOT NULL AND @dtmDisposed < @dtmStart
    BEGIN SELECT 'Error: The disposal date is before the start date.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @dblTerminationValue < 0
    BEGIN SELECT 'Error: The amount it was sold for can''t be negative.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END

    DECLARE @termination DECIMAL(19,4) = CASE WHEN @strKind = 'Plant' AND @dtmDisposed IS NOT NULL THEN @dblTerminationValue END;
    IF ISNULL(@iAssetID, 0) = 0
    BEGIN
        INSERT INTO tblRentalAssets (iPropertyID, strKind, strDescription, dtmStart, dblCost, dblLifeYears, strMethod, dblRate, dtmDisposed, dblTerminationValue)
        VALUES (@iPropertyID, @strKind, @strDescription, @dtmStart, @dblCost,
                CASE WHEN @strKind = 'Plant' AND @strMethod <> 'LV' THEN @dblLifeYears END, CASE WHEN @strKind = 'Plant' THEN @strMethod END,
                CASE WHEN @strKind = 'Building' THEN @dblRate END, @dtmDisposed, @termination);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblRentalAssets
    SET strKind = @strKind, strDescription = @strDescription, dtmStart = @dtmStart, dblCost = @dblCost,
        dblLifeYears = CASE WHEN @strKind = 'Plant' AND @strMethod <> 'LV' THEN @dblLifeYears END, strMethod = CASE WHEN @strKind = 'Plant' THEN @strMethod END,
        dblRate = CASE WHEN @strKind = 'Building' THEN @dblRate END, dtmDisposed = @dtmDisposed, dblTerminationValue = @termination
    WHERE iAssetID = @iAssetID AND iPropertyID = @iPropertyID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That asset was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iAssetID AS Id;
END
GO

-- 23-Oct-2026: deleting a property also removes its owners, loan links and sale
CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalProperty @iUserID INT, @iPropertyID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    BEGIN TRAN;
    DELETE FROM tblRentalAmounts WHERE iPropertyID = @iPropertyID;
    DELETE FROM tblRentalAssets WHERE iPropertyID = @iPropertyID;
    DELETE FROM tblRentalOwners WHERE iPropertyID = @iPropertyID;
    DELETE FROM tblRentalLoanLinks WHERE iPropertyID = @iPropertyID;
    DELETE FROM tblRentalSales WHERE iPropertyID = @iPropertyID;
    IF OBJECT_ID('dbo.tblRentalTenancies', 'U') IS NOT NULL
        EXEC sp_executesql N'
            DELETE FROM tblRentalRentPayments WHERE iTenancyID IN (SELECT iTenancyID FROM tblRentalTenancies WHERE iPropertyID = @p);
            DELETE FROM tblRentalTenancies WHERE iPropertyID = @p;
            DELETE FROM tblRentalTenantBills WHERE iPropertyID = @p;
            DELETE FROM tblRentalJobs WHERE iPropertyID = @p;
            DELETE FROM tblRentalInspections WHERE iPropertyID = @p;
            DELETE FROM tblRentalCompliance WHERE iPropertyID = @p;', N'@p INT', @p = @iPropertyID;
    DELETE FROM tblRentalProperties WHERE iPropertyID = @iPropertyID;
    COMMIT;
    SELECT 'Success';
END
GO

-- 23-Oct-2026: a private redraw (Redraw) adds to the balance but its share of the interest isn't deductible
CREATE OR ALTER PROCEDURE dbo.uspAddLoanEntry
    @iUserID    INT,
    @iLoanID    INT,
    @dtmDate    DATE,
    @strKind    VARCHAR(20),
    @dblAmount  FLOAT,
    @strComment NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLoans WHERE iLoanID = @iLoanID AND iUserID = @iUserID)
        SELECT 'Error: That loan isn''t in this portfolio.' AS Result, NULL AS Id;
    ELSE IF @strKind NOT IN ('Drawdown', 'Repayment', 'Interest', 'Fee', 'Redraw') OR ISNULL(@dblAmount, 0) <= 0 OR @dtmDate IS NULL
        SELECT 'Error: Choose the type of entry and enter an amount more than nil.' AS Result, NULL AS Id;
    ELSE IF @Locked IS NOT NULL AND @dtmDate <= @Locked
        SELECT 'Error: That date is in a locked period.' AS Result, NULL AS Id;
    ELSE
    BEGIN
        INSERT dbo.tblLoanEntries (iLoanID, dtmDate, strKind, dblAmount, strComment) VALUES (@iLoanID, @dtmDate, @strKind, @dblAmount, @strComment);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
    END
END
GO

-- 23-Oct-2026: the messages start "Error:" so the site shows them
CREATE OR ALTER PROCEDURE dbo.uspSaveRentalProperty
    @iUserID INT, @iPropertyID INT = 0, @strAddress NVARCHAR(300), @dtmFirstEarned DATE = NULL,
    @dblOwnershipPct DECIMAL(9,4) = 100, @bActive BIT = 1, @strNote NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @strAddress = LTRIM(RTRIM(ISNULL(@strAddress, '')));
    IF @strAddress = '' BEGIN SELECT 'Error: Enter the property address.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @dblOwnershipPct <= 0 OR @dblOwnershipPct > 100 BEGIN SELECT 'Error: The ownership share must be more than 0% and at most 100%.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END

    IF ISNULL(@iPropertyID, 0) = 0
    BEGIN
        INSERT INTO tblRentalProperties (iUserID, strAddress, dtmFirstEarned, dblOwnershipPct, bActive, strNote)
        VALUES (@iUserID, @strAddress, @dtmFirstEarned, @dblOwnershipPct, ISNULL(@bActive, 1), @strNote);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblRentalProperties
    SET strAddress = @strAddress, dtmFirstEarned = @dtmFirstEarned, dblOwnershipPct = @dblOwnershipPct,
        bActive = ISNULL(@bActive, 1), strNote = @strNote
    WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That property was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iPropertyID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalAmounts @iUserID INT, @iPropertyID INT, @iYear INT, @xmlAmounts XML
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    BEGIN TRAN;
    DELETE FROM tblRentalAmounts WHERE iPropertyID = @iPropertyID AND iYear = @iYear;
    INSERT INTO tblRentalAmounts (iPropertyID, iYear, strCategory, dblAmount)
    SELECT @iPropertyID, @iYear, X.c, SUM(X.v)
    FROM (SELECT A.n.value('@c', 'varchar(30)') AS c, A.n.value('@v', 'decimal(19,4)') AS v FROM @xmlAmounts.nodes('/a') A(n)) X
    WHERE ISNULL(X.c, '') <> '' AND ISNULL(X.v, 0) <> 0
    GROUP BY X.c;
    COMMIT;
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalAsset @iUserID INT, @iAssetID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE A FROM tblRentalAssets A JOIN tblRentalProperties P ON P.iPropertyID = A.iPropertyID
    WHERE A.iAssetID = @iAssetID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That asset was not found.'; RETURN; END
    SELECT 'Success';
END
GO

SET NOEXEC OFF;
GO
