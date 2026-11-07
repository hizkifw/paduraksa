namespace Paduraksa.Application.Issuance;

/// <summary>
/// Tunables of ACME issuance and the renewal scheduler (PLAN §9.3). The defaults suit a public CA. A record, so a
/// variation is <c>options with { ... }</c>.
/// </summary>
public sealed record AcmeIssuanceOptions
{
    /// <summary>
    /// How long to wait after publishing DNS-01 TXT records before telling the CA to validate, when no <c>IDnsPropagationCheck</c> is
    /// registered or the check cannot be made (no name servers found, none answering). With a working check the wait is however
    /// long the name servers take, bounded by the check's own timeout, and this is not used.
    /// </summary>
    public TimeSpan DnsPropagationDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the scheduler looks for work.</summary>
    public TimeSpan SchedulerPollInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long after the process starts the scheduler's first look waits, so the config store, the vault and the listeners are up before it resumes or renews anything.</summary>
    public TimeSpan SchedulerStartDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Without ARI, renewal starts when this fraction of the certificate's lifetime is left (a third: 30 days of a 90-day
    /// certificate, 2 days of a 6-day one).
    /// </summary>
    public double RenewalRemainingFraction { get; init; } = 1d / 3d;

    /// <summary>
    /// The renewal start is moved earlier by a random share of up to this fraction of the remaining-lifetime fraction,
    /// so certificates issued together do not all renew at the same moment.
    /// </summary>
    public double RenewalJitterFraction { get; init; } = 0.1;

    /// <summary>When ARI is asked again if the CA sent no <c>Retry-After</c> (RFC 9773 asks for at least every six hours).</summary>
    public TimeSpan AriDefaultRecheck { get; init; } = TimeSpan.FromHours(6);

    /// <summary>The shortest wait before ARI is asked again: a tiny <c>Retry-After</c> does not make the scheduler hammer the CA.</summary>
    public TimeSpan AriMinRecheck { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest wait before ARI is asked again, so a window the CA moves is noticed within a day.</summary>
    public TimeSpan AriMaxRecheck { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The first backoff after a transient failure (the CA or network had trouble); it doubles with each failure in a row.</summary>
    public TimeSpan TransientBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest backoff after transient failures.</summary>
    public TimeSpan TransientBackoffCap { get; init; } = TimeSpan.FromHours(6);

    /// <summary>The first backoff after a failure retrying cannot fix without a change (a rejected name, failed validation). A retry still happens, because the cause may be outside Paduraksa (a DNS record added later), but not often: failed validations count against CA limits.</summary>
    public TimeSpan PermanentBackoff { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The longest backoff after permanent failures.</summary>
    public TimeSpan PermanentBackoffCap { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The first backoff after the CA answered something that is not valid ACME.</summary>
    public TimeSpan ProtocolBackoff { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The longest backoff after protocol violations.</summary>
    public TimeSpan ProtocolBackoffCap { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The first backoff after a rate limit when the CA sent no <c>Retry-After</c>.</summary>
    public TimeSpan RateLimitBackoff { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The longest wait for a rate limit, whatever <c>Retry-After</c> asked for (Let's Encrypt limits reset within a week).</summary>
    public TimeSpan RateLimitBackoffCap { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Each backoff is stretched or shrunk by a random share of up to this fraction (0.2 is plus or minus 20 percent), except a rate limit's own <c>Retry-After</c>, which is a floor.</summary>
    public double BackoffJitterFraction { get; init; } = 0.2;
}
