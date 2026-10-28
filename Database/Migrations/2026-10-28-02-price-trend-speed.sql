-- =============================================
-- Daily price job: the trend update (200-day average and 52-week spread) stops reading every row of
-- tblSharePrice on each pass. Safe to run twice. Only the one lookup changed; the rest is as it was.
-- Kept with the same SET options the proc was first created with.
-- =============================================
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE OR ALTER PROCEDURE [dbo].[uspUpdateMissingSharePriceTrendsByDateAndExchange] 
(
	@strStockExchangeCode varchar(10),
	@dtmDetailsAsAtDate Date,
	@dtmStartDate Date,
	@strUpdateSharePriceCache VarChar(3) = 'Yes'
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        29-Jan-2021
-- Version:     1.001
-- Description:	Find and update missing share price trends by date
-- =============================================
-- Author:		Tony Darcy
-- Date:        25-May-2024
-- Version:     1.002
-- Description:	Added @strUpdateSharePriceCache as an optional paramiter & changed @iMaxiumumNumberOfLinesToUpdate from 100 to 1000
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        28-Oct-2026
-- Description:	Speed: the traded-stocks lookup matches the exchange and code columns instead of a
--              joined-up text key, so it seeks IX_SharePrice_StockDate instead of reading every price.
--              Same results.
-- =============================================
-- Add Entry To Log
-- =============================================
Insert Into tblLog ([dtmDateAccessed], [iUserID], [strBreifComment], [strDetailedComment])
Values (GETDATE(), 1, 'Update Missing Share Price Trends', 'Update Missing Share Price Trends - Stored Procedure. Stock Exchange: ' + @strStockExchangeCode + ' @strUpdateSharePriceCache = ' + @strUpdateSharePriceCache)
-- =============================================
-- Run Query
-- =============================================

If @dtmDetailsAsAtDate Is Null
	Begin
		Set @dtmDetailsAsAtDate = dateadd(d, -((datepart(weekday, getdate()) + 1 + @@DATEFIRST) % 7), getdate()) -- Most Recent Friday
	End
If @dtmStartDate Is Null
	Begin
		Set @dtmStartDate = DateAdd(w,-1,@dtmDetailsAsAtDate)
	End

--Select @dtmDetailsAsAtDate As '@dtmDetailsAsAtDate', @dtmStartDate As '@dtmStartDate';

Declare @strStockCode As VarChar(500)

Declare @iNumberOfLinesRemaining As Integer = 0
Declare @iNumberOfDaysToSkip As Integer = -7
Declare @dtmDetailsAsAtDateForLineToUpdate As Date = @dtmDetailsAsAtDate
Declare @iMaxiumumNumberOfLinesToUpdate As Integer = 1000
-- =============================================
-- Add Stocks to Temporary Table
-- =============================================

While (@dtmDetailsAsAtDate >= @dtmStartDate)
Begin

	IF OBJECT_ID('tempdb..#temptblSharePrice') IS NOT NULL DROP TABLE #temptblSharePrice
	Select * 
	Into #temptblSharePrice
	From tblSharePrice
	Where dtmDetailsAsAtDate = @dtmDetailsAsAtDate
	And strStockExchangeCode = @strStockExchangeCode
	And (strStockExchangeCode = 'ASX' and Len(strStockCode) <= 4 or strStockExchangeCode != 'ASX') -- Exclude Warrants
	And (strStockExchangeCode = 'Forex' and strStockCode Like '%AUD%' or strStockExchangeCode != 'Forex') -- Only AUD Forex Prices
	Order By strStockExchangeCode, strStockCode;
-- =============================================
--Insert Prices From Prior Day If There Was No Trading
-- =============================================
	Declare @i As Integer --= (Case When @iNumberOfDaysToSkip >= 0, @iNumberOfDaysToSkip, @iNumberOfDaysToSkip * -1);
	If @iNumberOfDaysToSkip > 0
		Begin
			Set @i = @iNumberOfDaysToSkip;
		End
	Else
		Begin
			Set @i = @iNumberOfDaysToSkip * -1;
		End;
	
	While @i > 0
	Begin

		Insert Into #temptblSharePrice ([strStockExchangeCode]
		  ,[strStockCode]
		  ,[dtmDetailsAsAtDate]
		  ,[dblOpenPrice]
		  ,[dblHighPrice]
		  ,[dblLowPrice]
		  ,[dblClosePrice]
		  ,[iVolume]
		  ,[dblClosePrice200DayAverage]
		  ,[dblStandardDeviation52Week])
		Select [strStockExchangeCode]
		  ,[strStockCode]
		  ,[dtmDetailsAsAtDate]
		  ,[dblOpenPrice]
		  ,[dblHighPrice]
		  ,[dblLowPrice]
		  ,[dblClosePrice]
		  ,[iVolume]
		  ,[dblClosePrice200DayAverage]
		  ,[dblStandardDeviation52Week]
		From tblSharePrice
		Where dtmDetailsAsAtDate = DateAdd(D,(@i * -1),@dtmDetailsAsAtDate)
		And strStockExchangeCode = @strStockExchangeCode
		And (strStockExchangeCode = 'ASX' and Len(strStockCode) <= 4 or strStockExchangeCode != 'ASX') -- Exclude Warrants
		And (strStockExchangeCode = 'Forex' and strStockCode Like '%AUD%' or strStockExchangeCode != 'Forex') -- Only AUD Forex Prices
		And strStockExchangeCode + ':' + strStockCode 
			Not in (Select strStockExchangeCode + ':' + strStockCode 
					From #temptblSharePrice
					);

		Set @i = @i - 1;
	End

	-- =============================================
	-- Remove Data That Does Not Require Updating
	-- =============================================
	Delete From #temptblSharePrice
	Where dblStandardDeviation52Week is Not Null 
		Or dblClosePrice200DayAverage is Not Null;

	-- =============================================
	-- Add All Stocks Since Previous Friday In Share Trades Table (Regardless of Exchange)
	-- =============================================
	Insert Into #temptblSharePrice ([strStockExchangeCode]
		  ,[strStockCode]
		  ,[dtmDetailsAsAtDate]
		  ,[dblOpenPrice]
		  ,[dblHighPrice]
		  ,[dblLowPrice]
		  ,[dblClosePrice]
		  ,[iVolume]
		  ,[dblClosePrice200DayAverage]
		  ,[dblStandardDeviation52Week])
		Select [strStockExchangeCode]
		  ,[strStockCode]
		  ,[dtmDetailsAsAtDate]
		  ,[dblOpenPrice]
		  ,[dblHighPrice]
		  ,[dblLowPrice]
		  ,[dblClosePrice]
		  ,[iVolume]
		  ,[dblClosePrice200DayAverage]
		  ,[dblStandardDeviation52Week]
		  From tblSharePrice
			Where Exists (Select 1 From tblShareTrades T
				Where T.strStockExchangeCode = tblSharePrice.strStockExchangeCode
				And T.strStockCode = tblSharePrice.strStockCode)
			And dtmDetailsAsAtDate >= dateadd(d, -((datepart(weekday, getdate()) + 1 + @@DATEFIRST) % 7), getdate()) -- Most Recent Friday
			And (dblStandardDeviation52Week is Null And dblClosePrice200DayAverage is Null)

	--Select * From #temptblSharePrice;

	-- =============================================
	-- Calculate Number of Lines to Update 
	-- =============================================
		Select @iNumberOfLinesRemaining = Count(*)
		From #temptblSharePrice

		If @iNumberOfLinesRemaining > @iMaxiumumNumberOfLinesToUpdate
			Begin
				Set @iNumberOfLinesRemaining = @iMaxiumumNumberOfLinesToUpdate
				Select 'Maxiumum Number Of Lines Reached';
				Set @strUpdateSharePriceCache = 'No' -- Query will be run multiple times, only update tblSharePriceCache on the last run
			End;

		
		
	-- =============================================
	-- Select Data
	-- =============================================
		--If @iNumberOfLinesRemaining <> 0
		--Begin
		--	Select DATENAME(WEEKDAY, dtmDetailsAsAtDate) As 'Day Of Week', *
		--	From #temptblSharePrice
		--	Order By dtmDetailsAsAtDate Desc, strStockExchangeCode, strStockCode;

		--	Select @iNumberOfLinesRemaining As 'Number of Lines to Update'
		--	,Convert(VarChar, @dtmDetailsAsAtDate, 106) As 'Date'
		--	,DATENAME(WEEKDAY, @dtmDetailsAsAtDate) As 'Day Of Week';

			
		--End
	-- =============================================
	-- Update Lines
	-- =============================================
	While (@iNumberOfLinesRemaining > 0)
	Begin
		(Select Top 1 
			@strStockExchangeCode = strStockExchangeCode, 
			@strStockCode = strStockCode
			,@dtmDetailsAsAtDateForLineToUpdate = dtmDetailsAsAtDate
			From #temptblSharePrice
			)

			If (@strStockCode Is Null or @dtmDetailsAsAtDate Is Null)
				Begin
					Select '@strStockCode Is Null or @dtmDetailsAsAtDate Is Null';
					Break -- End While
				End; 

				--Show number of lines remaining if it is a multiple of 10 (helps stop the script 
				--from crashing vs showing every line)
				--If (Convert(integer, @iNumberOfLinesRemaining/10)) = @iNumberOfLinesRemaining
				--	Begin

						Select @iNumberOfLinesRemaining As 'Number of Lines Remaining', 
							@strStockExchangeCode As 'Stock Exchange', 
							@strStockCode As 'Stock',
							@dtmDetailsAsAtDateForLineToUpdate As 'Date For Line To Update',
							@dtmDetailsAsAtDate As 'Date';
				--	End;
	
		If @strStockExchangeCode = 'ASX' And len(@strStockCode) = 3 --Force The Update For 3 Digit ASX Stocks
			Begin
				Exec [dbo].[uspUpdateSharePriceTrend] @strStockExchangeCode, @strStockCode, @dtmDetailsAsAtDateForLineToUpdate, 0,	1;
			End;
		Else --If @strStockExchangeCode != 'ASX'
			Begin
				Exec [dbo].[uspUpdateSharePriceTrend] @strStockExchangeCode, @strStockCode, @dtmDetailsAsAtDateForLineToUpdate, 0,	0;
			End;
		Delete From #temptblSharePrice
		Where strStockExchangeCode = @strStockExchangeCode
		And strStockCode = @strStockCode
		And dtmDetailsAsAtDate = @dtmDetailsAsAtDateForLineToUpdate
	
		Set @iNumberOfLinesRemaining = @iNumberOfLinesRemaining - 1

	End

	Set @dtmDetailsAsAtDate = DateAdd(D,@iNumberOfDaysToSkip,@dtmDetailsAsAtDate)
