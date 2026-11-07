namespace Paduraksa.Contracts.Config;

/// <summary>
/// Something about a config that compiles but probably isn't what the admin wants: a host without a certificate that
/// covers it, a binding to a certificate that expired. Surfaced next to the result (the dashboard's "hosts without
/// coverage"), never blocks a change on its own.
/// </summary>
/// <param name="Path">JSON pointer into the document (<c>/hosts/3</c>), like <see cref="ConfigError.Path"/>.</param>
/// <param name="Code">Stable, dotted identifier, e.g. <c>certificate.auto.noCoverage</c>.</param>
/// <param name="Message">Human-readable explanation; wording may change.</param>
public sealed record ConfigWarning(string Path, string Code, string Message);
