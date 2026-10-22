-- =============================================
-- Navexa API sync (Import, Navexa sync page)
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Safe to run more than once.
--   tblNavexaConnection  one row per portfolio: the encrypted API key, the Navexa portfolio, settings and schedule
--   tblNavexaMapping     how Navexa holdings, cash accounts and cash types are recorded here
--   tblNavexaSyncItem    every Navexa record the sync has seen, so nothing is added twice
--   uspAddNavexaCashTransaction  a cash entry with a chosen ledger account on its other side
-- =============================================

IF OBJECT_ID('dbo.tblNavexaConnection', 'U') IS NULL
CREATE TABLE dbo.tblNavexaConnection
(
    iUserId                 INT            NOT NULL CONSTRAINT PK_tblNavexaConnection PRIMARY KEY, -- the portfolio
    strApiKeyProtected      NVARCHAR(MAX)  NOT NULL, -- encrypted by the web app; never the plain key
    strApiKeyHint           NVARCHAR(10)   NULL,     -- last four characters, to show which key is saved
    iNavexaPortfolioId      INT            NOT NULL,
    strNavexaPortfolioName  NVARCHAR(200)  NULL,
    strTradeBankAccount     NVARCHAR(500)  NULL,     -- NULL: trades are recorded without a cash settlement
    dtmImportFrom           DATE           NULL,
    strSchedule             VARCHAR(10)    NOT NULL CONSTRAINT DF_tblNavexaConnection_Schedule DEFAULT ('Off'), -- Off, Daily, Weekly
    dtmLastChecked          DATETIME2(0)   NULL,     -- UTC, by Sync now or the schedule
    strLastResult           NVARCHAR(1000) NULL,
    iConnectedByLoginId     INT            NULL,
    dtmCreated              DATETIME2(0)   NOT NULL CONSTRAINT DF_tblNavexaConnection_Created DEFAULT (SYSUTCDATETIME())
);
GO

