; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PDKID001 | Paduraksa.TypedId | Error | Typed ID must be partial
PDKID002 | Paduraksa.TypedId | Error | Typed ID must be a readonly record struct
PDKID003 | Paduraksa.TypedId | Error | Invalid typed ID prefix
PDKID004 | Paduraksa.TypedId | Error | Duplicate typed ID prefix
PDKID005 | Paduraksa.TypedId | Error | Unknown typed ID kind
PDKID006 | Paduraksa.TypedId | Error | File-local typed IDs are not supported
