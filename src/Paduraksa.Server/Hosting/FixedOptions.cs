using Microsoft.Extensions.Options;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Options that are bound and validated once per process and never change (bootstrap settings like listen ports).
/// Serves the same instance through every options interface; named options don't exist and fail loudly.
/// </summary>
internal sealed class FixedOptions<TOptions>(TOptions value) : IOptions<TOptions>, IOptionsMonitor<TOptions>, IOptionsSnapshot<TOptions>
    where TOptions : class
{
    public TOptions Value { get; } = value;

    public TOptions CurrentValue => Value;

    public TOptions Get(string? name) =>
        string.IsNullOrEmpty(name) || name == Options.DefaultName
            ? Value
            : throw new InvalidOperationException($"{typeof(TOptions).Name} has no named instances (requested '{name}').");

    /// <summary>Never fires: the value can't change at runtime.</summary>
    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