IF OBJECT_ID('dbo.tblNavexaMapping', 'U') IS NULL
CREATE TABLE dbo.tblNavexaMapping
(
    iUserId     INT           NOT NULL,
    strKind     VARCHAR(20)   NOT NULL, -- Holding, CashAccount, CashType
    strKey      NVARCHAR(100) NOT NULL, -- Navexa holding id, cash account holding id, or cash type name
    strLabel    NVARCHAR(500) NULL,
    bSkip       BIT           NOT NULL CONSTRAINT DF_tblNavexaMapping_Skip DEFAULT (0),
    strExchange NVARCHAR(50)  NULL,     -- Holding
    strCode     NVARCHAR(500) NULL,     -- Holding: stock code; CashAccount: bank account name
    iAccountID  INT           NULL,     -- CashType: ledger account for the other side (NULL = Owner's Capital)
    CONSTRAINT PK_tblNavexaMapping PRIMARY KEY (iUserId, strKind, strKey)
);
GO

IF OBJECT_ID('dbo.tblNavexaSyncItem', 'U') IS NULL
CREATE TABLE dbo.tblNavexaSyncItem
(
    iUserId            INT            NOT NULL,
    strNavexaType      VARCHAR(30)    NOT NULL,
    strNavexaId        VARCHAR(50)    NOT NULL,
    dtmDate            DATE           NULL,
    strStatus          VARCHAR(20)    NOT NULL, -- Ready, NeedsInput, Waiting, Skipped, Imported, Failed
    strKind            VARCHAR(20)    NULL,     -- Trade, Payout, Drp, Cash, Split, CapitalReturn
    strDescription     NVARCHAR(300)  NULL,
    strMessage         NVARCHAR(1000) NULL,
    strPayload         NVARCHAR(MAX)  NULL,     -- Navexa's record as JSON, kept until it is added
    strInput           NVARCHAR(MAX)  NULL,     -- what the user entered for it (franking credit, DRP units, ...)
    dtmNavexaModified  DATETIME2(3)   NULL,
    bChangedInNavexa   BIT            NOT NULL CONSTRAINT DF_tblNavexaSyncItem_Changed DEFAULT (0),
    dtmImported        DATETIME2(0)   NULL,     -- UTC
    dtmUpdated         DATETIME2(0)   NOT NULL CONSTRAINT DF_tblNavexaSyncItem_Updated DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_tblNavexaSyncItem PRIMARY KEY (iUserId, strNavexaType, strNavexaId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblNavexaSyncItem_Status' AND object_id = OBJECT_ID('dbo.tblNavexaSyncItem'))
    CREATE INDEX IX_tblNavexaSyncItem_Status ON dbo.tblNavexaSyncItem (iUserId, strStatus);
GO

-- =============================================
-- A Navexa cash entry: uspAddCashTransaction (bank row, running balance and, in Hybrid/DoubleEntry
-- mode, a journal against Owner's Capital), then the Owner's Capital line moved to the chosen ledger
-- account, e.g. Interest Income for interest or an expense account for a fee.
-- Errors are thrown, so the caller's transaction rolls the whole entry back.
-- Returns one row: Result ('Cash Transaction Added' or 'Error: ...') and iBankStatementId.
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAddNavexaCashTransaction]
(
    @iUserId              INT,
    @strBankAccountName   VARCHAR(500),
    @dtmDateOfTransaction DATE,
    @dblWithdrawalAmount  DECIMAL(10,2),
    @dblDepositAmount     DECIMAL(10,2),
    @strComment           VARCHAR(500),
    @strCurrency          VARCHAR(50) = NULL,
    @iContraAccountID     INT = NULL -- NULL keeps Owner's Capital (3100)
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NULLIF(LTRIM(RTRIM(ISNULL(@strBankAccountName, ''))), '') IS NULL
        THROW 50201, 'Choose the bank account for this cash transaction.', 1;

    IF (ISNULL(@dblDepositAmount, 0) > 0 AND ISNULL(@dblWithdrawalAmount, 0) > 0)
        OR (ISNULL(@dblDepositAmount, 0) <= 0 AND ISNULL(@dblWithdrawalAmount, 0) <= 0)
        THROW 50202, 'A cash transaction is either a deposit or a withdrawal, of more than zero.', 1;

    IF @iContraAccountID IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM tblChartOfAccounts
        WHERE iAccountID = @iContraAccountID AND (iUserID = @iUserId OR iUserID = 0) AND bIsPostable = 1)
        THROW 50203, 'The ledger account chosen for this cash transaction was not found in the portfolio.', 1;

    BEGIN TRANSACTION;

    -- uspAddCashTransaction doesn't return the new row's id, so note the highest id first (locked until commit)
    DECLARE @iBefore INT = ISNULL((SELECT MAX(iBankStatementId) FROM tblBankStatement WITH (UPDLOCK, HOLDLOCK) WHERE iUserId = @iUserId), 0);

    EXEC dbo.uspAddCashTransaction @iUserId, @strBankAccountName, @dtmDateOfTransaction,
        @dblWithdrawalAmount, @dblDepositAmount, @strComment, NULL, @strCurrency;

    DECLARE @iBankStatementId INT = (SELECT MIN(iBankStatementId) FROM tblBankStatement WHERE iUserId = @iUserId AND iBankStatementId > @iBefore);
    IF @iBankStatementId IS NULL
        THROW 50204, 'The cash transaction was not saved.', 1;

    IF @iContraAccountID IS NOT NULL
    BEGIN
        DECLARE @iBankGLAccountID INT = (
            SELECT TOP 1 iAccountID FROM tblChartOfAccounts
            WHERE strAccountName = @strBankAccountName AND iUserID = @iUserId);

        UPDATE L SET iAccountID = @iContraAccountID
        FROM tblJournalLines L
        JOIN tblJournal J ON J.iJournalID = L.iJournalID
        WHERE J.iUserID = @iUserId
          AND J.strSourceModule = 'BankTransaction'
          AND J.iSourceID = @iBankStatementId
          AND L.iAccountID <> ISNULL(@iBankGLAccountID, -1);
    END

    COMMIT TRANSACTION;

    SELECT 'Cash Transaction Added' AS Result, @iBankStatementId AS iBankStatementId;
END
GO
