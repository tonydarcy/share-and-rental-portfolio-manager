-- =============================================
-- Migration: expense claims
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Business > Expense claims.
-- Run it after 2026-10-21-27-quotes-orders.sql and 2026-10-21-15-attachments-audit.sql. Safe to run more than once.
--
-- A claim is one receipt someone paid for themselves. Approving it posts a journal (source 'ExpenseClaim'): Dr the
-- expense (with any GST split to the tax control account), Cr 2150 Expense Claims Payable. Reimbursing one or more
-- approved claims posts Dr Expense Claims Payable, Cr the bank (source 'ExpenseClaimPayment').
--
-- What it adds:
--   * System account 2150 Expense Claims Payable (liability).
--   * tblExpenseClaims.
--   * uspAddAttachment also takes the kinds 'Bill' and 'ExpenseClaim' (the receipt photo), checked against the portfolio.
--   * uspSaveExpenseClaim, uspApproveExpenseClaim, uspRejectExpenseClaim, uspUnapproveExpenseClaim, uspPayExpenseClaims,
--     uspUndoExpenseClaimPayment, uspDeleteExpenseClaim.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblAttachments', 'U') IS NULL OR OBJECT_ID('dbo.tblQuotesOrders', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-15-attachments-audit.sql and 2026-10-21-27-quotes-orders.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 2150, 'Expense Claims Payable', 'Liability', @iAccountID OUTPUT;
GO

