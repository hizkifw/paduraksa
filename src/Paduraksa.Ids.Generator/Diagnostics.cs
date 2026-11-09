using Microsoft.CodeAnalysis;

namespace Paduraksa.Ids.Generator;

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

    // A 'file' type's partial declarations must all live in its own file; a generated partial in another file
    // would declare a different, unrelated type. So the generator can't add members to a file-local type.
    public static readonly DiagnosticDescriptor FileLocalNotSupported = new(
        id: "PDKID006",
        title: "File-local typed IDs are not supported",
        messageFormat: "Typed ID '{0}' is file-local; file-local typed IDs are not supported because generated members can't be added from another file",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
