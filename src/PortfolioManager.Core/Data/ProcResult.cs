namespace PortfolioManager.Core.Data;

/// <summary>The one-row message a write proc returns: success, or the text after "Error:".</summary>
public sealed record ProcResult(bool Ok, string? Error = null, int? Id = null)
{
    public static ProcResult Success(int? id = null) => new(true, null, id);

    public static ProcResult Failed(string error) => new(false, error);

    /// <summary>
    /// Reads the proc's Result text. "Error: ..." becomes the message; text starting with
    /// successStart is success; anything else (or nothing) is treated as a failure.
    /// </summary>
    public static ProcResult From(string? result, string successStart, int? id = null)
    {
        var text = result?.Trim() ?? "";
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return Failed(text["Error:".Length..].Trim());
        if (text.StartsWith(successStart, StringComparison.OrdinalIgnoreCase))
            return Success(id);
        return Failed("The database didn't confirm the change. Check that the latest database migration has been run.");
    }
}