IF OBJECT_ID('dbo.tblExpenseClaims', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblExpenseClaims (
        iClaimID            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblExpenseClaims PRIMARY KEY,
        iUserID             INT NOT NULL,               -- the portfolio
        iClaimantUserID     INT NULL,                   -- the login who claimed it
        strClaimant         NVARCHAR(200) NOT NULL,     -- who is paid back
        dtmDate             DATE NOT NULL,              -- the receipt's date
        strPayee            NVARCHAR(200) NULL,         -- where it was bought
        strDescription      NVARCHAR(500) NOT NULL,
        iAccountID          INT NOT NULL,
        iTaxCodeID          INT NULL,
        dblAmount           DECIMAL(19,4) NOT NULL,     -- including tax
        dblTax              DECIMAL(19,4) NOT NULL CONSTRAINT DF_tblExpenseClaims_Tax DEFAULT (0),
        iTrackingOption1ID  INT NULL,
        iTrackingOption2ID  INT NULL,
        strStatus           VARCHAR(10) NOT NULL,       -- Submitted, Approved, Rejected or Paid
        strRejectReason     NVARCHAR(500) NULL,
        iApprovedByUserID   INT NULL,
        dtmApproved         DATETIME NULL,
        iJournalID          INT NULL,
        iPaymentJournalID   INT NULL,
        dtmPaid             DATE NULL,
        dtmSubmitted        DATETIME NOT NULL CONSTRAINT DF_tblExpenseClaims_Submitted DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblExpenseClaims_User ON dbo.tblExpenseClaims (iUserID, strStatus);
END
GO

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
-- 21-Oct-2026: also files on a bill and the receipt on an expense claim.
BEGIN
    SET NOCOUNT ON;
    IF @strKind NOT IN ('Trade', 'Journal', 'Client', 'Year', 'Workpaper', 'Query', 'Bill', 'ExpenseClaim') OR LTRIM(RTRIM(ISNULL(@strFileName, ''))) = ''
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

-- Adds (@iClaimID 0) or changes a claim. Only a submitted or rejected claim can be changed; saving a rejected one
-- submits it again. The amount includes tax. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveExpenseClaim
    @iUserId          INT,
    @iClaimID         INT = 0,
    @iClaimantUserID  INT = NULL,
    @strClaimant      NVARCHAR(200),
    @dtDate           DATE,
    @strPayee         NVARCHAR(200) = NULL,
    @strDescription   NVARCHAR(500),
    @iAccountID       INT,
    @iTaxCodeID       INT = NULL,
    @dblAmount        DECIMAL(19,4),
    @dblTax           DECIMAL(19,4) = 0,
    @iTracking1ID     INT = NULL,
    @iTracking2ID     INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @strClaimant = LTRIM(RTRIM(ISNULL(@strClaimant, '')));
    SET @strDescription = LTRIM(RTRIM(ISNULL(@strDescription, '')));
    SET @iTaxCodeID = NULLIF(@iTaxCodeID, 0);
    SET @iTracking1ID = NULLIF(@iTracking1ID, 0);
    SET @iTracking2ID = NULLIF(@iTracking2ID, 0);
    SET @dblTax = CASE WHEN @iTaxCodeID IS NULL THEN 0 ELSE ISNULL(@dblTax, 0) END;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @strClaimant = '' THEN 'Enter who is claiming.'
        WHEN @dtDate IS NULL THEN 'Enter the date on the receipt.'
        WHEN @strDescription = '' THEN 'Enter what it was for.'
        WHEN ISNULL(@dblAmount, 0) <= 0 THEN 'Enter the amount paid.'
        WHEN @dblTax < 0 OR @dblTax > @dblAmount THEN 'The tax can''t be more than the amount.'
        WHEN NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0) AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the account it goes to.'
        WHEN @iTaxCodeID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iTaxCodeID AND iUserID = @iUserId)
            THEN 'That tax code was not found in this portfolio.'
        WHEN (@iTracking1ID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
                                                        WHERE O.iOptionID = @iTracking1ID AND C.iUserID = @iUserId AND C.iPosition = 1))
          OR (@iTracking2ID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
                                                        WHERE O.iOptionID = @iTracking2ID AND C.iUserID = @iUserId AND C.iPosition = 2))
            THEN 'Choose tracking options from this portfolio''s categories.'
        WHEN ISNULL(@iClaimID, 0) > 0 AND NOT EXISTS (SELECT 1 FROM tblExpenseClaims WHERE iClaimID = @iClaimID AND iUserID = @iUserId AND strStatus IN ('Submitted', 'Rejected'))
            THEN 'Only a claim that hasn''t been approved can be changed.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END

    IF ISNULL(@iClaimID, 0) = 0
    BEGIN
        INSERT tblExpenseClaims (iUserID, iClaimantUserID, strClaimant, dtmDate, strPayee, strDescription, iAccountID, iTaxCodeID, dblAmount, dblTax,
            iTrackingOption1ID, iTrackingOption2ID, strStatus)
        VALUES (@iUserId, @iClaimantUserID, @strClaimant, @dtDate, NULLIF(LTRIM(RTRIM(@strPayee)), ''), @strDescription, @iAccountID, @iTaxCodeID,
            @dblAmount, @dblTax, @iTracking1ID, @iTracking2ID, 'Submitted');
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblExpenseClaims
    SET strClaimant = @strClaimant, dtmDate = @dtDate, strPayee = NULLIF(LTRIM(RTRIM(@strPayee)), ''), strDescription = @strDescription,
        iAccountID = @iAccountID, iTaxCodeID = @iTaxCodeID, dblAmount = @dblAmount, dblTax = @dblTax,
        iTrackingOption1ID = @iTracking1ID, iTrackingOption2ID = @iTracking2ID, strStatus = 'Submitted', strRejectReason = NULL
    WHERE iClaimID = @iClaimID AND iUserID = @iUserId;
    SELECT 'Success' AS Result, @iClaimID AS Id;
END
GO

