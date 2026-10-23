-- Running a rental property (roadmap 34 to 40, 23 Oct 2026): tenancies, the rent ledger, bills passed on to the
-- tenant, the maintenance log, inspections and condition reports, compliance reminders, files kept on a property,
-- and email alerts for missed rent and reminders. Safe to run twice.
-- Run after 2026-10-23-01-rental-reports.sql, 2026-10-21-08-alerts.sql and 2026-10-21-28-expense-claims.sql.
-- No USE line: pick your database in SSMS first.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblRentalSales', 'U') IS NULL OR OBJECT_ID('dbo.tblAlertSettings', 'U') IS NULL OR OBJECT_ID('dbo.tblExpenseClaims', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-08-alerts.sql, 2026-10-21-28-expense-claims.sql and 2026-10-23-01-rental-reports.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- strFrequency Week, Fortnight or Month; the rent is due in advance from dtmStart
IF OBJECT_ID('dbo.tblRentalTenancies', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalTenancies (
        iTenancyID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalTenancies PRIMARY KEY,
        iPropertyID       INT NOT NULL,
        strTenants        NVARCHAR(300) NOT NULL,
        strEmail          NVARCHAR(200) NULL,
        strPhone          NVARCHAR(50) NULL,
        dtmStart          DATE NOT NULL,
        dtmLeaseEnd       DATE NULL,
        dblRent           DECIMAL(19,4) NOT NULL,
        strFrequency      VARCHAR(10) NOT NULL,
        dblBond           DECIMAL(19,4) NULL,
        strBondLodgement  NVARCHAR(100) NULL,
        dtmRentReview     DATE NULL,
        dtmVacated        DATE NULL,
        strNote           NVARCHAR(1000) NULL
    );
    CREATE INDEX IX_tblRentalTenancies_Property ON dbo.tblRentalTenancies (iPropertyID);
END
GO

IF OBJECT_ID('dbo.tblRentalRentPayments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalRentPayments (
        iPaymentID  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalRentPayments PRIMARY KEY,
        iTenancyID  INT NOT NULL,
        dtmPaid     DATE NOT NULL,
        dblAmount   DECIMAL(19,4) NOT NULL,
        strNote     NVARCHAR(300) NULL
    );
    CREATE INDEX IX_tblRentalRentPayments_Tenancy ON dbo.tblRentalRentPayments (iTenancyID);
END
GO

-- A cost passed on to the tenant (water usage and so on). strCategory is the rental expense it nets off.
IF OBJECT_ID('dbo.tblRentalTenantBills', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalTenantBills (
        iBillID         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalTenantBills PRIMARY KEY,
        iPropertyID     INT NOT NULL,
        iTenancyID      INT NULL,
        strCategory     VARCHAR(30) NOT NULL,
        strDescription  NVARCHAR(300) NOT NULL,
        dtmBilled       DATE NOT NULL,
        dblAmount       DECIMAL(19,4) NOT NULL,
        dtmPaid         DATE NULL
    );
    CREATE INDEX IX_tblRentalTenantBills_Property ON dbo.tblRentalTenantBills (iPropertyID);
END
GO

-- strTreatment Repair, Initial (an initial repair, capital) or Improvement (capital)
IF OBJECT_ID('dbo.tblRentalJobs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalJobs (
        iJobID           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalJobs PRIMARY KEY,
        iPropertyID      INT NOT NULL,
        dtmDone          DATE NOT NULL,
        strDescription   NVARCHAR(300) NOT NULL,
        strTradesperson  NVARCHAR(200) NULL,
        dblCost          DECIMAL(19,4) NOT NULL,
        strTreatment     VARCHAR(12) NOT NULL,
        strInvoiceRef    NVARCHAR(100) NULL
    );
    CREATE INDEX IX_tblRentalJobs_Property ON dbo.tblRentalJobs (iPropertyID);
END
GO

-- strKind Entry, Routine or Exit
IF OBJECT_ID('dbo.tblRentalInspections', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalInspections (
        iInspectionID  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalInspections PRIMARY KEY,
        iPropertyID    INT NOT NULL,
        iTenancyID     INT NULL,
        strKind        VARCHAR(10) NOT NULL,
        dtmDone        DATE NOT NULL,
        strNotes       NVARCHAR(4000) NULL
    );
    CREATE INDEX IX_tblRentalInspections_Property ON dbo.tblRentalInspections (iPropertyID);
END
GO

-- A recurring check or renewal: done last on dtmLastDone, due every iMonths, or on dtmDue when that is set
IF OBJECT_ID('dbo.tblRentalCompliance', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalCompliance (
        iItemID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRentalCompliance PRIMARY KEY,
        iPropertyID  INT NOT NULL,
        strKind      NVARCHAR(100) NOT NULL,
        dtmLastDone  DATE NULL,
        iMonths      INT NULL,
        dtmDue       DATE NULL,
        strNote      NVARCHAR(500) NULL
    );
    CREATE INDEX IX_tblRentalCompliance_Property ON dbo.tblRentalCompliance (iPropertyID);
END
GO

-- Property alerts already emailed (missed rent, reminders), so each is sent once
IF OBJECT_ID('dbo.tblRentalAlertsSent', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRentalAlertsSent (
        iPortfolioID  INT NOT NULL,
        strKey        VARCHAR(100) NOT NULL,
        dtmSent       DATETIME NOT NULL CONSTRAINT DF_tblRentalAlertsSent_Sent DEFAULT (GETUTCDATE()),
        CONSTRAINT PK_tblRentalAlertsSent PRIMARY KEY (iPortfolioID, strKey)
    );
END
GO

IF COL_LENGTH('dbo.tblAlertSettings', 'bProperty') IS NULL
    ALTER TABLE dbo.tblAlertSettings ADD bProperty BIT NOT NULL CONSTRAINT DF_tblAlertSettings_Property DEFAULT (1);
GO

-- 23-Oct-2026: also property alerts (missed rent and reminders)
CREATE OR ALTER PROCEDURE dbo.uspSaveAlertSettings
    @iLoginID      INT,
    @iPortfolioID  INT,
    @bPriceTargets BIT,
    @bDividends    BIT,
    @bSyncFailures BIT,
    @bUnallocated  BIT,
    @bProperty     BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblAlertSettings
       SET bPriceTargets = @bPriceTargets, bDividends = @bDividends, bSyncFailures = @bSyncFailures, bUnallocated = @bUnallocated,
           bProperty = ISNULL(@bProperty, 1)
     WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID;
    IF @@ROWCOUNT = 0
        INSERT dbo.tblAlertSettings (iLoginID, iPortfolioID, bPriceTargets, bDividends, bSyncFailures, bUnallocated, bProperty)
        VALUES (@iLoginID, @iPortfolioID, @bPriceTargets, @bDividends, @bSyncFailures, @bUnallocated, ISNULL(@bProperty, 1));
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspMarkRentalAlertSent @iPortfolioID INT, @strKey VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalAlertsSent WHERE iPortfolioID = @iPortfolioID AND strKey = @strKey)
        INSERT INTO tblRentalAlertsSent (iPortfolioID, strKey) VALUES (@iPortfolioID, @strKey);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalTenancy
    @iUserID INT, @iPropertyID INT, @iTenancyID INT = 0, @strTenants NVARCHAR(300), @strEmail NVARCHAR(200) = NULL, @strPhone NVARCHAR(50) = NULL,
    @dtmStart DATE, @dtmLeaseEnd DATE = NULL, @dblRent DECIMAL(19,4), @strFrequency VARCHAR(10), @dblBond DECIMAL(19,4) = NULL,
    @strBondLodgement NVARCHAR(100) = NULL, @dtmRentReview DATE = NULL, @dtmVacated DATE = NULL, @strNote NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF LTRIM(RTRIM(ISNULL(@strTenants, ''))) = '' OR @dtmStart IS NULL
    BEGIN SELECT 'Error: Enter the tenants'' names and the start date.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@dblRent, 0) <= 0 OR ISNULL(@strFrequency, '') NOT IN ('Week', 'Fortnight', 'Month')
    BEGIN SELECT 'Error: Enter the rent and how often it is due.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF (@dtmLeaseEnd IS NOT NULL AND @dtmLeaseEnd < @dtmStart) OR (@dtmVacated IS NOT NULL AND @dtmVacated < @dtmStart)
    BEGIN SELECT 'Error: The lease end and the date vacated can''t be before the start.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @dblBond < 0 BEGIN SELECT 'Error: The bond can''t be negative.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END

    IF ISNULL(@iTenancyID, 0) = 0
    BEGIN
        INSERT INTO tblRentalTenancies (iPropertyID, strTenants, strEmail, strPhone, dtmStart, dtmLeaseEnd, dblRent, strFrequency, dblBond,
            strBondLodgement, dtmRentReview, dtmVacated, strNote)
        VALUES (@iPropertyID, LTRIM(RTRIM(@strTenants)), NULLIF(LTRIM(RTRIM(@strEmail)), ''), NULLIF(LTRIM(RTRIM(@strPhone)), ''), @dtmStart,
            @dtmLeaseEnd, @dblRent, @strFrequency, @dblBond, NULLIF(LTRIM(RTRIM(@strBondLodgement)), ''), @dtmRentReview, @dtmVacated,
            NULLIF(LTRIM(RTRIM(@strNote)), ''));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblRentalTenancies
    SET strTenants = LTRIM(RTRIM(@strTenants)), strEmail = NULLIF(LTRIM(RTRIM(@strEmail)), ''), strPhone = NULLIF(LTRIM(RTRIM(@strPhone)), ''),
        dtmStart = @dtmStart, dtmLeaseEnd = @dtmLeaseEnd, dblRent = @dblRent, strFrequency = @strFrequency, dblBond = @dblBond,
        strBondLodgement = NULLIF(LTRIM(RTRIM(@strBondLodgement)), ''), dtmRentReview = @dtmRentReview, dtmVacated = @dtmVacated,
        strNote = NULLIF(LTRIM(RTRIM(@strNote)), '')
    WHERE iTenancyID = @iTenancyID AND iPropertyID = @iPropertyID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That tenancy was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iTenancyID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalTenancy @iUserID INT, @iTenancyID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalTenancies T JOIN tblRentalProperties P ON P.iPropertyID = T.iPropertyID
                   WHERE T.iTenancyID = @iTenancyID AND P.iUserID = @iUserID)
    BEGIN SELECT 'Error: That tenancy was not found.'; RETURN; END
    BEGIN TRAN;
    DELETE FROM tblRentalRentPayments WHERE iTenancyID = @iTenancyID;
    UPDATE tblRentalTenantBills SET iTenancyID = NULL WHERE iTenancyID = @iTenancyID;
    UPDATE tblRentalInspections SET iTenancyID = NULL WHERE iTenancyID = @iTenancyID;
    DELETE FROM tblRentalTenancies WHERE iTenancyID = @iTenancyID;
    COMMIT;
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspAddRentalRentPayment @iUserID INT, @iTenancyID INT, @dtmPaid DATE, @dblAmount DECIMAL(19,4), @strNote NVARCHAR(300) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalTenancies T JOIN tblRentalProperties P ON P.iPropertyID = T.iPropertyID
                   WHERE T.iTenancyID = @iTenancyID AND P.iUserID = @iUserID)
    BEGIN SELECT 'Error: That tenancy was not found.'; RETURN; END
    IF @dtmPaid IS NULL OR ISNULL(@dblAmount, 0) = 0 BEGIN SELECT 'Error: Enter the date and the amount paid.'; RETURN; END
    INSERT INTO tblRentalRentPayments (iTenancyID, dtmPaid, dblAmount, strNote) VALUES (@iTenancyID, @dtmPaid, @dblAmount, NULLIF(LTRIM(RTRIM(@strNote)), ''));
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalRentPayment @iUserID INT, @iPaymentID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE R FROM tblRentalRentPayments R JOIN tblRentalTenancies T ON T.iTenancyID = R.iTenancyID JOIN tblRentalProperties P ON P.iPropertyID = T.iPropertyID
    WHERE R.iPaymentID = @iPaymentID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That payment was not found.'; RETURN; END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalTenantBill
    @iUserID INT, @iPropertyID INT, @iBillID INT = 0, @iTenancyID INT = NULL, @strCategory VARCHAR(30), @strDescription NVARCHAR(300),
    @dtmBilled DATE, @dblAmount DECIMAL(19,4), @dtmPaid DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    IF @iTenancyID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblRentalTenancies WHERE iTenancyID = @iTenancyID AND iPropertyID = @iPropertyID)
    BEGIN SELECT 'Error: That tenancy was not found.'; RETURN; END
    IF LTRIM(RTRIM(ISNULL(@strDescription, ''))) = '' OR @dtmBilled IS NULL OR ISNULL(@dblAmount, 0) <= 0 OR LTRIM(RTRIM(ISNULL(@strCategory, ''))) = ''
    BEGIN SELECT 'Error: Enter what the bill is for, the date and the amount.'; RETURN; END
    IF @dtmPaid IS NOT NULL AND @dtmPaid < @dtmBilled BEGIN SELECT 'Error: The date paid is before the date billed.'; RETURN; END
    IF ISNULL(@iBillID, 0) = 0
        INSERT INTO tblRentalTenantBills (iPropertyID, iTenancyID, strCategory, strDescription, dtmBilled, dblAmount, dtmPaid)
        VALUES (@iPropertyID, @iTenancyID, @strCategory, LTRIM(RTRIM(@strDescription)), @dtmBilled, @dblAmount, @dtmPaid);
    ELSE
    BEGIN
        UPDATE tblRentalTenantBills SET iTenancyID = @iTenancyID, strCategory = @strCategory, strDescription = LTRIM(RTRIM(@strDescription)),
            dtmBilled = @dtmBilled, dblAmount = @dblAmount, dtmPaid = @dtmPaid
        WHERE iBillID = @iBillID AND iPropertyID = @iPropertyID;
        IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That bill was not found.'; RETURN; END
    END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalTenantBill @iUserID INT, @iBillID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE B FROM tblRentalTenantBills B JOIN tblRentalProperties P ON P.iPropertyID = B.iPropertyID WHERE B.iBillID = @iBillID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That bill was not found.'; RETURN; END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalJob
    @iUserID INT, @iPropertyID INT, @iJobID INT = 0, @dtmDone DATE, @strDescription NVARCHAR(300), @strTradesperson NVARCHAR(200) = NULL,
    @dblCost DECIMAL(19,4), @strTreatment VARCHAR(12), @strInvoiceRef NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @dtmDone IS NULL OR LTRIM(RTRIM(ISNULL(@strDescription, ''))) = '' OR ISNULL(@dblCost, -1) < 0
    BEGIN SELECT 'Error: Enter the date, what was done and the cost.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@strTreatment, '') NOT IN ('Repair', 'Initial', 'Improvement')
    BEGIN SELECT 'Error: Choose a repair, an initial repair or an improvement.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@iJobID, 0) = 0
    BEGIN
        INSERT INTO tblRentalJobs (iPropertyID, dtmDone, strDescription, strTradesperson, dblCost, strTreatment, strInvoiceRef)
        VALUES (@iPropertyID, @dtmDone, LTRIM(RTRIM(@strDescription)), NULLIF(LTRIM(RTRIM(@strTradesperson)), ''), @dblCost, @strTreatment,
            NULLIF(LTRIM(RTRIM(@strInvoiceRef)), ''));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblRentalJobs SET dtmDone = @dtmDone, strDescription = LTRIM(RTRIM(@strDescription)), strTradesperson = NULLIF(LTRIM(RTRIM(@strTradesperson)), ''),
        dblCost = @dblCost, strTreatment = @strTreatment, strInvoiceRef = NULLIF(LTRIM(RTRIM(@strInvoiceRef)), '')
    WHERE iJobID = @iJobID AND iPropertyID = @iPropertyID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That job was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iJobID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalJob @iUserID INT, @iJobID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE J FROM tblRentalJobs J JOIN tblRentalProperties P ON P.iPropertyID = J.iPropertyID WHERE J.iJobID = @iJobID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That job was not found.'; RETURN; END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalInspection
    @iUserID INT, @iPropertyID INT, @iInspectionID INT = 0, @iTenancyID INT = NULL, @strKind VARCHAR(10), @dtmDone DATE, @strNotes NVARCHAR(4000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @iTenancyID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblRentalTenancies WHERE iTenancyID = @iTenancyID AND iPropertyID = @iPropertyID)
    BEGIN SELECT 'Error: That tenancy was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@strKind, '') NOT IN ('Entry', 'Routine', 'Exit') OR @dtmDone IS NULL
    BEGIN SELECT 'Error: Choose the kind of inspection and enter the date.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@iInspectionID, 0) = 0
    BEGIN
        INSERT INTO tblRentalInspections (iPropertyID, iTenancyID, strKind, dtmDone, strNotes)
        VALUES (@iPropertyID, @iTenancyID, @strKind, @dtmDone, NULLIF(LTRIM(RTRIM(@strNotes)), ''));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblRentalInspections SET iTenancyID = @iTenancyID, strKind = @strKind, dtmDone = @dtmDone, strNotes = NULLIF(LTRIM(RTRIM(@strNotes)), '')
    WHERE iInspectionID = @iInspectionID AND iPropertyID = @iPropertyID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That inspection was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iInspectionID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalInspection @iUserID INT, @iInspectionID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE I FROM tblRentalInspections I JOIN tblRentalProperties P ON P.iPropertyID = I.iPropertyID WHERE I.iInspectionID = @iInspectionID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That inspection was not found.'; RETURN; END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveRentalCompliance
    @iUserID INT, @iPropertyID INT, @iItemID INT = 0, @strKind NVARCHAR(100), @dtmLastDone DATE = NULL, @iMonths INT = NULL, @dtmDue DATE = NULL,
    @strNote NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblRentalProperties WHERE iPropertyID = @iPropertyID AND iUserID = @iUserID)
    BEGIN SELECT 'Error: That property was not found.'; RETURN; END
    IF LTRIM(RTRIM(ISNULL(@strKind, ''))) = '' BEGIN SELECT 'Error: Enter what needs checking or renewing.'; RETURN; END
    IF @iMonths IS NOT NULL AND (@iMonths < 1 OR @iMonths > 120) BEGIN SELECT 'Error: Enter how often, from 1 to 120 months.'; RETURN; END
    IF ISNULL(@iItemID, 0) = 0
        INSERT INTO tblRentalCompliance (iPropertyID, strKind, dtmLastDone, iMonths, dtmDue, strNote)
        VALUES (@iPropertyID, LTRIM(RTRIM(@strKind)), @dtmLastDone, @iMonths, @dtmDue, NULLIF(LTRIM(RTRIM(@strNote)), ''));
    ELSE
    BEGIN
        UPDATE tblRentalCompliance SET strKind = LTRIM(RTRIM(@strKind)), dtmLastDone = @dtmLastDone, iMonths = @iMonths, dtmDue = @dtmDue,
            strNote = NULLIF(LTRIM(RTRIM(@strNote)), '')
        WHERE iItemID = @iItemID AND iPropertyID = @iPropertyID;
        IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That reminder was not found.'; RETURN; END
    END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteRentalCompliance @iUserID INT, @iItemID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE C FROM tblRentalCompliance C JOIN tblRentalProperties P ON P.iPropertyID = C.iPropertyID WHERE C.iItemID = @iItemID AND P.iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'Error: That reminder was not found.'; RETURN; END
    SELECT 'Success';
