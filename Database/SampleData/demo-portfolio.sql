-- =============================================
-- Share Portfolio Manager: sample (demo) portfolio
--
-- Adds a login demo@example.com, password demo-portfolio, with a made-up
-- portfolio covering the last twelve months: a deposit, buys and a sale,
-- dividends and distributions, one manual journal and weekly prices.
-- For screenshots, testing and trying the app. Nothing in it is real.
--
-- Run it against a database built with CreateDatabase.sql and every newer
-- migration (it needs 2026-10-21-01-account-security.sql). It does nothing if the demo login already exists.
-- Docker: LOAD_SAMPLE_DATA=1 docker compose up -d runs it for you.
--
-- Every change goes through the same procedures as the website, so the
-- double-entry journals and capital gains are posted as they would be.
-- The share codes (DEMOBANK and so on) are made up so their prices can't
-- mix with real ones, because prices are shared by every portfolio.
-- Don't run it on a live site you don't want a demo login on.
-- =============================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF EXISTS (SELECT 1 FROM tblUsers WHERE strLoginEmail = 'demo@example.com')
BEGIN
    PRINT 'The demo login already exists. Nothing was changed.';
    RETURN;
END

BEGIN TRANSACTION;

-- The password is stored as a hash, as the Register page would
EXEC AddNewUser
    @strEntityName = 'Demo Portfolio',
    @strEntityType = 'Individual',
    @strEmail = 'demo@example.com',
    @strLoginPassword = 'PBKDF2$310000$JtQStUVS7NNPbvn8szv7Bg==$MZRFB8rdsTbsIhVATk2JKNtN7+luMlZUtEb0YfLkwj0=',
    @strContactName = 'Demo User';

DECLARE @iUserId INT = (SELECT MIN(iUserId) FROM tblUsers WHERE strLoginEmail = 'demo@example.com');
-- Nobody can follow an emailed link for a made-up address, so the email check is marked done
EXEC uspConfirmLoginEmail @iUserId = @iUserId;
DECLARE @Today DATE = CAST(GETDATE() AS DATE);
DECLARE @Start DATE = DATEADD(MONTH, -12, @Today);
DECLARE @Bank VARCHAR(100) = 'Demo Cash Account';
DECLARE @d DATE;

-- Opening deposit
EXEC uspAddCashTransaction @iUserId = @iUserId, @strBankAccountName = @Bank, @dtmDateOfTransaction = @Start,
    @dblWithdrawalAmount = 0, @dblDepositAmount = 60000, @strComment = 'Opening deposit', @iShareTradeId = NULL, @strCurrency = 'AUD';

-- Trades, each paid from the cash account
DECLARE @Trades TABLE (iDay INT, strCode VARCHAR(20), strName VARCHAR(100), strType VARCHAR(4), iQuantity INT, dblPrice DECIMAL(18,4));
INSERT INTO @Trades VALUES
    (3,   'DEMOBANK', 'Demo Bank Ltd',            'Buy',  300, 40.00),
    (3,   'DEMOMINE', 'Demo Mining Ltd',          'Buy',  500, 18.00),
    (4,   'DEMOETF',  'Demo Australian Index ETF', 'Buy',  150, 100.00),
    (5,   'DEMOREIT', 'Demo Property Trust',      'Buy', 1000,  4.50),
    (95,  'DEMOETF',  'Demo Australian Index ETF', 'Buy',   50, 102.00),
    (200, 'DEMOMINE', 'Demo Mining Ltd',          'Sell', 200, 24.50),
    (280, 'DEMOBANK', 'Demo Bank Ltd',            'Buy',  100, 44.00);

DECLARE @iDay INT, @strCode VARCHAR(20), @strName VARCHAR(100), @strType VARCHAR(4), @iQuantity INT, @dblPrice DECIMAL(18,4);
DECLARE trades CURSOR LOCAL FAST_FORWARD FOR SELECT iDay, strCode, strName, strType, iQuantity, dblPrice FROM @Trades ORDER BY iDay;
OPEN trades;
FETCH NEXT FROM trades INTO @iDay, @strCode, @strName, @strType, @iQuantity, @dblPrice;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @d = DATEADD(DAY, @iDay, @Start);
    EXEC AddShareTradeWithCashTransaction @iUserId = @iUserId, @strMarket = 'ASX', @strCode = @strCode, @strName = @strName,
        @strType = @strType, @dtmDateOfTransaction = @d, @iQuantity = @iQuantity, @dblPricePerUnit = @dblPrice,
        @dblBrokerage = 9.95, @strCurrency = 'AUD', @dblExchangeRate = 1, @strShareTradeComment = 'Sample trade',
        @strBankAccountName = @Bank, @dtmSettlementDate = NULL, @strCashTransactionComment = NULL;
    FETCH NEXT FROM trades INTO @iDay, @strCode, @strName, @strType, @iQuantity, @dblPrice;
END
CLOSE trades;
DEALLOCATE trades;