-- Approves a submitted claim and posts it: Dr the account (tax split out), Cr 2150 Expense Claims Payable. Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspApproveExpenseClaim
    @iUserId         INT,
    @iClaimID        INT,
    @iApproverUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strStatus VARCHAR(10), @dtDate DATE, @iAccountID INT, @iTaxCodeID INT, @dblAmount DECIMAL(19,4), @dblTax DECIMAL(19,4),
            @t1 INT, @t2 INT, @strDescription VARCHAR(500);
    SELECT @strStatus = strStatus, @dtDate = dtmDate, @iAccountID = iAccountID, @iTaxCodeID = iTaxCodeID, @dblAmount = dblAmount, @dblTax = dblTax,
           @t1 = iTrackingOption1ID, @t2 = iTrackingOption2ID,
           @strDescription = LEFT(CAST('Expense claim - ' + strClaimant + ': ' + strDescription + ISNULL(' (' + strPayee + ')', '') AS VARCHAR(600)), 500)
    FROM tblExpenseClaims WHERE iClaimID = @iClaimID AND iUserID = @iUserId;

    IF @strStatus IS NULL OR @strStatus <> 'Submitted'
    BEGIN
        SELECT 'Error: Only a submitted claim can be approved.' AS Result;
        RETURN;
    END

    DECLARE @iPayableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 2150, 'Expense Claims Payable', 'Liability', @iPayableID OUTPUT;

    BEGIN TRANSACTION;
    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'ExpenseClaim', @iClaimID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    IF @iTaxCodeID IS NOT NULL
        EXEC dbo.uspAddJournalLineWithTax @iUserId, @iJournalID, @iAccountID, @dblAmount, 0, @strDescription, @iTaxCodeID, @dblTax;
    ELSE
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription) VALUES (@iJournalID, @iAccountID, @dblAmount, 0, @strDescription);
    UPDATE tblJournalLines SET iTrackingOption1ID = @t1, iTrackingOption2ID = @t2 WHERE iJournalID = @iJournalID AND (@t1 IS NOT NULL OR @t2 IS NOT NULL);
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription) VALUES (@iJournalID, @iPayableID, 0, @dblAmount, @strDescription);

    UPDATE tblExpenseClaims SET strStatus = 'Approved', iJournalID = @iJournalID, iApprovedByUserID = @iApproverUserID, dtmApproved = GETDATE()
    WHERE iClaimID = @iClaimID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspRejectExpenseClaim
    @iUserId   INT,
    @iClaimID  INT,
    @strReason NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE tblExpenseClaims SET strStatus = 'Rejected', strRejectReason = NULLIF(LTRIM(RTRIM(@strReason)), '')
    WHERE iClaimID = @iClaimID AND iUserID = @iUserId AND strStatus = 'Submitted';
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: Only a submitted claim can be rejected.' END AS Result;
END
GO

-- Takes an approved, unpaid claim back to submitted and deletes its journal.
CREATE OR ALTER PROCEDURE dbo.uspUnapproveExpenseClaim
    @iUserId  INT,
    @iClaimID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @iJournalID INT, @strStatus VARCHAR(10);
    SELECT @iJournalID = iJournalID, @strStatus = strStatus FROM tblExpenseClaims WHERE iClaimID = @iClaimID AND iUserID = @iUserId;
    IF ISNULL(@strStatus, '') <> 'Approved'
    BEGIN
        SELECT 'Error: Only an approved claim that hasn''t been paid can be taken back.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    IF @iJournalID IS NOT NULL
        DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    UPDATE tblExpenseClaims SET strStatus = 'Submitted', iJournalID = NULL, iApprovedByUserID = NULL, dtmApproved = NULL WHERE iClaimID = @iClaimID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

