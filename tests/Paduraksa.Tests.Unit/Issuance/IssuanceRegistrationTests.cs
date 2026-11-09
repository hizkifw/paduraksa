using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Application.Issuance;

namespace Paduraksa.Tests.Unit.Issuance;

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