END
GO

-- 23-Oct-2026: also files on a rental property (Property), a maintenance job (RentalJob) and an inspection (Inspection)
CREATE OR ALTER PROCEDURE dbo.uspAddAttachment
    @iUserID           INT,
    @strKind           VARCHAR(20),
    @iTargetID         INT = NULL,
    @iYear             INT = NULL,
    @strFileName       NVARCHAR(260),
    @strContentType    VARCHAR(100),
    @binData           VARBINARY(MAX),
    @strNote           NVARCHAR(500) = NULL,
    @iUploadedByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strKind NOT IN ('Trade', 'Journal', 'Client', 'Year', 'Workpaper', 'Query', 'Bill', 'ExpenseClaim', 'Property', 'RentalJob', 'Inspection')
        OR LTRIM(RTRIM(ISNULL(@strFileName, ''))) = ''
        OR @binData IS NULL OR DATALENGTH(@binData) = 0 OR DATALENGTH(@binData) > 10485760
    BEGIN
        SELECT 'Error: Choose a file of up to 10 MB.' AS Result, NULL AS Id;
        RETURN;
    END
    -- The record it's attached to must be in the same portfolio
    IF (@strKind = 'Journal' AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal WHERE iJournalID = @iTargetID AND iUserID = @iUserID))
        OR (@strKind = 'Trade' AND NOT EXISTS (SELECT 1 FROM dbo.tblShareTrades WHERE iShareTradeId = @iTargetID AND iUserId = @iUserID))
        OR (@strKind = 'Bill' AND NOT EXISTS (SELECT 1 FROM dbo.tblBills WHERE iBillID = @iTargetID AND iUserID = @iUserID))
        OR (@strKind = 'ExpenseClaim' AND NOT EXISTS (SELECT 1 FROM dbo.tblExpenseClaims WHERE iClaimID = @iTargetID AND iUserID = @iUserID))
        OR (@strKind = 'Property' AND NOT EXISTS (SELECT 1 FROM dbo.tblRentalProperties WHERE iPropertyID = @iTargetID AND iUserID = @iUserID))
        OR (@strKind = 'RentalJob' AND NOT EXISTS (SELECT 1 FROM dbo.tblRentalJobs J JOIN dbo.tblRentalProperties P ON P.iPropertyID = J.iPropertyID
                                                    WHERE J.iJobID = @iTargetID AND P.iUserID = @iUserID))
        OR (@strKind = 'Inspection' AND NOT EXISTS (SELECT 1 FROM dbo.tblRentalInspections I JOIN dbo.tblRentalProperties P ON P.iPropertyID = I.iPropertyID
                                                     WHERE I.iInspectionID = @iTargetID AND P.iUserID = @iUserID))
    BEGIN
        SELECT 'Error: That record isn''t in this portfolio.' AS Result, NULL AS Id;
        RETURN;
    END
    INSERT dbo.tblAttachments (iUserID, strKind, iTargetID, iYear, strFileName, strContentType, iSize, binData, strNote, iUploadedByUserID)
    VALUES (@iUserID, @strKind, @iTargetID, @iYear, LTRIM(RTRIM(@strFileName)), @strContentType, DATALENGTH(@binData), @binData,
        NULLIF(LTRIM(RTRIM(@strNote)), ''), @iUploadedByUserID);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

SET NOEXEC OFF;
GO
