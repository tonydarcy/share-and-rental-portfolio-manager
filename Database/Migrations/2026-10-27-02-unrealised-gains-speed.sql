-- =============================================
-- Unrealised gains report: stop the price lookup timing out on a big price history.
-- Safe to run twice.
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[GetUnrealizedCapitalGains]
			 @iUserID As Integer
			,@strGroupByStockCode As VarChar(500)
			,@dtmDetailsAsAtDate As Date
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        4-Mar-2021
-- Version:     1.001
-- Description:	Calculate Unrealized Capital Gains
-- =============================================
-- Author:		Tony Darcy
-- Date:        29-Mar-2021
-- Version:     1.002
-- Description:	Added Totals Row
-- =============================================
-- Author:		Tony Darcy
-- Date:        8-Mar-2024
-- Version:     1.003
-- Description:	Updated to use tblSharePriceCache instead of tblSharePrice to improve speed & avoid timeouts.
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        27-Oct-2026
-- Description:	Speed: today's report uses tblSharePriceCache (today's date was compared with
--              GETDATE(), which has a time, so it always read the full tblSharePrice), and the
--              price lookups join on the stock instead of matching a joined-up text key, so
--              IX_SharePrice_StockDate can be used. Same results.
-- =============================================
--Declare @iUserID As Integer = 1
--Declare @strGroupByStockCode As VarChar(500) = 'No'
----Declare @strGroupByStockCode As VarChar(500) = 'Yes'
--Declare @dtmDetailsAsAtDate As Date = Null

If @dtmDetailsAsAtDate is Null
	Begin
		Set @dtmDetailsAsAtDate = DateAdd(Month, 1, GetDate()) -- One Month From Now
	End;

--Select @dtmDetailsAsAtDate As '@dtmDetailsAsAtDate'

IF OBJECT_ID('tempdb..#temptblShareTrades') IS NOT NULL DROP TABLE #temptblShareTrades
IF OBJECT_ID('tempdb..#temptblCapitalGains') IS NOT NULL DROP TABLE #temptblCapitalGains
IF OBJECT_ID('tempdb..#temptblSharePrices') IS NOT NULL DROP TABLE #temptblSharePrices
IF OBJECT_ID('tempdb..#temptblCapitalGains2') IS NOT NULL DROP TABLE #temptblCapitalGains2
IF OBJECT_ID('tempdb..#temptblCapitalGains3') IS NOT NULL DROP TABLE #temptblCapitalGains3

Select
(strStockExchangeCode) As 'strStockExchangeCode',
(strStockCode) As 'Item',
dtmDateOfTransaction As 'Date',
Case When Upper(strType) = 'BUY' Then iQuantity Else 0 End As 'strUnitsPurchased',
Case When Upper(strType) = 'SELL' Then iQuantity Else 0 End As 'strUnitsSold',
--iQuantity As 'Qty',
dblCostBasisPerUnit As 'Price',
dblOrderTotal As 'Value'
Into #temptblShareTrades
From tblShareTrades
Where iUserID = @iUserID
And dtmDateOfTransaction <= @dtmDetailsAsAtDate
Order By Date;

--Select * From #temptblShareTrades;

