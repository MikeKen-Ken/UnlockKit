namespace UnlockKit.UI.Models;

/// <summary>One row in the locker grid: a process holding the target, not a single handle.</summary>
public sealed class LockerRow
{
    public required string Name { get; init; }
    public required int Pid { get; init; }
    public required int Handles { get; init; }
    public required string Access { get; init; }
    /// <summary>Sample of the actual files/directories this process holds (subtree scans).</summary>
    public required string Files { get; init; }
    public required bool Protected { get; init; }
    public required bool Denied { get; init; }
    public required string Note { get; init; }

    public string PidText => Pid.ToString();
    public string Status => Denied ? "Guarded" : Protected ? "PPL" : "—";
}
