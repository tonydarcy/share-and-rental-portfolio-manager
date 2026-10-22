-- =============================================
-- Migration: debt and fixed income
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Holdings > Loans and
-- Holdings > Term deposits and bonds. Run it after 2026-10-15-lock-periods.sql. Safe to run more than once.
-- It only adds things.
--
-- What it adds:
--   * tblLoans and tblLoanEntries: margin loans and investment loans (drawdowns, repayments, interest and fees), for
--     the balance, the loan-to-value ratio and the year's deductible interest.
--   * tblFixedIncome: term deposits, bonds and hybrids (face value, rate, start, maturity, how often interest is paid),
--     for the interest earned in a year and accrued at its end.
--   * uspSaveLoan, uspDeleteLoan, uspAddLoanEntry, uspDeleteLoanEntry, uspSaveFixedIncome, uspDeleteFixedIncome.
--     Loan entries can't be added or deleted in a locked period.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnLockedTo', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-lock-periods.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblLoans', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblLoans (
        iLoanID              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblLoans PRIMARY KEY,
        iUserID              INT NOT NULL,
        strName              NVARCHAR(200) NOT NULL,
        strLender            NVARCHAR(200) NULL,
        strKind              VARCHAR(20) NOT NULL,             -- Margin or Investment
        dblRatePercent       FLOAT NOT NULL DEFAULT (0),
        dblDeductiblePercent FLOAT NOT NULL DEFAULT (100),     -- the part used to buy income-producing investments
        dblLimit             FLOAT NULL,
        dtmOpened            DATE NOT NULL,
        strComment           NVARCHAR(500) NULL
    );
    CREATE INDEX IX_tblLoans_User ON dbo.tblLoans (iUserID);
END
GO

IF OBJECT_ID('dbo.tblLoanEntries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblLoanEntries (
        iLoanEntryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblLoanEntries PRIMARY KEY,
        iLoanID      INT NOT NULL CONSTRAINT FK_tblLoanEntries_Loan REFERENCES dbo.tblLoans (iLoanID),
        dtmDate      DATE NOT NULL,
        strKind      VARCHAR(20) NOT NULL,     -- Drawdown, Repayment, Interest, Fee
        dblAmount    FLOAT NOT NULL,
        strComment   NVARCHAR(500) NULL
    );
    CREATE INDEX IX_tblLoanEntries_Loan ON dbo.tblLoanEntries (iLoanID, dtmDate);
END
GO

IF OBJECT_ID('dbo.tblFixedIncome', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFixedIncome (
        iFixedIncomeID       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblFixedIncome PRIMARY KEY,
        iUserID              INT NOT NULL,
        strKind              VARCHAR(20) NOT NULL,     -- TermDeposit, Bond, Hybrid
        strName              NVARCHAR(200) NOT NULL,
        strStockExchangeCode VARCHAR(50) NULL,         -- a listed bond or hybrid
        strStockCode         VARCHAR(50) NULL,
        dblFaceValue         FLOAT NOT NULL,
        dblRatePercent       FLOAT NOT NULL,
        dtmStart             DATE NOT NULL,
        dtmMaturity          DATE NULL,
        strFrequency         VARCHAR(20) NOT NULL,     -- AtMaturity, Monthly, Quarterly, HalfYearly, Yearly
        strComment           NVARCHAR(500) NULL
    );
    CREATE INDEX IX_tblFixedIncome_User ON dbo.tblFixedIncome (iUserID);
END
GO

-- Adds (@iLoanID 0) or changes a loan. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveLoan
    @iUserID              INT,
    @iLoanID              INT = 0,
    @strName              NVARCHAR(200),
    @strLender            NVARCHAR(200) = NULL,
    @strKind              VARCHAR(20),
    @dblRatePercent       FLOAT = 0,
    @dblDeductiblePercent FLOAT = 100,
    @dblLimit             FLOAT = NULL,
    @dtmOpened            DATE,
    @strComment           NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF LTRIM(RTRIM(ISNULL(@strName, ''))) = '' OR @strKind NOT IN ('Margin', 'Investment')
        OR @dblRatePercent < 0 OR @dblDeductiblePercent < 0 OR @dblDeductiblePercent > 100 OR @dblLimit < 0 OR @dtmOpened IS NULL
    BEGIN
        SELECT 'Error: Check the loan''s name, type, rate and deductible percentage.' AS Result, NULL AS Id;
        RETURN;
    END
    IF ISNULL(@iLoanID, 0) > 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.tblLoans WHERE iLoanID = @iLoanID AND iUserID = @iUserID)
        BEGIN
            SELECT 'Error: That loan isn''t in this portfolio.' AS Result, NULL AS Id;
            RETURN;
        END
        UPDATE dbo.tblLoans SET strName = LTRIM(RTRIM(@strName)), strLender = @strLender, strKind = @strKind, dblRatePercent = @dblRatePercent,
            dblDeductiblePercent = @dblDeductiblePercent, dblLimit = @dblLimit, dtmOpened = @dtmOpened, strComment = @strComment
        WHERE iLoanID = @iLoanID AND iUserID = @iUserID;
        SELECT 'Success' AS Result, @iLoanID AS Id;
        RETURN;
    END
    INSERT dbo.tblLoans (iUserID, strName, strLender, strKind, dblRatePercent, dblDeductiblePercent, dblLimit, dtmOpened, strComment)
    VALUES (@iUserID, LTRIM(RTRIM(@strName)), @strLender, @strKind, @dblRatePercent, @dblDeductiblePercent, @dblLimit, @dtmOpened, @strComment);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

