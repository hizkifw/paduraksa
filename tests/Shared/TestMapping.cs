// Linked into the test projects that map a config document without a vault.
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Shared;

/// <summary><see cref="ProxyConfigMapping.Map"/> over an in-memory password registry that holds no password and a command with none pending.</summary>
internal static class TestMapping
{
    public static MappedConfig Map(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory) =>
        ProxyConfigMapping.Map(document, revision, inventory, new TestPasswords(), PendingPasswords.None);
}
