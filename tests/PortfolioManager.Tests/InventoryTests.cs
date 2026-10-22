using PortfolioManager.Core.Inventory;

namespace PortfolioManager.Tests;

public class InventoryTests
{
    [Fact]
    public void On_hand_adds_up_movements_to_the_date()
    {
        StockMovement[] moves =
        [
            new(1, 5, new DateOnly(2026, 7, 1), "Bill", 1, "B1", 10, 50),
            new(2, 5, new DateOnly(2026, 8, 1), "Invoice", 2, "INV-1", -4, -20),
            new(3, 6, new DateOnly(2026, 8, 1), "Bill", 3, "B2", 99, 99),
            new(4, 5, new DateOnly(2026, 9, 1), "Invoice", 4, "INV-2", -1, -5),
        ];
        Assert.Equal(6m, InventoryRules.OnHandAt(moves, 5, new DateOnly(2026, 8, 31)));
        Assert.Equal(5m, InventoryRules.OnHandAt(moves, 5, new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void Stocktake_sends_only_the_changes_with_a_cost_for_stock_found()
    {
        var xml = InventoryRules.AdjustmentXml([
            new StocktakeLine { ItemId = 1, OnHand = 10, Counted = 8 },
            new StocktakeLine { ItemId = 2, OnHand = 5, Counted = 5 },
            new StocktakeLine { ItemId = 3, OnHand = 0, Counted = 2, UnitCost = 4.5m },
            new StocktakeLine { ItemId = 4, OnHand = 3 },
        ]);
        Assert.Equal("<items><i id=\"1\" q=\"-2\" /><i id=\"3\" q=\"2\" c=\"4.5\" /></items>", xml);
    }
}