-- Dividends (franked at 30%) and trust distributions
DECLARE @Payouts TABLE (iDay INT, strCode VARCHAR(20), dblTotal DECIMAL(18,2), dblFranked DECIMAL(18,2), dblUnfranked DECIMAL(18,2), dblTrust DECIMAL(18,2));
INSERT INTO @Payouts VALUES
    (60,  'DEMOBANK', 360.00, 360.00,   0.00,   0.00),
    (90,  'DEMOREIT',  80.00,   0.00,   0.00,  80.00),
    (100, 'DEMOETF',  210.00, 150.00,  60.00,   0.00),
    (150, 'DEMOMINE', 250.00, 250.00,   0.00,   0.00),
    (180, 'DEMOREIT',  80.00,   0.00,   0.00,  80.00),
    (240, 'DEMOBANK', 380.00, 380.00,   0.00,   0.00),
    (270, 'DEMOREIT',  85.00,   0.00,   0.00,  85.00),
    (290, 'DEMOETF',  240.00, 170.00,  70.00,   0.00),
    (330, 'DEMOMINE', 150.00, 150.00,   0.00,   0.00),
    (360, 'DEMOREIT',  85.00,   0.00,   0.00,  85.00);

DECLARE @dblTotal DECIMAL(18,2), @dblFranked DECIMAL(18,2), @dblUnfranked DECIMAL(18,2), @dblTrust DECIMAL(18,2), @dblCredit DECIMAL(18,2);
DECLARE payouts CURSOR LOCAL FAST_FORWARD FOR SELECT iDay, strCode, dblTotal, dblFranked, dblUnfranked, dblTrust FROM @Payouts ORDER BY iDay;
OPEN payouts;
FETCH NEXT FROM payouts INTO @iDay, @strCode, @dblTotal, @dblFranked, @dblUnfranked, @dblTrust;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @d = DATEADD(DAY, @iDay, @Start);
    IF @d <= @Today
    BEGIN
        SET @dblCredit = ROUND(@dblFranked * 30 / 70, 2);
        EXEC uspAddPayout @iUserId = @iUserId, @strStockExchangeCode = 'ASX', @strStockCode = @strCode, @dtmTransactionDate = @d,
            @dblTotal = @dblTotal, @dblNetAmount = @dblTotal, @dblFrankedAmount = @dblFranked, @dblUnFrankedAmount = @dblUnfranked,
            @dblInterestAmount = 0, @dblTFNWithholdingAmount = 0, @dblForeignIncomeTaxAmount = 0, @dblOtherAmount = 0,
            @strComment = NULL, @strPayoutConfirmed = 'Yes', @dblFrankingCredit = @dblCredit, @dblTrustIncome = @dblTrust;
    END
    FETCH NEXT FROM payouts INTO @iDay, @strCode, @dblTotal, @dblFranked, @dblUnfranked, @dblTrust;
END
CLOSE payouts;
DEALLOCATE payouts;

-- A manual journal: an accountant's fee paid from the owner's own pocket
DECLARE @iJournalId INT, @iExpenses INT, @iCapital INT;
SELECT @iExpenses = iAccountID FROM tblChartOfAccounts WHERE iUserID = 0 AND iAccountNumber = 5000;
SELECT @iCapital = iAccountID FROM tblChartOfAccounts WHERE iUserID = 0 AND iAccountNumber = 3100;
SET @d = DATEADD(DAY, 120, @Start);
DECLARE @NewJournal TABLE (iJournalId DECIMAL(18,0));
INSERT INTO @NewJournal EXEC uspAddJournalHeader @iUserId = @iUserId, @dtDate = @d, @strDescription = 'Tax agent fee paid personally';
SELECT @iJournalId = iJournalId FROM @NewJournal;
EXEC uspAddJournalLine @iJournalID = @iJournalId, @iAccountID = @iExpenses, @dblDebit = 330, @dblCredit = 0, @strDescription = 'Tax agent fee';
EXEC uspAddJournalLine @iJournalID = @iJournalId, @iAccountID = @iCapital, @dblDebit = 0, @dblCredit = 330, @strDescription = 'Paid by the owner';

COMMIT TRANSACTION;

-- Weekly closing prices that drift up (or down) with a little wobble, up to today
DECLARE @Prices TABLE (strCode VARCHAR(20), dblStart DECIMAL(18,4), dblEnd DECIMAL(18,4));
INSERT INTO @Prices VALUES ('DEMOBANK', 40, 46), ('DEMOMINE', 18, 22), ('DEMOETF', 100, 108), ('DEMOREIT', 4.5, 4.2);
DECLARE @iWeek INT = 0, @iWeeks INT = DATEDIFF(DAY, @Start, @Today) / 7, @dblClose DECIMAL(18,4), @dblStart DECIMAL(18,4), @dblEnd DECIMAL(18,4);
WHILE @iWeek <= @iWeeks
BEGIN
    SET @d = CASE WHEN @iWeek = @iWeeks THEN @Today ELSE DATEADD(DAY, @iWeek * 7, @Start) END;
    DECLARE prices CURSOR LOCAL FAST_FORWARD FOR SELECT strCode, dblStart, dblEnd FROM @Prices;
    OPEN prices;
    FETCH NEXT FROM prices INTO @strCode, @dblStart, @dblEnd;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @dblClose = ROUND(@dblStart + (@dblEnd - @dblStart) * @iWeek / @iWeeks
            + @dblStart * 0.04 * SIN(@iWeek * 0.7 + LEN(@strCode)), 2);
        EXEC uspUploadSharePrice @strStockExchangeCode = 'ASX', @strStockCode = @strCode, @dtmDetailsAsAtDate = @d,
            @dblOpenPrice = @dblClose, @dblHighPrice = @dblClose, @dblLowPrice = @dblClose, @dblClosePrice = @dblClose, @iVolume = 10000;
        FETCH NEXT FROM prices INTO @strCode, @dblStart, @dblEnd;
    END
    CLOSE prices;
    DEALLOCATE prices;
    SET @iWeek += 1;
END

PRINT 'Demo portfolio added. Log in as demo@example.com with password demo-portfolio.';
