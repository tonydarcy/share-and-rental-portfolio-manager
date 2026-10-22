-- =============================================
-- Migration: fixed asset register
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Business > Fixed assets.
-- Run it after 2026-10-21-28-expense-claims.sql. Safe to run more than once. It only adds things.
--
-- The site works out each asset's depreciation (the same engine as the rental property schedule: diminishing value,
-- prime cost, low-value pool, small business pool or an instant write-off) and posts it here:
--   * A depreciation run posts one journal (source 'Depreciation') on its date: Dr each asset's depreciation expense
--     account, Cr its accumulated depreciation account, with the amount for each asset kept in tblDepreciationEntries.
--   * A disposal posts the depreciation up to the sale date and the sale (source 'AssetDisposal'): Dr accumulated
--     depreciation (all of it, including any brought in), Dr the account the proceeds went to, Cr the asset's cost,
--     and the difference to 4910 Gain or Loss on Disposal of Assets.
--
-- What it adds:
--   * System accounts 1500 Plant and Equipment, 1590 Accumulated Depreciation (assets), 5750 Depreciation (expense)
--     and 4910 Gain or Loss on Disposal of Assets (revenue).
--   * tblFixedAssets, tblDepreciationRuns, tblDepreciationEntries.
--   * uspSaveFixedAsset, uspDeleteFixedAsset, uspPostDepreciationRun, uspDeleteDepreciationRun, uspDisposeFixedAsset,
--     uspUndoAssetDisposal.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblExpenseClaims', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-28-expense-claims.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 1500, 'Plant and Equipment', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 1590, 'Accumulated Depreciation', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 5750, 'Depreciation', 'Expense', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4910, 'Gain or Loss on Disposal of Assets', 'Revenue', @iAccountID OUTPUT;
GO

