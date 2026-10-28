-- =============================================
-- tblSharePriceCache (the latest price of each stock) was a heap: a table with no clustered index. Its rows are
-- deleted and re-added every day, and a heap doesn't hand the space back, so on the live site 419 rows had grown
-- to 2.4 GB of mostly empty pages. Every time SQL Server refreshed the table's statistics it read all 2.4 GB, and
-- queries that use the table (holdings, the dashboard value) timed out while that happened.
-- A clustered index packs the table down now and keeps it packed from then on. It can take a minute on a big
-- database while the empty pages are read once. Safe to run twice.
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.tblSharePriceCache') AND type = 1)
    CREATE CLUSTERED INDEX CIX_tblSharePriceCache ON dbo.tblSharePriceCache (strStockExchangeCode, strStockCode, dtmDetailsAsAtDate);
GO
UPDATE STATISTICS dbo.tblSharePriceCache WITH FULLSCAN;
GO
