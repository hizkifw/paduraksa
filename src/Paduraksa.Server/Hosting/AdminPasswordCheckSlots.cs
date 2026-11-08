using Paduraksa.Admin.Auth;
using Paduraksa.Dashboard.Identity;

namespace Paduraksa.Server.Hosting;

/// <summary>The admin app's password-derivation slots (<see cref="AdminThrottle"/>), handed to the dashboard, so its re-authentications share the API's bound.</summary>
internal sealed class AdminPasswordCheckSlots(AdminThrottle throttle) : IPasswordCheckSlots
{
    public ValueTask<IDisposable?> EnterAsync(CancellationToken cancellationToken) => throttle.EnterPasswordCheckAsync(cancellationToken);
}
