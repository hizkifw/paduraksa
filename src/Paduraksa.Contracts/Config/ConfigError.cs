namespace Paduraksa.Contracts.Config;

/// <summary>A validation or compile error in a config document (PLAN §6.4).</summary>
/// <param name="Path">JSON pointer (RFC 6901) into the document, e.g. <c>/hosts/3/routes/0/path</c>; <c>""</c> is the
/// whole document. Indices count in the document as validated: as the client sent it, before canonicalisation;
/// validation and compile errors both point at what the client wrote, and an object command's errors are relative to
/// the object it sent.</param>
/// <param name="Code">Stable, dotted identifier of the rule, e.g. <c>route.path.notCanonical</c>.</param>
/// <param name="Message">Human-readable explanation; wording may change.</param>
public sealed record ConfigError(string Path, string Code, string Message);
