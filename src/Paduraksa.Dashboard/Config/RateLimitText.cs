using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// A rate-limit policy in the dashboard's words: one line for a list, the short rate a picker's inherit choice carries,
/// and the effect of the numbers on a client. The policy itself is never named by ID.
/// </summary>
public static class RateLimitText
{
    /// <summary>The policy as a list row reads it: "60 per minute, bursts of 20, per client IP".</summary>
    public static string Summary(RateLimitPolicyConfig policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return $"{policy.Requests} per {PeriodWord(policy.Per)}, bursts of {policy.Burst}, {KeyText(policy.Key, policy.Header)}";
    }

    /// <summary>The rate alone, compact enough for an inherit choice: "10/s", "60/min", "100/h".</summary>
    public static string Rate(RateLimitPolicyConfig policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return $"{policy.Requests}/{PeriodUnit(policy.Per)}";
    }

    public static string PeriodWord(RateLimitPeriod period) => period switch
    {
        RateLimitPeriod.Second => "second",
        RateLimitPeriod.Hour => "hour",
        _ => "minute",
    };

    /// <summary>What one bucket belongs to: "per client IP", "per client IP and path", "per X-Api-Key header".</summary>
    public static string KeyText(RateLimitKey key, string? header) => key switch
    {
        RateLimitKey.ClientIpAndPath => "per client IP and path",
        RateLimitKey.Header when !string.IsNullOrWhiteSpace(header) => $"per {header.Trim()} header",
        RateLimitKey.Header => "per header",
        _ => "per client IP",
    };

    /// <summary>
    /// What a layer would inherit, in the words of the picker's first choice: "no limit", or the policy and its rate
    /// ("Strict API, 10/s"). A switch that is off is no limit whatever policy is still remembered under it.
    /// </summary>
    public static string Inherited(RateLimitSettings? inherited, IReadOnlyList<RateLimitPolicyConfig> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (inherited is null || inherited.Enabled == false || inherited.PolicyId is not { IsEmpty: false } id)
        {
            return "no limit";
        }

        return policies.FirstOrDefault(policy => policy.Id == id) is { } named
            ? $"{named.Name}, {Rate(named)}"
            : "a policy that isn't here";
    }

    /// <summary>
    /// One line about what the numbers do: "A client gets 20 requests at once, then 1 a second." A rate that a period
    /// doesn't divide into whole seconds is said per period, since "0.02 a second" helps nobody.
    /// </summary>
    public static string Effect(int requests, RateLimitPeriod period, int burst)
    {
        var seconds = period switch
        {
            RateLimitPeriod.Second => 1,
            RateLimitPeriod.Hour => 3600,
            _ => 60,
        };
        var rate = requests % seconds == 0
            ? $"{requests / seconds} a second"
            : period switch
            {
                RateLimitPeriod.Hour => $"{requests} an hour",
                RateLimitPeriod.Second => $"{requests} a second",
                _ => $"{requests} a minute",
            };
        return $"A client gets {burst} {(burst == 1 ? "request" : "requests")} at once, then {rate}.";
    }

    private static string PeriodUnit(RateLimitPeriod period) => period switch
    {
        RateLimitPeriod.Second => "s",
        RateLimitPeriod.Hour => "h",
        _ => "min",
    };
}