-- Reimburses approved claims (<claims><c id="..."/></claims>, all for the same person) in one payment from the bank:
-- Dr 2150 Expense Claims Payable, Cr the bank. Returns Result and iJournalID.
CREATE OR ALTER PROCEDURE dbo.uspPayExpenseClaims
    @iUserId    INT,
    @xmlClaims  XML,
    @dtDate     DATE,
    @iAccountID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Ids TABLE (iClaimID INT PRIMARY KEY);
    INSERT @Ids SELECT DISTINCT X.c.value('@id', 'INT') FROM @xmlClaims.nodes('/claims/c') AS X(c);

    DECLARE @iPayableID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 2150, 'Expense Claims Payable', 'Liability', @iPayableID OUTPUT;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN NOT EXISTS (SELECT 1 FROM @Ids) THEN 'Choose the claims to pay.'
        WHEN EXISTS (SELECT 1 FROM @Ids I WHERE NOT EXISTS (SELECT 1 FROM tblExpenseClaims C WHERE C.iClaimID = I.iClaimID AND C.iUserID = @iUserId AND C.strStatus = 'Approved'))
            THEN 'Only approved claims that haven''t been paid can be paid.'
        WHEN (SELECT COUNT(DISTINCT C.strClaimant) FROM tblExpenseClaims C JOIN @Ids I ON I.iClaimID = C.iClaimID) > 1
            THEN 'Pay one person''s claims at a time.'
        WHEN @dtDate IS NULL THEN 'Enter the date it was paid.'
        WHEN @iAccountID = @iPayableID OR NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                                                        AND strAccountType IN ('Asset', 'Liability') AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the bank account it was paid from.'
        WHEN EXISTS (SELECT 1 FROM tblForeignCurrencyAccounts WHERE iAccountID = @iAccountID AND iUserID = @iUserId)
            THEN 'Pay claims from an account in the portfolio''s currency.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iJournalID;
        RETURN;
    END

    DECLARE @dblTotal DECIMAL(19,4), @strClaimant NVARCHAR(200), @iCount INT;
    SELECT @dblTotal = SUM(C.dblAmount), @strClaimant = MAX(C.strClaimant), @iCount = COUNT(*)
    FROM tblExpenseClaims C JOIN @Ids I ON I.iClaimID = C.iClaimID;
    DECLARE @strDescription VARCHAR(500) = LEFT(CAST('Expense claims paid - ' + @strClaimant + ' (' + CAST(@iCount AS NVARCHAR(10)) + ')' AS VARCHAR(600)), 500);

    BEGIN TRANSACTION;
    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'ExpenseClaimPayment', NULL, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    VALUES (@iJournalID, @iPayableID, @dblTotal, 0, @strDescription), (@iJournalID, @iAccountID, 0, @dblTotal, @strDescription);
    UPDATE tblJournal SET iSourceID = @iJournalID WHERE iJournalID = @iJournalID;
    UPDATE C SET strStatus = 'Paid', iPaymentJournalID = @iJournalID, dtmPaid = @dtDate
    FROM tblExpenseClaims C JOIN @Ids I ON I.iClaimID = C.iClaimID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iJournalID AS iJournalID;
END
GO

-- Deletes a reimbursement: its journal goes and its claims are approved and unpaid again.
CREATE OR ALTER PROCEDURE dbo.uspUndoExpenseClaimPayment
    @iUserId    INT,
    @iJournalID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId AND strSourceModule = 'ExpenseClaimPayment')
    BEGIN
        SELECT 'Error: That payment wasn''t found.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId;
    UPDATE tblExpenseClaims SET strStatus = 'Approved', iPaymentJournalID = NULL, dtmPaid = NULL WHERE iPaymentJournalID = @iJournalID AND iUserID = @iUserId;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteExpenseClaim
    @iUserId  INT,
    @iClaimID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM tblExpenseClaims WHERE iClaimID = @iClaimID AND iUserID = @iUserId AND strStatus IN ('Submitted', 'Rejected'))
    BEGIN
        SELECT 'Error: Only a claim that hasn''t been approved can be deleted.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    DELETE FROM tblAttachments WHERE iUserID = @iUserId AND strKind = 'ExpenseClaim' AND iTargetID = @iClaimID;
    DELETE FROM tblExpenseClaims WHERE iClaimID = @iClaimID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Expense claims migration done.';
GO
