namespace ModificationsKnown.Core.Common;

/// <summary>
/// A small, explicit result type for operations that can fail without throwing.
/// </summary>
public readonly record struct OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message = "OK") => new(true, message);

    public static OperationResult Fail(string message) => new(false, message);
}
