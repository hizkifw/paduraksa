using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Application.Issuance;

namespace Paduraksa.Tests.Unit.Issuance;

/// <summary>Registering issuance (PLAN §9.3): however often it is asked for, one scheduler runs.</summary>
public sealed class IssuanceRegistrationTests
{
    [Fact]
    public void RegisteringIssuanceTwiceAddsOneSchedulerAsAHostedService()
    {
        ServiceCollection services = new();

        services.AddPaduraksaIssuance().AddPaduraksaIssuance();

        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Single(services, d => d.ServiceType == typeof(AcmeRenewalScheduler));
        Assert.Single(services, d => d.ServiceType == typeof(AcmeIssuanceService));
    }
}
