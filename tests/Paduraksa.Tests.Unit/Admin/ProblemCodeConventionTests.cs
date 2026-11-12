using System.Reflection;
using System.Text.RegularExpressions;

using Paduraksa.Admin.Api;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Drafts;
using Paduraksa.Application.Issuance;
using Paduraksa.Application.Simulation;
using Paduraksa.Contracts.Notifications;

namespace Paduraksa.Tests.Unit.Admin;

/// <summary>
/// Every problem code and every error code is dotted segments in lowerCamelCase, so a client can branch on it and the words
/// don't change shape between areas. This scans the code classes themselves, so a new flat or dotted_lower code fails here.
/// </summary>
public sealed partial class ProblemCodeConventionTests
{
    [GeneratedRegex("^[a-z][a-zA-Z0-9]*(\\.[a-z][a-zA-Z0-9]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [Fact]
    public void EveryProblemAndErrorCodeIsDottedLowerCamelCase()
    {
        List<(string Name, string Code)> codes = [.. Codes()];
        Assert.True(codes.Count > 80, $"only {codes.Count} codes were found; the scan is missing a class");

        List<string> bad = [.. codes.Where(c => !CodePattern().IsMatch(c.Code)).Select(c => $"{c.Name} = \"{c.Code}\"")];
        Assert.True(bad.Count == 0, "Codes outside the convention (^[a-z][a-zA-Z0-9]*(\\.[a-z][a-zA-Z0-9]*)+$): " + string.Join(", ", bad));
    }

    [Fact]
    public void TheProblemCodesAreGroupedInTheApiCodeClass()
    {
        // Every area is a nested class of ApiCodes, which is what the scan above walks: a code on a loose constant would escape it.
        Assert.Equal(
            ["Admin", "Auth", "Backup", "Ca", "Config", "Csrf", "Draft", "Identity", "Setup"],
            typeof(ApiCodes).GetNestedTypes(BindingFlags.Public).Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            typeof(ApiCodes).GetFields(BindingFlags.Public | BindingFlags.Static),
            field => field.IsLiteral && field.FieldType == typeof(string));
    }

    [Fact]
    public void AnExampleOfEachShapeIsChecked()
    {
        // The pattern itself: these are the shapes the convention allows and refuses.
        Assert.All(Conforming, code => Assert.Matches(CodePattern(), code));
        Assert.All(Outside, code => Assert.DoesNotMatch(CodePattern(), code));
    }

    private static readonly string[] Conforming = ["setup.required", "auth.rateLimited", "config.publishFailed", "ca.notFound", "host.certificate.unusable"];

    private static readonly string[] Outside = ["publish_failed", "admin.internal_hop", "auth.rate_limited", ".setup", "setup.", "auth..required", "Auth.required"];

    private static IEnumerable<(string Name, string Code)> Codes()
    {
        foreach (var area in typeof(ApiCodes).GetNestedTypes(BindingFlags.Public))
        {
            foreach (var code in Constants(area))
            {
                yield return code;
            }
        }

        foreach (var type in (Type[])
        [
            typeof(ConfigErrorCodes),
            typeof(ConfigWarningCodes),
            typeof(IssuanceErrorCodes),
            typeof(SimulationErrorCodes),
            typeof(CertificateErrorCodes),
            typeof(NotificationErrorCodes),
            typeof(DraftErrorCodes),
        ])
        {
            foreach (var code in Constants(type))
            {
                yield return code;
            }
        }

        // Constants that live on a class that is not a code class.
        yield return (nameof(ConfigService.MessageInvalidCode), ConfigService.MessageInvalidCode);
        yield return (nameof(ConfigService.StoreUnavailableCode), ConfigService.StoreUnavailableCode);
        yield return (nameof(ConfigService.CorruptRevisionCode), ConfigService.CorruptRevisionCode);
        yield return (nameof(CanonicalConfigJson.InvalidJsonCode), CanonicalConfigJson.InvalidJsonCode);
    }

    private static IEnumerable<(string Name, string Code)> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => ($"{type.Name}.{field.Name}", (string)field.GetRawConstantValue()!));
}