End --While Loop

--If Upper(@strUpdateSharePriceCache) = 'YES'
--	Begin
--		Exec [UpdatetblSharePriceCache] @dtmStartDate;
--	End;

-- =============================================
-- Clean up tblSharePriceCache
-- =============================================
	-- =============================================
	-- Delete All Prices Except For The Most Recent
	-- =============================================
	;WITH ce 
		AS (SELECT *, 
			Row_number() 
			OVER ( 
				partition BY strStockExchangeCode + ':' + strStockCode 
				ORDER BY dtmDetailsAsAtDate DESC) AS rownumber 
		FROM   [dbo].[tblSharePriceCache]) 
	DELETE FROM ce 
	WHERE  rownumber <> 1

	-- =============================================
	-- Delete Lines Where There Are No Trades in tblShareTrades
	-- =============================================

	Delete From tblSharePriceCache
	Where  ((strStockExchangeCode + ':' + strStockCode) not in (Select Distinct strStockExchangeCode + ':' + strStockCode From tblShareTrades 
																Union Select Distinct 'Forex' + ':' + tblShareTrades.strCurrency + strPortfolioCurrency
																		From tblShareTrades
																		Left Join tblUsers on tblUsers.iUserID = tblShareTrades.iUserID))



Select 'Command Completed Sucessfully'

IF OBJECT_ID('tempdb..#temptblSharePrice') IS NOT NULL DROP TABLE #temptblSharePrice
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