-- Deletes a loan and its entries, unless an entry is in a locked period. Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspDeleteLoan
    @iUserID INT,
    @iLoanID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLoans WHERE iLoanID = @iLoanID AND iUserID = @iUserID)
        SELECT 'Error: That loan isn''t in this portfolio.' AS Result;
    ELSE IF @Locked IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.tblLoanEntries WHERE iLoanID = @iLoanID AND dtmDate <= @Locked)
        SELECT 'Error: The loan has entries in a locked period, so it can''t be deleted.' AS Result;
    ELSE
    BEGIN
        BEGIN TRANSACTION;
        DELETE dbo.tblLoanEntries WHERE iLoanID = @iLoanID;
        DELETE dbo.tblLoans WHERE iLoanID = @iLoanID AND iUserID = @iUserID;
        COMMIT;
        SELECT 'Success' AS Result;
    END
END
GO

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
    ELSE IF @strKind NOT IN ('Drawdown', 'Repayment', 'Interest', 'Fee') OR ISNULL(@dblAmount, 0) <= 0 OR @dtmDate IS NULL
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

CREATE OR ALTER PROCEDURE dbo.uspDeleteLoanEntry
    @iUserID      INT,
    @iLoanEntryID INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @Date DATE = (SELECT E.dtmDate FROM dbo.tblLoanEntries E JOIN dbo.tblLoans L ON L.iLoanID = E.iLoanID
                          WHERE E.iLoanEntryID = @iLoanEntryID AND L.iUserID = @iUserID);
    IF @Date IS NULL
        SELECT 'Error: That entry isn''t in this portfolio.' AS Result;
    ELSE IF @Locked IS NOT NULL AND @Date <= @Locked
        SELECT 'Error: That date is in a locked period.' AS Result;
    ELSE
    BEGIN
        DELETE dbo.tblLoanEntries WHERE iLoanEntryID = @iLoanEntryID;
        SELECT 'Success' AS Result;
    END
END
GO

-- Adds (@iFixedIncomeID 0) or changes a term deposit, bond or hybrid. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveFixedIncome
    @iUserID              INT,
    @iFixedIncomeID       INT = 0,
    @strKind              VARCHAR(20),
    @strName              NVARCHAR(200),
    @strStockExchangeCode VARCHAR(50) = NULL,
    @strStockCode         VARCHAR(50) = NULL,
    @dblFaceValue         FLOAT,
    @dblRatePercent       FLOAT,
    @dtmStart             DATE,
    @dtmMaturity          DATE = NULL,
    @strFrequency         VARCHAR(20),
    @strComment           NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strKind NOT IN ('TermDeposit', 'Bond', 'Hybrid') OR LTRIM(RTRIM(ISNULL(@strName, ''))) = '' OR ISNULL(@dblFaceValue, 0) <= 0
        OR @dblRatePercent < 0 OR @dtmStart IS NULL OR (@dtmMaturity IS NOT NULL AND @dtmMaturity <= @dtmStart)
        OR @strFrequency NOT IN ('AtMaturity', 'Monthly', 'Quarterly', 'HalfYearly', 'Yearly')
        OR (@strFrequency = 'AtMaturity' AND @dtmMaturity IS NULL)
    BEGIN
        SELECT 'Error: Check the type, name, amount, rate and dates.' AS Result, NULL AS Id;
        RETURN;
    END
    IF ISNULL(@iFixedIncomeID, 0) > 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.tblFixedIncome WHERE iFixedIncomeID = @iFixedIncomeID AND iUserID = @iUserID)
        BEGIN
            SELECT 'Error: That investment isn''t in this portfolio.' AS Result, NULL AS Id;
            RETURN;
        END
        UPDATE dbo.tblFixedIncome SET strKind = @strKind, strName = LTRIM(RTRIM(@strName)), strStockExchangeCode = NULLIF(UPPER(LTRIM(RTRIM(@strStockExchangeCode))), ''),
            strStockCode = NULLIF(UPPER(LTRIM(RTRIM(@strStockCode))), ''), dblFaceValue = @dblFaceValue, dblRatePercent = @dblRatePercent,
            dtmStart = @dtmStart, dtmMaturity = @dtmMaturity, strFrequency = @strFrequency, strComment = @strComment
        WHERE iFixedIncomeID = @iFixedIncomeID AND iUserID = @iUserID;
        SELECT 'Success' AS Result, @iFixedIncomeID AS Id;
        RETURN;
    END
    INSERT dbo.tblFixedIncome (iUserID, strKind, strName, strStockExchangeCode, strStockCode, dblFaceValue, dblRatePercent, dtmStart, dtmMaturity, strFrequency, strComment)
    VALUES (@iUserID, @strKind, LTRIM(RTRIM(@strName)), NULLIF(UPPER(LTRIM(RTRIM(@strStockExchangeCode))), ''), NULLIF(UPPER(LTRIM(RTRIM(@strStockCode))), ''),
        @dblFaceValue, @dblRatePercent, @dtmStart, @dtmMaturity, @strFrequency, @strComment);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteFixedIncome
    @iUserID        INT,
    @iFixedIncomeID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblFixedIncome WHERE iFixedIncomeID = @iFixedIncomeID AND iUserID = @iUserID)
        SELECT 'Error: That investment isn''t in this portfolio.' AS Result;
    ELSE
    BEGIN
        DELETE dbo.tblFixedIncome WHERE iFixedIncomeID = @iFixedIncomeID AND iUserID = @iUserID;
        SELECT 'Success' AS Result;
    END
END
GO

PRINT 'Debt and fixed income migration done.';
GO