IF OBJECT_ID('dbo.tblFixedAssets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFixedAssets (
        iAssetID               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblFixedAssets PRIMARY KEY,
        iUserID                INT NOT NULL,
        strNumber              VARCHAR(30) NULL,
        strName                NVARCHAR(200) NOT NULL,
        strDescription         NVARCHAR(500) NULL,
        dtmPurchaseDate        DATE NOT NULL,
        dtmStartDate           DATE NOT NULL,              -- first used or installed ready for use: depreciation starts here
        dblCost                DECIMAL(19,4) NOT NULL,
        strMethod              VARCHAR(10) NOT NULL,       -- DV, PC, LowValue, SbPool, WriteOff or None
        dblLifeYears           DECIMAL(9,4) NULL,          -- effective life, for DV and PC
        dtmOpeningDate         DATE NULL,                  -- brought in part way through its life: the date ...
        dblOpeningAccum        DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblFixedAssets_Opening DEFAULT (0),   -- ... and the depreciation to then
        iAssetAccountID        INT NOT NULL,
        iAccumAccountID        INT NOT NULL,
        iExpenseAccountID      INT NOT NULL,
        dtmDisposed            DATE NULL,
        dblProceeds            DECIMAL(19,4) NULL,
        iProceedsAccountID     INT NULL,
        dblDisposalDepreciation DECIMAL(19,4) NULL,       -- the depreciation from the last run to the sale
        iDisposalJournalID     INT NULL,
        dtmCreatedDate         DATETIME NOT NULL CONSTRAINT DF_tblFixedAssets_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblFixedAssets_User ON dbo.tblFixedAssets (iUserID);
END
GO

IF OBJECT_ID('dbo.tblDepreciationRuns', 'U') IS NULL
    CREATE TABLE dbo.tblDepreciationRuns (
        iRunID         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblDepreciationRuns PRIMARY KEY,
        iUserID        INT NOT NULL,
        dtmTo          DATE NOT NULL,
        iJournalID     INT NULL,
        dtmCreatedDate DATETIME NOT NULL CONSTRAINT DF_tblDepreciationRuns_Created DEFAULT (GETDATE())
    );
GO

IF OBJECT_ID('dbo.tblDepreciationEntries', 'U') IS NULL
    CREATE TABLE dbo.tblDepreciationEntries (
        iRunID    INT NOT NULL CONSTRAINT FK_tblDepreciationEntries_Run REFERENCES dbo.tblDepreciationRuns (iRunID) ON DELETE CASCADE,
        iAssetID  INT NOT NULL CONSTRAINT FK_tblDepreciationEntries_Asset REFERENCES dbo.tblFixedAssets (iAssetID),
        dblAmount DECIMAL(19,4) NOT NULL,
        CONSTRAINT PK_tblDepreciationEntries PRIMARY KEY (iRunID, iAssetID)
    );
GO

-- Adds (@iAssetID 0) or changes an asset. Once depreciation has been posted for it, only its number, name and
-- description can change. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveFixedAsset
    @iUserId           INT,
    @iAssetID          INT = 0,
    @strNumber         VARCHAR(30) = NULL,
    @strName           NVARCHAR(200),
    @strDescription    NVARCHAR(500) = NULL,
    @dtPurchaseDate    DATE,
    @dtStartDate       DATE,
    @dblCost           DECIMAL(19,4),
    @strMethod         VARCHAR(10),
    @dblLifeYears      DECIMAL(9,4) = NULL,
    @dtOpeningDate     DATE = NULL,
    @dblOpeningAccum   DECIMAL(19,4) = 0,
    @iAssetAccountID   INT,
    @iAccumAccountID   INT,
    @iExpenseAccountID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    SET @dblOpeningAccum = ISNULL(@dblOpeningAccum, 0);
    IF @dblOpeningAccum = 0
        SET @dtOpeningDate = NULL;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @strName = '' THEN 'Enter the asset''s name.'
        WHEN @dtPurchaseDate IS NULL OR @dtStartDate IS NULL OR @dtStartDate < @dtPurchaseDate THEN 'Check the dates: it can''t be used before it was bought.'
        WHEN ISNULL(@dblCost, 0) <= 0 THEN 'Enter the cost.'
        WHEN ISNULL(@strMethod, '') NOT IN ('DV', 'PC', 'LowValue', 'SbPool', 'WriteOff', 'None') THEN 'Choose the depreciation method.'
        WHEN @strMethod IN ('DV', 'PC') AND ISNULL(@dblLifeYears, 0) <= 0 THEN 'Enter the effective life in years.'
        WHEN @dblOpeningAccum < 0 OR @dblOpeningAccum > @dblCost THEN 'The depreciation brought in can''t be more than the cost.'
        WHEN @dblOpeningAccum > 0 AND (@dtOpeningDate IS NULL OR @dtOpeningDate < @dtStartDate) THEN 'Enter the date the depreciation brought in goes up to.'
        WHEN NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAssetAccountID AND (iUserID = @iUserId OR iUserID = 0) AND strAccountType = 'Asset')
          OR NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccumAccountID AND (iUserID = @iUserId OR iUserID = 0) AND strAccountType = 'Asset')
          OR NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iExpenseAccountID AND (iUserID = @iUserId OR iUserID = 0) AND strAccountType = 'Expense')
            THEN 'Choose the asset, accumulated depreciation and depreciation expense accounts.'
        WHEN ISNULL(@iAssetID, 0) > 0 AND NOT EXISTS (SELECT 1 FROM tblFixedAssets WHERE iAssetID = @iAssetID AND iUserID = @iUserId)
            THEN 'That asset isn''t in this portfolio.'
        WHEN ISNULL(@iAssetID, 0) > 0 AND (EXISTS (SELECT 1 FROM tblDepreciationEntries WHERE iAssetID = @iAssetID)
                                           OR EXISTS (SELECT 1 FROM tblFixedAssets WHERE iAssetID = @iAssetID AND dtmDisposed IS NOT NULL))
             AND EXISTS (SELECT 1 FROM tblFixedAssets WHERE iAssetID = @iAssetID AND (dtmPurchaseDate <> @dtPurchaseDate OR dtmStartDate <> @dtStartDate
                         OR dblCost <> @dblCost OR strMethod <> @strMethod OR ISNULL(dblLifeYears, 0) <> ISNULL(@dblLifeYears, 0)
                         OR ISNULL(dtmOpeningDate, '19000101') <> ISNULL(@dtOpeningDate, '19000101') OR dblOpeningAccum <> @dblOpeningAccum
                         OR iAssetAccountID <> @iAssetAccountID OR iAccumAccountID <> @iAccumAccountID OR iExpenseAccountID <> @iExpenseAccountID))
            THEN 'Depreciation has been posted for this asset, so only its number, name and description can change. Delete the later depreciation runs first.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END

    IF ISNULL(@iAssetID, 0) = 0
    BEGIN
        INSERT tblFixedAssets (iUserID, strNumber, strName, strDescription, dtmPurchaseDate, dtmStartDate, dblCost, strMethod, dblLifeYears,
            dtmOpeningDate, dblOpeningAccum, iAssetAccountID, iAccumAccountID, iExpenseAccountID)
        VALUES (@iUserId, NULLIF(LTRIM(RTRIM(@strNumber)), ''), @strName, NULLIF(LTRIM(RTRIM(@strDescription)), ''), @dtPurchaseDate, @dtStartDate,
            @dblCost, @strMethod, CASE WHEN @strMethod IN ('DV', 'PC') THEN @dblLifeYears END, @dtOpeningDate, @dblOpeningAccum,
            @iAssetAccountID, @iAccumAccountID, @iExpenseAccountID);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblFixedAssets
    SET strNumber = NULLIF(LTRIM(RTRIM(@strNumber)), ''), strName = @strName, strDescription = NULLIF(LTRIM(RTRIM(@strDescription)), ''),
        dtmPurchaseDate = @dtPurchaseDate, dtmStartDate = @dtStartDate, dblCost = @dblCost, strMethod = @strMethod,
        dblLifeYears = CASE WHEN @strMethod IN ('DV', 'PC') THEN @dblLifeYears END, dtmOpeningDate = @dtOpeningDate, dblOpeningAccum = @dblOpeningAccum,
        iAssetAccountID = @iAssetAccountID, iAccumAccountID = @iAccumAccountID, iExpenseAccountID = @iExpenseAccountID
    WHERE iAssetID = @iAssetID AND iUserID = @iUserId;
    SELECT 'Success' AS Result, @iAssetID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteFixedAsset
    @iUserId  INT,
    @iAssetID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM tblDepreciationEntries WHERE iAssetID = @iAssetID)
       OR EXISTS (SELECT 1 FROM tblFixedAssets WHERE iAssetID = @iAssetID AND dtmDisposed IS NOT NULL)
    BEGIN
        SELECT 'Error: Depreciation has been posted for this asset, so it can''t be deleted.' AS Result;
        RETURN;
    END
    DELETE FROM tblFixedAssets WHERE iAssetID = @iAssetID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That asset isn''t in this portfolio.' END AS Result;
