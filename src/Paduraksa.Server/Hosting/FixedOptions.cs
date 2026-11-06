using Microsoft.Extensions.Options;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Options that are bound and validated once per process and never change (bootstrap settings like listen ports).
/// Serves the same instance through every options interface, including <see cref="IOptionsFactory{TOptions}"/>:
/// left to the default factory, anything resolving it would silently build an unvalidated copy from configuration.
/// Named options don't exist and fail loudly.
/// </summary>
internal sealed class FixedOptions<TOptions> : IOptions<TOptions>, IOptionsMonitor<TOptions>, IOptionsSnapshot<TOptions>, IOptionsFactory<TOptions>
    where TOptions : class
{
    public FixedOptions(TOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public TOptions Value { get; }

    public TOptions CurrentValue => Value;

    public TOptions Get(string? name) =>
        string.IsNullOrEmpty(name) || name == Options.DefaultName
            ? Value
            : throw new InvalidOperationException($"{typeof(TOptions).Name} has no named instances (requested '{name}').");

    /// <summary>Returns the one validated instance for the default name; throws for any other, like <see cref="Get"/>.</summary>
    public TOptions Create(string name) => Get(name);

    /// <summary>Never fires: the value can't change at runtime.</summary>
    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
