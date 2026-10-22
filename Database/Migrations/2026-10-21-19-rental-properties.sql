-- Rental property schedule (roadmap 34): properties, each year's income and expenses by ATO category,
-- and depreciating assets (Division 40) and capital works (Division 43). Safe to run twice.
-- No USE line: pick your database in SSMS first.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblRentalProperties', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalProperties (
        iPropertyID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalProperties PRIMARY KEY,
        iUserID          INT NOT NULL,
        strAddress       NVARCHAR(300) NOT NULL,
        dtmFirstEarned   DATE NULL,
        dblOwnershipPct  DECIMAL(9,4) NOT NULL CONSTRAINT DF_tblRentalProperties_Pct DEFAULT (100),
        bActive          BIT NOT NULL CONSTRAINT DF_tblRentalProperties_Active DEFAULT (1),
        strNote          NVARCHAR(1000) NULL,
        dtmCreated       DATETIME NOT NULL CONSTRAINT DF_tblRentalProperties_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblRentalProperties_User ON dbo.tblRentalProperties (iUserID);
END
GO

-- One amount per property, financial year (the year it ends) and category, for the whole property
-- (the schedule takes the owner's share). Category 'Weeks' holds the weeks rented.
IF OBJECT_ID('dbo.tblRentalAmounts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalAmounts (
        iPropertyID  INT NOT NULL,
        iYear        INT NOT NULL,
        strCategory  VARCHAR(30) NOT NULL,
        dblAmount    DECIMAL(19,4) NOT NULL,
        CONSTRAINT PK_tblRentalAmounts PRIMARY KEY (iPropertyID, iYear, strCategory)
    );
END
GO

-- strKind Plant (Division 40: life and method DV or PC) or Building (Division 43: rate, 2.5 or 4)
IF OBJECT_ID('dbo.tblRentalAssets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalAssets (
        iAssetID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalAssets PRIMARY KEY,
        iPropertyID     INT NOT NULL,
        strKind         VARCHAR(10) NOT NULL,
        strDescription  NVARCHAR(200) NOT NULL,
        dtmStart        DATE NOT NULL,
        dblCost         DECIMAL(19,4) NOT NULL,
        dblLifeYears    DECIMAL(9,2) NULL,
        strMethod       VARCHAR(2) NULL,
        dblRate         DECIMAL(9,4) NULL,
        dtmDisposed     DATE NULL
    );
    CREATE INDEX IX_tblRentalAssets_Property ON dbo.tblRentalAssets (iPropertyID);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalProperty
    @iUserID INT, @iPropertyID INT = 0, @strAddress NVARCHAR(300), @dtmFirstEarned DATE = NULL,
    @dblOwnershipPct DECIMAL(9,4) = 100, @bActive BIT = 1, @strNote NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @strAddress = LTRIM(RTRIM(ISNULL(@strAddress, '')));
    IF @strAddress = '' BEGIN SELECT 'Enter the property address.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @dblOwnershipPct <= 0 OR @dblOwnershipPct > 100 BEGIN SELECT 'The ownership share must be more than 0% and at most 100%.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END

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
    IF @@ROWCOUNT = 0 BEGIN SELECT 'That property was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iPropertyID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalProperty @iUserID INT, @iPropertyID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'That property was not found.'; RETURN; END
    BEGIN TRAN;
    DELETE FROM tblRentalAmounts WHERE iPropertyID = @iPropertyID;
    DELETE FROM tblRentalAssets WHERE iPropertyID = @iPropertyID;
    DELETE FROM tblRentalProperties WHERE iPropertyID = @iPropertyID;
    COMMIT;
    SELECT 'Success';
END
GO

-- Replaces the year's amounts with <a c="GrossRent" v="1234.50"/> elements (blank or nil amounts are dropped)
CREATE OR ALTER PROCEDURE dbo.uspSaveRentalAmounts @iUserID INT, @iPropertyID INT, @iYear INT, @xmlAmounts XML
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'That property was not found.'; RETURN; END
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

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalAsset
    @iUserID INT, @iPropertyID INT, @iAssetID INT = 0, @strKind VARCHAR(10), @strDescription NVARCHAR(200), @dtmStart DATE,
    @dblCost DECIMAL(19,4), @dblLifeYears DECIMAL(9,2) = NULL, @strMethod VARCHAR(2) = NULL, @dblRate DECIMAL(9,4) = NULL, @dtmDisposed DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'That property was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strKind NOT IN ('Plant', 'Building') BEGIN SELECT 'Choose a depreciating asset or capital works.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@dblCost, 0) <= 0 BEGIN SELECT 'Enter the cost.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strKind = 'Plant' AND (ISNULL(@dblLifeYears, 0) <= 0 OR ISNULL(@strMethod, '') NOT IN ('DV', 'PC'))
    BEGIN SELECT 'Enter the effective life and choose diminishing value or prime cost.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strKind = 'Building' AND ISNULL(@dblRate, 0) <= 0
    BEGIN SELECT 'Enter the capital works rate (2.5% or 4%).' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @dtmDisposed IS NOT NULL AND @dtmDisposed < @dtmStart
    BEGIN SELECT 'The disposal date is before the start date.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END

    IF ISNULL(@iAssetID, 0) = 0
    BEGIN
        INSERT INTO tblRentalAssets (iPropertyID, strKind, strDescription, dtmStart, dblCost, dblLifeYears, strMethod, dblRate, dtmDisposed)
        VALUES (@iPropertyID, @strKind, @strDescription, @dtmStart, @dblCost,
                CASE WHEN @strKind = 'Plant' THEN @dblLifeYears END, CASE WHEN @strKind = 'Plant' THEN @strMethod END,
                CASE WHEN @strKind = 'Building' THEN @dblRate END, @dtmDisposed);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblRentalAssets
    SET strKind = @strKind, strDescription = @strDescription, dtmStart = @dtmStart, dblCost = @dblCost,
        dblLifeYears = CASE WHEN @strKind = 'Plant' THEN @dblLifeYears END, strMethod = CASE WHEN @strKind = 'Plant' THEN @strMethod END,
        dblRate = CASE WHEN @strKind = 'Building' THEN @dblRate END, dtmDisposed = @dtmDisposed
    WHERE iAssetID = @iAssetID AND iPropertyID = @iPropertyID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'That asset was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iAssetID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalAsset @iUserID INT, @iAssetID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE A FROM tblRentalAssets A JOIN tblRentalProperties P ON P.iPropertyID = A.iPropertyID
    WHERE A.iAssetID = @iAssetID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'That asset was not found.'; RETURN; END
    SELECT 'Success';
END
GO