END
GO

-- Posts depreciation worked out by the site up to @dtTo: <assets><a id="asset" v="amount"/></assets>. Runs go forward
-- in date order: @dtTo must be after the last run. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspPostDepreciationRun
    @iUserId   INT,
    @dtTo      DATE,
    @xmlAssets XML
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Amounts TABLE (iAssetID INT PRIMARY KEY, dblAmount DECIMAL(19,4));
    INSERT @Amounts SELECT X.a.value('@id', 'INT'), X.a.value('@v', 'DECIMAL(19,4)') FROM @xmlAssets.nodes('/assets/a') AS X(a);
    DELETE FROM @Amounts WHERE ISNULL(dblAmount, 0) = 0;
    DECLARE @dtLast DATE = (SELECT MAX(dtmTo) FROM tblDepreciationRuns WHERE iUserID = @iUserId);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @dtTo IS NULL THEN 'Enter the date to depreciate to.'
        WHEN @dtLast IS NOT NULL AND @dtTo <= @dtLast THEN 'Depreciation has already been posted to ' + CONVERT(VARCHAR(10), @dtLast, 103) + '. Choose a later date.'
        WHEN NOT EXISTS (SELECT 1 FROM @Amounts) THEN 'There is no depreciation to post to that date.'
        WHEN EXISTS (SELECT 1 FROM @Amounts WHERE dblAmount < 0) THEN 'Depreciation can''t be negative.'
        WHEN EXISTS (SELECT 1 FROM @Amounts X WHERE NOT EXISTS (SELECT 1 FROM tblFixedAssets A WHERE A.iAssetID = X.iAssetID AND A.iUserID = @iUserId
                                                                AND (A.dtmDisposed IS NULL OR A.dtmDisposed > @dtTo)))
            THEN 'An asset wasn''t found in this portfolio, or has been sold.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END

    DECLARE @strDescription VARCHAR(500) = 'Depreciation to ' + CONVERT(VARCHAR(10), @dtTo, 103);
    BEGIN TRANSACTION;
    INSERT tblDepreciationRuns (iUserID, dtmTo) VALUES (@iUserId, @dtTo);
    DECLARE @iRunID INT = SCOPE_IDENTITY();
    INSERT tblDepreciationEntries (iRunID, iAssetID, dblAmount) SELECT @iRunID, iAssetID, dblAmount FROM @Amounts;

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtTo, @strDescription, 'Depreciation', @iRunID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    SELECT @iJournalID, A.iExpenseAccountID, SUM(X.dblAmount), 0, @strDescription
    FROM @Amounts X JOIN tblFixedAssets A ON A.iAssetID = X.iAssetID GROUP BY A.iExpenseAccountID
    UNION ALL
    SELECT @iJournalID, A.iAccumAccountID, 0, SUM(X.dblAmount), @strDescription
    FROM @Amounts X JOIN tblFixedAssets A ON A.iAssetID = X.iAssetID GROUP BY A.iAccumAccountID;
    UPDATE tblDepreciationRuns SET iJournalID = @iJournalID WHERE iRunID = @iRunID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iRunID AS Id;
