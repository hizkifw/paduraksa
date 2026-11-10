using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// A rate-limit policy as its form edits it: name, notes, requests per period, burst and the key a bucket belongs to.
/// A new policy gets a fresh typed ID, and the version the form opened with makes a concurrent edit a conflict.
/// </summary>
public sealed class RateLimitPolicyEditModel
{
    private readonly RateLimitPolicyConfig? _original;

    private RateLimitPolicyEditModel(RateLimitPolicyConfig? original, RateLimitPolicyId id, RateLimitPolicyConfig? source = null)
    {
        _original = original;
        Id = id;
        source ??= original;
        Name = source?.Name ?? string.Empty;
        Notes = source?.Notes;
        Requests = source?.Requests ?? DefaultRequests;
        Per = source?.Per ?? RateLimitPeriod.Minute;
        Burst = source?.Burst ?? DefaultBurst;
        Key = source?.Key ?? RateLimitKey.ClientIp;
        Header = source?.Header;
    }

    /// <summary>The contract's defaults, so a new policy is a usable one before a field is typed.</summary>
    public const int DefaultRequests = 60;

    public const int DefaultBurst = 60;

    public RateLimitPolicyId Id { get; }

    public bool IsNew => _original is null;

    /// <summary>The version the form was opened with (0 for a new policy).</summary>
    public long Version => _original?.Version ?? 0;

    public string Name { get; set; }

    public string? Notes { get; set; }

    public int Requests { get; set; }

    public RateLimitPeriod Per { get; set; }

    public int Burst { get; set; }

    public RateLimitKey Key { get; set; }

    /// <summary>The header whose value is the key; only sent with <see cref="RateLimitKey.Header"/>.</summary>
    public string? Header { get; set; }

    public static RateLimitPolicyEditModel New() => new(null, RateLimitPolicyId.New());

    public static RateLimitPolicyEditModel From(RateLimitPolicyConfig policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new RateLimitPolicyEditModel(policy, policy.Id);
    }

    /// <summary>A form for a policy made earlier and not applied yet (staged in a host's draft): still new, with what it was given.</summary>
    public static RateLimitPolicyEditModel FromDraft(RateLimitPolicyConfig policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new RateLimitPolicyEditModel(null, policy.Id, policy);
    }

    /// <summary>
    /// The policy as the command sends it. A header left over from another key is dropped: the form only shows it for
    /// <see cref="RateLimitKey.Header"/>, and the validator refuses one anywhere else.
    /// </summary>
    public RateLimitPolicyConfig ToConfig() => new(
        Id,
        Name.Trim(),
        Version,
        Requests,
        Per,
        Burst,
        Key,
        Key == RateLimitKey.Header && !string.IsNullOrWhiteSpace(Header) ? Header.Trim() : null,
        string.IsNullOrWhiteSpace(Notes) ? null : Notes);
}
