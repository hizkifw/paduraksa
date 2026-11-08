namespace Paduraksa.Dashboard.AccountArea;

/// <summary>What the two-step verification drawer does.</summary>
public enum TwoFactorStep
{
    /// <summary>Set up an authenticator.</summary>
    Enrol,

    /// <summary>Make new recovery codes (behind the password).</summary>
    Regenerate,

    /// <summary>Turn two-step verification off (behind the password).</summary>
    Disable,
}