END
GO

-- Deletes the latest run (and its journal), so it can be run again. Refused if an asset in it has since been sold.
CREATE OR ALTER PROCEDURE dbo.uspDeleteDepreciationRun
    @iUserId INT,
    @iRunID  INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @dtTo DATE, @iJournalID INT;
    SELECT @dtTo = dtmTo, @iJournalID = iJournalID FROM tblDepreciationRuns WHERE iRunID = @iRunID AND iUserID = @iUserId;
    IF @dtTo IS NULL
    BEGIN
        SELECT 'Error: That depreciation run wasn''t found.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblDepreciationRuns WHERE iUserID = @iUserId AND dtmTo > @dtTo)
    BEGIN
        SELECT 'Error: Only the latest run can be deleted. Delete the later ones first.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblDepreciationEntries E JOIN tblFixedAssets A ON A.iAssetID = E.iAssetID WHERE E.iRunID = @iRunID AND A.dtmDisposed IS NOT NULL)
    BEGIN
        SELECT 'Error: An asset in this run has since been sold. Undo the sale first.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    DELETE FROM tblDepreciationRuns WHERE iRunID = @iRunID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

-- Sells or scraps an asset on @dtDate: posts @dblFinalDepreciation (worked out by the site from the last run to the
-- sale), then removes the cost and all its depreciation, with the proceeds and the gain or loss. Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspDisposeFixedAsset
    @iUserId              INT,
    @iAssetID             INT,
    @dtDate               DATE,
    @dblProceeds          DECIMAL(19,4),
    @iProceedsAccountID   INT = NULL,
    @dblFinalDepreciation DECIMAL(19,4) = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @dblProceeds = ISNULL(@dblProceeds, 0);
    SET @dblFinalDepreciation = ISNULL(@dblFinalDepreciation, 0);

    DECLARE @strName NVARCHAR(200), @dblCost DECIMAL(19,4), @dblOpening DECIMAL(19,4), @dtStart DATE, @dtDisposed DATE, @bFound BIT = 0,
            @iAssetAccountID INT, @iAccumAccountID INT, @iExpenseAccountID INT;
    SELECT @strName = strName, @dblCost = dblCost, @dblOpening = dblOpeningAccum, @dtStart = dtmPurchaseDate, @dtDisposed = dtmDisposed, @bFound = 1,
           @iAssetAccountID = iAssetAccountID, @iAccumAccountID = iAccumAccountID, @iExpenseAccountID = iExpenseAccountID
    FROM tblFixedAssets WHERE iAssetID = @iAssetID AND iUserID = @iUserId;
    DECLARE @dblPosted DECIMAL(19,4) = ISNULL((SELECT SUM(dblAmount) FROM tblDepreciationEntries WHERE iAssetID = @iAssetID), 0);
    DECLARE @dtLastRun DATE = (SELECT MAX(R.dtmTo) FROM tblDepreciationRuns R JOIN tblDepreciationEntries E ON E.iRunID = R.iRunID WHERE E.iAssetID = @iAssetID);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @bFound = 0 THEN 'That asset isn''t in this portfolio.'
        WHEN @dtDisposed IS NOT NULL THEN 'This asset has already been sold or scrapped.'
        WHEN @dtDate IS NULL OR @dtDate < @dtStart THEN 'Enter the date it was sold or scrapped.'
        WHEN @dtLastRun IS NOT NULL AND @dtDate <= @dtLastRun THEN 'Depreciation has been posted past that date. Delete the later runs first.'
        WHEN @dblProceeds < 0 OR @dblFinalDepreciation < 0 THEN 'The amounts can''t be negative.'
        WHEN @dblOpening + @dblPosted + @dblFinalDepreciation > @dblCost THEN 'The depreciation would be more than the cost.'
        WHEN @dblProceeds > 0 AND NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iProceedsAccountID AND (iUserID = @iUserId OR iUserID = 0) AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the account the money went to.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result;
        RETURN;
    END

    DECLARE @iGainID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 4910, 'Gain or Loss on Disposal of Assets', 'Revenue', @iGainID OUTPUT;
    DECLARE @dblAccum DECIMAL(19,4) = @dblOpening + @dblPosted + @dblFinalDepreciation;
    DECLARE @dblGain DECIMAL(19,4) = @dblProceeds - (@dblCost - @dblAccum);
    DECLARE @strDescription VARCHAR(500) = LEFT(CAST('Sale of ' + @strName AS VARCHAR(600)), 500);

    BEGIN TRANSACTION;
    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'AssetDisposal', @iAssetID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    IF @dblFinalDepreciation > 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iExpenseAccountID, @dblFinalDepreciation, 0, LEFT('Depreciation to sale: ' + @strDescription, 500)),
               (@iJournalID, @iAccumAccountID, 0, @dblFinalDepreciation, LEFT('Depreciation to sale: ' + @strDescription, 500));
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iAccumAccountID, @dblAccum, 0, @strDescription),
           (@iJournalID, @iAssetAccountID, 0, @dblCost, @strDescription);
    IF @dblProceeds > 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iProceedsAccountID, @dblProceeds, 0, @strDescription);
    IF @dblGain <> 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iGainID, CASE WHEN @dblGain < 0 THEN -@dblGain ELSE 0 END, CASE WHEN @dblGain > 0 THEN @dblGain ELSE 0 END, @strDescription);
    DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID AND dblDebit = 0 AND dblCredit = 0;

    UPDATE tblFixedAssets SET dtmDisposed = @dtDate, dblProceeds = @dblProceeds, iProceedsAccountID = NULLIF(@iProceedsAccountID, 0),
        dblDisposalDepreciation = @dblFinalDepreciation, iDisposalJournalID = @iJournalID
    WHERE iAssetID = @iAssetID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspUndoAssetDisposal
    @iUserId  INT,
    @iAssetID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @iJournalID INT, @dtDisposed DATE;
    SELECT @iJournalID = iDisposalJournalID, @dtDisposed = dtmDisposed FROM tblFixedAssets WHERE iAssetID = @iAssetID AND iUserID = @iUserId;
    IF @dtDisposed IS NULL
    BEGIN
        SELECT 'Error: That asset hasn''t been sold.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    UPDATE tblFixedAssets SET dtmDisposed = NULL, dblProceeds = NULL, iProceedsAccountID = NULL, dblDisposalDepreciation = NULL, iDisposalJournalID = NULL
    WHERE iAssetID = @iAssetID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Fixed asset register migration done.';
GO
