-- =============================================
-- Check:     journals whose debits and credits don't balance
-- Date:      15-Oct-2026
-- Read only: this script changes nothing. Run it any time, before or after
-- 2026-10-15-ledger-fixes.sql, to see how much existing ledger data is wrong.
--
-- Result 1: every unbalanced journal (Difference = debits - credits).
-- Result 2: the same, totalled by portfolio and by where the journal came from
--           (Payout, ShareTrade, BankTransaction, ManualJournal...).
--
-- A Payout journal from before the migration can be fixed by opening the
-- payout on the new site and saving it: uspUpdatePayout re-posts its lines.
-- =============================================
SET NOCOUNT ON;

WITH JournalTotals AS (
    SELECT
        J.iUserID,
        J.iJournalID,
        J.dtmTransactionDate,
        J.strDescription,
        J.strSourceModule,
        J.iSourceID,
        COUNT(JL.iJournalLineID) AS LineCount,
        CAST(SUM(ISNULL(JL.dblDebit, 0)) AS DECIMAL(19,4)) AS TotalDebit,
        CAST(SUM(ISNULL(JL.dblCredit, 0)) AS DECIMAL(19,4)) AS TotalCredit
    FROM tblJournal J
    LEFT JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
    GROUP BY J.iUserID, J.iJournalID, J.dtmTransactionDate, J.strDescription, J.strSourceModule, J.iSourceID
)
SELECT
    T.iUserID AS PortfolioId,
    U.strEntityName AS PortfolioName,
    T.iJournalID AS JournalId,
    T.dtmTransactionDate AS JournalDate,
    T.strDescription AS Description,
    T.strSourceModule AS SourceModule,
    T.iSourceID AS SourceId,
    T.LineCount,
    T.TotalDebit,
    T.TotalCredit,
    T.TotalDebit - T.TotalCredit AS Difference
FROM JournalTotals T
LEFT JOIN tblUsers U ON U.iUserId = T.iUserID
WHERE T.TotalDebit <> T.TotalCredit
ORDER BY T.iUserID, T.dtmTransactionDate, T.iJournalID;

WITH JournalTotals AS (
    SELECT
        J.iUserID,
        J.iJournalID,
        J.strSourceModule,
        CAST(SUM(ISNULL(JL.dblDebit, 0)) AS DECIMAL(19,4)) AS TotalDebit,
        CAST(SUM(ISNULL(JL.dblCredit, 0)) AS DECIMAL(19,4)) AS TotalCredit
    FROM tblJournal J
    LEFT JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
    GROUP BY J.iUserID, J.iJournalID, J.strSourceModule
)
SELECT
    T.iUserID AS PortfolioId,
    U.strEntityName AS PortfolioName,
    ISNULL(T.strSourceModule, '(none)') AS SourceModule,
    COUNT(*) AS UnbalancedJournals,
    SUM(T.TotalDebit - T.TotalCredit) AS NetDifference,
    SUM(ABS(T.TotalDebit - T.TotalCredit)) AS TotalAbsoluteDifference
FROM JournalTotals T
LEFT JOIN tblUsers U ON U.iUserId = T.iUserID
WHERE T.TotalDebit <> T.TotalCredit
GROUP BY T.iUserID, U.strEntityName, T.strSourceModule
ORDER BY T.iUserID, SourceModule;
GO
