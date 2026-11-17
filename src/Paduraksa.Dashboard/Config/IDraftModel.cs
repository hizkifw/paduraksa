namespace Paduraksa.Dashboard.Config;

/// <summary>What the draft drawers ask of the object they edit.</summary>
public interface IDraftModel
{
    bool IsNew { get; }
}
