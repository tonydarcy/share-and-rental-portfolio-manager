-- Run by build-database.sh after the demo portfolio is added. Records the same trades, cash, payouts, DRP, return of
-- capital and demerger in a new double-entry portfolio and in a new single-entry one, switches the single-entry one
-- with uspConvertToDoubleEntry, and fails unless the two ledgers are line for line the same. So a change to an add
-- proc's journal that isn't made in uspConvertToDoubleEntry too fails CI here. Rolled back at the end.
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

CREATE OR ALTER PROCEDURE #RecordSample @p INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Bank VARCHAR(100) = 'Convert Check Bank';
    EXEC uspAddCashTransaction @iUserId = @p, @strBankAccountName = @Bank, @dtmDateOfTransaction = '2024-07-01',
        @dblWithdrawalAmount = 0, @dblDepositAmount = 50000, @strComment = 'Opening deposit', @iShareTradeId = NULL;
    EXEC AddShareTradeWithCashTransaction @iUserId = @p, @strMarket = 'ASX', @strCode = 'CVAAA', @strName = 'Convert A',
        @strType = 'Buy', @dtmDateOfTransaction = '2024-07-10', @iQuantity = 100, @dblPricePerUnit = 10, @dblBrokerage = 9.95,
        @strCurrency = 'AUD', @dblExchangeRate = 1, @strShareTradeComment = '', @strBankAccountName = @Bank,
        @dtmSettlementDate = '2024-07-12', @strCashTransactionComment = NULL;
    EXEC AddShareTradeWithCashTransaction @iUserId = @p, @strMarket = 'ASX', @strCode = 'CVBBB', @strName = 'Convert B',
        @strType = 'Buy', @dtmDateOfTransaction = '2024-07-11', @iQuantity = 250.5, @dblPricePerUnit = 4.2, @dblBrokerage = 9.95,
        @strCurrency = 'AUD', @dblExchangeRate = 1, @strShareTradeComment = '', @strBankAccountName = @Bank,
        @dtmSettlementDate = '2024-07-13', @strCashTransactionComment = NULL;
    EXEC AddShareTradeWithCashTransaction @iUserId = @p, @strMarket = 'NASDAQ', @strCode = 'CVUSD', @strName = 'Convert US',
        @strType = 'Buy', @dtmDateOfTransaction = '2024-07-15', @iQuantity = 10, @dblPricePerUnit = 150, @dblBrokerage = 5,
        @strCurrency = 'USD', @dblExchangeRate = 0.6612, @strShareTradeComment = '', @strBankAccountName = @Bank,
        @dtmSettlementDate = '2024-07-17', @strCashTransactionComment = NULL;
    EXEC uspAddShareTrade @iUserId = @p, @strMarket = 'ASX', @strCode = 'CVCCC', @strName = 'Convert C', @strType = 'Buy',
        @dtmDateOfTransaction = '2024-08-01', @iQuantity = 50, @dblPricePerUnit = 2, @dblBrokerage = 0, @strCurrency = 'AUD',
        @dblExchangeRate = 1, @strComment = 'No cash side', @iBankStatementId = NULL;
    EXEC uspAddPayout @iUserId = @p, @strStockExchangeCode = 'ASX', @strStockCode = 'CVAAA', @dtmTransactionDate = '2024-09-20',
        @dblTotal = 50, @dblNetAmount = 50, @dblFrankedAmount = 40, @dblUnFrankedAmount = 10, @dblInterestAmount = 0,
        @dblTFNWithholdingAmount = 0, @dblForeignIncomeTaxAmount = 0, @dblOtherAmount = 0, @strComment = NULL,
        @strPayoutConfirmed = 'Yes', @dblFrankingCredit = 17.14;
    EXEC uspAddDRPTransaction @iUserId = @p, @strStockExchangeCode = 'ASX', @strStockCode = 'CVBBB', @dtmTransactionDate = '2024-09-25',
        @dblTotalPayout = 21, @dblFrankedAmount = 21, @dblUnFrankedAmount = 0, @dblFrankingCredit = 9, @iQuantity = 5, @dblPricePerUnit = 4.2;
    EXEC uspAddReturnOfCapital @iUserId = @p, @strStockExchangeCode = 'ASX', @strStockCode = 'CVBBB', @dtmPaymentDate = '2024-10-01',
        @dblTotalAmount = 25.55, @dblAmountPerUnit = 0.1, @strComment = NULL;
    EXEC uspApplyDemerger @iUserID = @p, @strMarket = 'ASX', @strCode = 'CVAAA', @strNewMarket = 'ASX', @strNewCode = 'CVDDD',
        @strNewName = 'Convert D', @dtmDate = '2024-11-01', @iNewUnits = 20, @dblNewPerOld = 0.2, @dblCostShare = 0.1,
        @dblCostMoved = 100.99, @strComment = NULL;
    EXEC AddShareTradeWithCashTransaction @iUserId = @p, @strMarket = 'ASX', @strCode = 'CVAAA', @strName = 'Convert A',
        @strType = 'Sell', @dtmDateOfTransaction = '2025-02-03', @iQuantity = 40, @dblPricePerUnit = 12.5, @dblBrokerage = 9.95,
        @strCurrency = 'AUD', @dblExchangeRate = 1, @strShareTradeComment = '', @strBankAccountName = @Bank,
        @dtmSettlementDate = '2025-02-05', @strCashTransactionComment = NULL;
    EXEC uspAddCashTransaction @iUserId = @p, @strBankAccountName = @Bank, @dtmDateOfTransaction = '2025-03-01',
        @dblWithdrawalAmount = 1200, @dblDepositAmount = 0, @strComment = 'Drawings', @iShareTradeId = NULL;
