using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Identity;

/// <summary>
/// Which <see cref="Permission"/> each kind of operation needs, so the services that refuse an operation and the Admin
/// endpoints and dashboard pages that offer it name the same one. The check itself is
/// <see cref="IPermissionEvaluator"/>.
/// </summary>
public static class OperationPermissions
{
    /// <summary>Every config command (host, upstream, profile, rate limit, access list, settings, replace, rollback).</summary>
    public const Permission ForConfigCommand = Permission.ConfigWrite;

    /// <summary>A config command that changes the server settings (<c>settings.server</c>), on top of <see cref="ForConfigCommand"/>.</summary>
    public const Permission ForServerSettings = Permission.ServerWrite;

    /// <summary>Uploading, issuing, re-issuing and deleting certificates.</summary>
    public const Permission ForCertificateOperation = Permission.CertificatesWrite;

    /// <summary>
    /// What simulating against <paramref name="target"/> needs: every simulation needs <see cref="Permission.Simulate"/>; a draft
    /// or a stored revision shows what the config contains, so those also need <see cref="Permission.ConfigRead"/>; the caller's
    /// stored draft needs what reading the draft does (<see cref="ForConfigCommand"/>).
    /// </summary>
    public static IReadOnlyList<Permission> ForSimulation(SimulationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target switch
        {
            LiveSimulationTarget => [Permission.Simulate],
            DraftSimulationTarget => [Permission.Simulate, ForConfigCommand],
            _ => [Permission.Simulate, Permission.ConfigRead],
        };
    }
}
