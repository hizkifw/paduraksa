using Microsoft.CodeAnalysis;

namespace Paduraksa.Ids.Generator;

/// <summary>Diagnostics reported by <see cref="TypedIdGenerator"/>.</summary>
public static class Diagnostics
{
    private const string Category = "Paduraksa.TypedId";

    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "PDKID001",
        title: "Typed ID must be partial",
        messageFormat: "Typed ID '{0}' must be declared 'partial' so the generator can add its members",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotReadonlyRecordStruct = new(
        id: "PDKID002",
        title: "Typed ID must be a readonly record struct",
        messageFormat: "Typed ID '{0}' must be a top-level, non-generic 'readonly partial record struct' without a parameter list",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidPrefix = new(
        id: "PDKID003",
        title: "Invalid typed ID prefix",
        messageFormat: "Typed ID prefix '{0}' is invalid: it must match ^[a-z][a-z0-9]{{1,7}}$",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicatePrefix = new(
        id: "PDKID004",
        title: "Duplicate typed ID prefix",
        messageFormat: "Typed ID prefix '{0}' is also used by '{1}'; prefixes must be unique",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidKind = new(
        id: "PDKID005",
        title: "Unknown typed ID kind",
        messageFormat: "Typed ID '{0}' uses unknown IdKind value '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