END
GO

DECLARE @id INT = (SELECT MIN(iUserId) FROM tblUsers WHERE strLoginEmail = 'demo@example.com');
DECLARE @r TABLE (Result NVARCHAR(4000), iJournals INT);

BEGIN TRANSACTION;

EXEC dbo.AddNewPortfolio @id, 'Convert check double', 'Individual', 'DoubleEntry';
DECLARE @double INT = (SELECT MAX(iUserId) FROM tblUsers WHERE strLoginEmail = 'demo@example.com');
EXEC dbo.AddNewPortfolio @id, 'Convert check single', 'Individual', 'SingleEntry';
DECLARE @single INT = (SELECT MAX(iUserId) FROM tblUsers WHERE strLoginEmail = 'demo@example.com');

EXEC #RecordSample @double;
EXEC #RecordSample @single;
IF EXISTS (SELECT 1 FROM tblJournal WHERE iUserID = @single)
    THROW 50000, 'The single-entry portfolio posted journals before it was switched', 1;

INSERT INTO @r EXEC dbo.uspConvertToDoubleEntry @id, @single;
IF NOT EXISTS (SELECT 1 FROM @r WHERE Result = 'Switched' AND iJournals > 0)
   OR (SELECT strAccountingMode FROM tblUsers WHERE iUserId = @single) <> 'DoubleEntry'
    THROW 50000, 'uspConvertToDoubleEntry did not switch the single-entry portfolio', 1;

-- Each journal line, with its source numbered in the order recorded (the ids differ between the two portfolios)
DECLARE @Lines TABLE (iSide INT, strSourceModule VARCHAR(50), iSourceSeq BIGINT, dtmDate DATE, strDescription VARCHAR(500),
                      strAccountName VARCHAR(500), dblDebit DECIMAL(19,4), dblCredit DECIMAL(19,4), strLine VARCHAR(500));
INSERT INTO @Lines
SELECT CASE WHEN J.iUserID = @double THEN 1 ELSE 2 END, J.strSourceModule,
       DENSE_RANK() OVER (PARTITION BY J.iUserID, J.strSourceModule ORDER BY J.iSourceID),
       J.dtmTransactionDate, J.strDescription, A.strAccountName,
       ISNULL(L.dblDebit, 0), ISNULL(L.dblCredit, 0), ISNULL(L.strDescription, '')
FROM tblJournal J
JOIN tblJournalLines L ON L.iJournalID = J.iJournalID
JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
WHERE J.iUserID IN (@double, @single);

DECLARE @diff NVARCHAR(MAX);
SELECT @diff = STRING_AGG(CAST(CONCAT(CASE WHEN D.iSide = 1 THEN 'only in double entry: ' ELSE 'only after switching: ' END,
                    D.strSourceModule, ' #', D.iSourceSeq, ' ', D.strDescription, ' / ', D.strAccountName, CASE WHEN D.strLine <> '' THEN ' (' + D.strLine + ')' ELSE '' END,
                    ' Dr ', D.dblDebit, ' Cr ', D.dblCredit) AS NVARCHAR(MAX)), '; ')
FROM (
    SELECT 1 AS iSide, * FROM (SELECT strSourceModule, iSourceSeq, dtmDate, strDescription, strAccountName, dblDebit, dblCredit, strLine FROM @Lines WHERE iSide = 1
                               EXCEPT SELECT strSourceModule, iSourceSeq, dtmDate, strDescription, strAccountName, dblDebit, dblCredit, strLine FROM @Lines WHERE iSide = 2) X
    UNION ALL
    SELECT 2, * FROM (SELECT strSourceModule, iSourceSeq, dtmDate, strDescription, strAccountName, dblDebit, dblCredit, strLine FROM @Lines WHERE iSide = 2
                      EXCEPT SELECT strSourceModule, iSourceSeq, dtmDate, strDescription, strAccountName, dblDebit, dblCredit, strLine FROM @Lines WHERE iSide = 1) Y
) D;
IF @diff IS NOT NULL OR (SELECT COUNT(*) FROM @Lines WHERE iSide = 1) <> (SELECT COUNT(*) FROM @Lines WHERE iSide = 2)
BEGIN
    DECLARE @msg NVARCHAR(2047) = LEFT(N'Switching to double entry posted a different ledger: ' + ISNULL(@diff, N'line counts differ'), 2047);
    THROW 50000, @msg, 1;
END

-- Switching again is refused, and posts nothing
DELETE FROM @r;
INSERT INTO @r EXEC dbo.uspConvertToDoubleEntry @id, @single;
IF NOT EXISTS (SELECT 1 FROM @r WHERE Result LIKE 'Error:%')
    THROW 50000, 'uspConvertToDoubleEntry should refuse a portfolio that is already double entry', 1;

-- Deleting everything in a single-entry portfolio leaves it double entry
EXEC dbo.AddNewPortfolio @id, 'Convert check delete', 'Individual', 'SingleEntry';
DECLARE @deleted INT = (SELECT MAX(iUserId) FROM tblUsers WHERE strLoginEmail = 'demo@example.com');
DECLARE @d TABLE (Result NVARCHAR(4000));
INSERT INTO @d EXEC dbo.uspDeletePortfolioData @id, @deleted, 'All';
IF NOT EXISTS (SELECT 1 FROM @d WHERE Result = 'Deleted')
   OR (SELECT strAccountingMode FROM tblUsers WHERE iUserId = @deleted) <> 'DoubleEntry'
    THROW 50000, 'Deleting everything should switch a single-entry portfolio to double entry', 1;

ROLLBACK TRANSACTION;
PRINT 'Convert check passed: switching to double entry posted the same ledger as double entry from the start (rolled back).';