with cte as (
select item, strStockExchangeCode, strUnitsPurchased, strUnitsSold, price, value
     , row_number() over (partition by item, strStockExchangeCode order by item, strStockExchangeCode) as rank
from   #temptblShareTrades)
select	a.strStockExchangeCode
		,a.item As 'strStockCode'
		, a.strUnitsPurchased - coalesce(b.strUnitsSold, 0) As 'strCurrentHolding'
		, a.price As 'dblCostBasisPerUnit'
--		, a.value - coalesce(b.value, 0) As 'dblCostBasis' -- This line was causing issues when there were multiple rows with the same symbol
		, (a.price * (a.strUnitsPurchased - coalesce(b.strUnitsSold, 0))) As 'dblCostBasis'
		, cast(0.00 As Float) As 'dblMarketPrice'
		, cast(0.00 As Float) As 'dblMarketValue'
into #temptblCapitalGains
from cte a
     left join cte b on a.item = b.item and a.strStockExchangeCode = b.strStockExchangeCode and a.rank = b.rank - 1
where a.strUnitsPurchased - coalesce(b.strUnitsSold, 0) > 0

-- =============================================
-- Work Out Unrealized Gain
-- =============================================
-- Create Temporary Table To Hold Share Price Data
	Select * 
	into #temptblSharePrices 
	From tblSharePrice
	Where 1=0


If @dtmDetailsAsAtDate >= Cast(GetDate() As Date) -- If date is equal to or greater than today, use tblSharePriceCache to improve speed.
	Begin
		IF OBJECT_ID('tempdb..#temptblSharePrices2') IS NOT NULL DROP TABLE #temptblSharePrices2 -- Needed to avoid bug where it thinks this table already exists

		Insert into #temptblSharePrices
			([strStockExchangeCode]
				,[strStockCode]
				,[dtmDetailsAsAtDate]
				,[dblOpenPrice]
				,[dblHighPrice]
				,[dblLowPrice]
				,[dblClosePrice]
				,[iVolume]
				,[dblClosePrice200DayAverage]
				,[dblStandardDeviation52Week])
	
			Select
				 [strStockExchangeCode]
				,[strStockCode]
				,[dtmDetailsAsAtDate]
				,[dblOpenPrice]
				,[dblHighPrice]
				,[dblLowPrice]
				,[dblClosePrice]
				,[iVolume]
				,[dblClosePrice200DayAverage]
				,[dblStandardDeviation52Week]
			From tblSharePriceCache P
			Where Exists (
				Select 1
				From tblShareTrades T
				Where T.iUserID = @iUserID
				And T.strStockExchangeCode = P.strStockExchangeCode
				And T.strStockCode = P.strStockCode
				)
			And dtmDetailsAsAtDate >= DateAdd(Year, -1, @dtmDetailsAsAtDate)
			And dtmDetailsAsAtDate <= @dtmDetailsAsAtDate
			And (iUserID is Null or iUserID = 0 or iUserID = @iUserId)
	End;
Else -- Otherwise use tblSharePrice to get all data (as tblSharePriceCache only contants the latest data for each stock)
	Begin
		IF OBJECT_ID('tempdb..#temptblSharePrices3') IS NOT NULL DROP TABLE #temptblSharePrices3 -- Needed to avoid bug where it thinks this table already exists

		Insert into #temptblSharePrices
			([strStockExchangeCode]
				,[strStockCode]
				,[dtmDetailsAsAtDate]
				,[dblOpenPrice]
				,[dblHighPrice]
				,[dblLowPrice]
				,[dblClosePrice]
				,[iVolume]
				,[dblClosePrice200DayAverage]
				,[dblStandardDeviation52Week])
	
			Select
				 [strStockExchangeCode]
				,[strStockCode]
				,[dtmDetailsAsAtDate]
				,[dblOpenPrice]
				,[dblHighPrice]
				,[dblLowPrice]
				,[dblClosePrice]
				,[iVolume]
				,[dblClosePrice200DayAverage]
				,[dblStandardDeviation52Week]
			From tblSharePrice P
			Where Exists (
				Select 1
				From tblShareTrades T
				Where T.iUserID = @iUserID
				And T.strStockExchangeCode = P.strStockExchangeCode
				And T.strStockCode = P.strStockCode
				)
			And dtmDetailsAsAtDate >= DateAdd(Year, -1, @dtmDetailsAsAtDate)
			And dtmDetailsAsAtDate <= @dtmDetailsAsAtDate
			And (iUserID is Null or iUserID = 0 or iUserID = @iUserId)
	End;

-- =============================================
-- Process Data
-- =============================================

Update #temptblCapitalGains
Set dblMarketPrice = (Select Top 1 [dblClosePrice] 
						From #temptblSharePrices 
						Where #temptblSharePrices.[strStockExchangeCode] = #temptblCapitalGains.[strStockExchangeCode] 
						And #temptblSharePrices.[strStockCode] = #temptblCapitalGains.[strStockCode] 
						Order By [dtmDetailsAsAtDate] Desc);

Update #temptblCapitalGains
Set dblMarketValue = (Case When dblMarketPrice = 0 Then 0 
							When dblMarketPrice = Null Then 0
							Else strCurrentHolding * dblMarketPrice End);


If Upper(@strGroupByStockCode) = 'YES'
	Begin
		Select	strStockExchangeCode As 'Stock Exchange'
				,strStockCode As 'Stock Code'
				,Sum(strCurrentHolding) As 'Current Holding'
				,Round(Sum(dblCostBasis) / Sum(strCurrentHolding),3) As 'Cost Basis Per Unit'
				,Sum(dblCostBasis) As 'Total Cost Basis'
				,Sum(dblMarketValue) / Sum(strCurrentHolding) As 'Market Price'
				,Sum(dblMarketValue) As 'Market Value'
				,Sum(Round(dblMarketValue - dblCostBasis,3)) As 'Total Gain / Loss'
		Into #temptblCapitalGains2
		From #temptblCapitalGains
		Group By strStockExchangeCode, strStockCode
		Order By strStockExchangeCode, strStockCode;

		Select * From #temptblCapitalGains2
		
		Union -- Add Totals Row

		Select 'Total:' As 'Stock Exchange',
				Null As 'Stock Code',
				Null  As 'Current Holding',
				Null  As 'Cost Basis Per Unit', 
				Sum(Round(dblCostBasis,2))  As 'Total Cost Basis',
				Null As 'Market Price',
				Sum(dblMarketValue) As 'Market Value',
				Sum(Round(dblMarketValue - dblCostBasis,3)) As 'Total Gain / Loss'
		From #temptblCapitalGains
	End
Else
	Begin
		Select	strStockExchangeCode As 'Stock Exchange'
				,strStockCode As 'Stock Code'
				,strCurrentHolding As 'Current Holding'
				,Round(dblCostBasisPerUnit,3) As 'Cost Basis Per Unit'
				,dblCostBasis As 'Total Cost Basis'
				,dblMarketPrice As 'Market Price'
				,dblMarketValue As 'Market Value'
				,Round(dblMarketValue - dblCostBasis,3) As 'Total Gain / Loss'
		Into #temptblCapitalGains3
		From #temptblCapitalGains
		Order By strStockExchangeCode, strStockCode

		Select * From #temptblCapitalGains3
		
		Union -- Add Totals Row

		Select 'Total:' As 'Stock Exchange',
				Null As 'Stock Code',
				Null  As 'Current Holding',
				Null  As 'Cost Basis Per Unit', 
				Sum(Round(dblCostBasis,2))  As 'Total Cost Basis',
				Null As 'Market Price',
				Sum(dblMarketValue) As 'Market Value',
				Sum(Round(dblMarketValue - dblCostBasis,3)) As 'Total Gain / Loss'
		From #temptblCapitalGains

		
	End;

	--Select * From #temptblCapitalGains2
	--Union -- Add Totals Row

	--	Select 'Total:' As 'Stock Exchange',
	--			Null As 'Stock Code',
	--			Null  As 'Current Holding',
	--			Null  As 'Cost Basis Per Unit', 
	--			Sum(Round(dblCostBasis,2))  As 'Total Cost Basis',
	--			Null As 'Market Price',
	--			Sum(dblMarketValue) As 'Market Value',
	--			Sum(Round(dblMarketValue - dblCostBasis,3)) As 'Total Gain / Loss'
	--	From #temptblCapitalGains

IF OBJECT_ID('tempdb..#temptblShareTrades') IS NOT NULL DROP TABLE #temptblShareTrades
IF OBJECT_ID('tempdb..#temptblCapitalGains') IS NOT NULL DROP TABLE #temptblCapitalGains
IF OBJECT_ID('tempdb..#temptblSharePrices') IS NOT NULL DROP TABLE #temptblSharePrices
IF OBJECT_ID('tempdb..#temptblSharePrices2') IS NOT NULL DROP TABLE #temptblSharePrices2
IF OBJECT_ID('tempdb..#temptblSharePrices3') IS NOT NULL DROP TABLE #temptblSharePrices3
IF OBJECT_ID('tempdb..#temptblCapitalGains2') IS NOT NULL DROP TABLE #temptblCapitalGains2
IF OBJECT_ID('tempdb..#temptblCapitalGains3') IS NOT NULL DROP TABLE #temptblCapitalGains3
GO
