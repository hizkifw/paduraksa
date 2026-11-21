using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Proxy.Config;

/// <summary>A host's trap: its site, derived from the proof-of-work key and the host's ID.</summary>
public static partial class SnapshotCompiler
{
    private static ReadOnlySpan<byte> SiteKeyPrefix => "paduraksa-honeypot-site-v1"u8;

    internal sealed partial class CompileContext
    {
        public HoneypotRuntime HoneypotRuntime => Options.Honeypot ?? HoneypotRuntime.Default;

        /// <summary>
        /// The trap of <paramref name="spec"/>, or null when the host serves none. The site key is HMAC-SHA-256 over a fixed prefix and
        /// the host's ID under the proof-of-work key, so the same host keeps its prefix and pages for as long as that key does.
        /// </summary>
        public CompiledHoneypot? CompileHoneypot(HostSpec spec, CompileErrorSource where)
        {
            var settings = spec.Honeypot;
            if (!spec.Enabled || spec.Kind == HostKind.Admin || !settings.Enabled)
            {
                return null;
            }

            CheckHoneypot(settings, where, Errors);
            var runtime = HoneypotRuntime;
            var serveRobotsLine = spec.Robots.Mode != RobotsMode.Passthrough;
            var key = Options.ProofOfWork?.Key;
            if (key is null)
            {
                // A prefix the operator wrote can still be answered, with the 503 a protected route gives; a derived one can't be known.
                runtime.ReportKeyUnavailable();
                return settings.Path is { } configured && HoneypotPrefix.Problem(configured) is null
                    ? new CompiledHoneypot(configured, site: null, settings.NoIndex, settings.PowDifficulty, settings.PowShare, serveRobotsLine, runtime, Options.ProofOfWork ?? ProofOfWorkRuntime.Unavailable)
                    : null;
            }

            Span<byte> siteKey = stackalloc byte[64];
            if (!TryDeriveSiteKey(key, spec.Id, siteKey, out var length) || length != 32)
            {
                Errors.Add(new(where, "the honeypot site key can't be derived: the proof-of-work key must be an HMAC-SHA-256 key"));
                return null;
            }

            siteKey = siteKey[..length];
            try
            {
                var factory = runtime.SiteFactory;
                var prefix = settings.Path ?? factory.DerivePrefix(siteKey);
                var name = settings.SiteName ?? factory.DeriveSiteName(siteKey);
                if (HoneypotPrefix.Problem(prefix) is { } problem)
                {
                    Errors.Add(new(where, problem));
                    return null;
                }

                var site = factory.Create(siteKey, new HoneypotSiteOptions(prefix, name, settings.PowShare));
                if (!string.Equals(site.Prefix, prefix, StringComparison.Ordinal))
                {
                    Errors.Add(new(where, $"the honeypot site's prefix {site.Prefix} isn't the one it was made for, {prefix}"));
                    return null;
                }

                return new CompiledHoneypot(prefix, site, settings.NoIndex, settings.PowDifficulty, settings.PowShare, serveRobotsLine, runtime, Options.ProofOfWork ?? ProofOfWorkRuntime.Unavailable);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
            {
                Errors.Add(new(where, $"the honeypot site can't be built: {ex.Message}"));
                return null;
            }
            finally
            {
                siteKey.Clear();
            }
        }
    }

    /// <summary>
    /// The prefix and title a host with no configured ones gets, for the dashboard to show before a publish. It is what
    /// the compiler derives, from the same key and factory; false when the proof-of-work key isn't open.
    /// </summary>
    public static bool TryDeriveHoneypotNames(SnapshotCompilerOptions options, HostId host, out string prefix, out string siteName)
    {
        ArgumentNullException.ThrowIfNull(options);
        prefix = string.Empty;
        siteName = string.Empty;
        if (options.ProofOfWork?.Key is not { } key)
        {
            return false;
        }

        Span<byte> siteKey = stackalloc byte[64];
        if (!TryDeriveSiteKey(key, host, siteKey, out var length) || length != 32)
        {
            return false;
        }

        siteKey = siteKey[..length];
        try
        {
            var factory = (options.Honeypot ?? HoneypotRuntime.Default).SiteFactory;
            prefix = factory.DerivePrefix(siteKey);
            siteName = factory.DeriveSiteName(siteKey);
            return true;
        }
        finally
        {
            siteKey.Clear();
        }
    }

    internal static bool TryDeriveSiteKey(IMacKey key, HostId host, Span<byte> destination, out int length)
    {
        Span<byte> input = stackalloc byte[SiteKeyPrefix.Length + 16];
        SiteKeyPrefix.CopyTo(input);
        HostScopeOf(host).CopyTo(input[SiteKeyPrefix.Length..]);
        length = key.Sign(input, destination);
        return length >= 32;
    }

    /// <summary>The backstop for what the validator judges: a value out of its bounds fails the compile instead of serving something else.</summary>
    private static void CheckHoneypot(HostHoneypotSpec spec, CompileErrorSource where, List<CompileError> errors)
    {
        if (spec.Path is { } path && HoneypotPrefix.Problem(path) is { } problem)
        {
            errors.Add(new(where, problem));
        }

        if (spec.SiteName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > HoneypotLimits.MaxSiteNameLength))
        {
            errors.Add(new(where, $"the honeypot's blog title is 1-{HoneypotLimits.MaxSiteNameLength} characters"));
        }

        if (spec.PowShare is < HoneypotLimits.MinPowShare or > HoneypotLimits.MaxPowShare)
        {
            errors.Add(new(where, $"the honeypot's proof-of-work share must be {HoneypotLimits.MinPowShare}-{HoneypotLimits.MaxPowShare} percent, got {spec.PowShare}"));
        }

        if (spec.PowDifficulty is < ProofOfWorkLimits.MinDifficulty or > ProofOfWorkLimits.MaxDifficulty)
        {
            errors.Add(new(where, $"the honeypot's proof-of-work difficulty must be {ProofOfWorkLimits.MinDifficulty}-{ProofOfWorkLimits.MaxDifficulty}, got {spec.PowDifficulty}"));
        }
    }
}
