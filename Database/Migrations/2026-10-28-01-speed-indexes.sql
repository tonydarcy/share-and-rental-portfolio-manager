-- =============================================
-- Speed: indexes for the lookups the site makes most, so reports stop reading whole tables.
-- Safe to run twice. Plain indexes only (no filtered ones: those break the older procs
-- that write to these tables, because they were created with QUOTED_IDENTIFIER OFF).
-- Building them takes a little while on a big database; the site can stay up.
-- =============================================

-- Journal lines by journal. Every ledger report (trial balance, balance sheet, profit and loss,
-- cash movement, journals list, sale gains) joins journals to their lines on iJournalID, and
-- there was no index for it, so each one read every line of every portfolio. The cash movement
-- report did that once per bank line.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JournalLines_Journal' AND object_id = OBJECT_ID('dbo.tblJournalLines'))
    CREATE NONCLUSTERED INDEX IX_JournalLines_Journal ON dbo.tblJournalLines (iJournalID)
        INCLUDE (iAccountID, dblDebit, dblCredit);
GO

-- Journals by what posted them (a trade, payout, invoice...). Used to find, redo and delete
-- a transaction's journal, and by the realised gains check.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Journal_UserSource' AND object_id = OBJECT_ID('dbo.tblJournal'))
    CREATE NONCLUSTERED INDEX IX_Journal_UserSource ON dbo.tblJournal (iUserID, strSourceModule, iSourceID)
        INCLUDE (dtmTransactionDate);
GO

-- Bank statement lines by portfolio and date (the cash balance on the dashboard, cash reports,
-- performance), and by the trade they settle.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BankStatement_UserDate' AND object_id = OBJECT_ID('dbo.tblBankStatement'))
    CREATE NONCLUSTERED INDEX IX_BankStatement_UserDate ON dbo.tblBankStatement (iUserId, dtmDateOfTransaction)
        INCLUDE (strBankAccountName, strCurrency, dblDepositAmount, dblWithdrawalAmount, dblClosingBalance, iShareTradeId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BankStatement_Trade' AND object_id = OBJECT_ID('dbo.tblBankStatement'))
    CREATE NONCLUSTERED INDEX IX_BankStatement_Trade ON dbo.tblBankStatement (iShareTradeId);
GO

-- A portfolio's own prices (Prices > Manual prices). Most price rows have no iUserID, so this is small.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SharePrice_User' AND object_id = OBJECT_ID('dbo.tblSharePrice'))
    CREATE NONCLUSTERED INDEX IX_SharePrice_User ON dbo.tblSharePrice (iUserID);
GO

-- CGT parcel matches by the buy or sell trade (checked when a trade is changed or deleted).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CGTParcelMatching_Buy' AND object_id = OBJECT_ID('dbo.tblCGTParcelMatching'))
    CREATE NONCLUSTERED INDEX IX_CGTParcelMatching_Buy ON dbo.tblCGTParcelMatching (iBuyTradeID);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CGTParcelMatching_Sell' AND object_id = OBJECT_ID('dbo.tblCGTParcelMatching'))
    CREATE NONCLUSTERED INDEX IX_CGTParcelMatching_Sell ON dbo.tblCGTParcelMatching (iSellTradeID);
GO
