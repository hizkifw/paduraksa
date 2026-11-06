using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Paduraksa.Ids.Generator;

/// <summary>
/// Generates typed-ID members for every <c>[TypedId]</c> <c>readonly partial record struct</c>. The generated code
/// depends on runtime helpers in <c>Paduraksa.Core.Ids</c> (Base62, ITypedId, converters), so any project that
/// declares IDs must reference Paduraksa.Core.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TypedIdGenerator : IIncrementalGenerator
{
    public const string AttributeMetadataName = "Paduraksa.Core.Ids.TypedIdAttribute";

    /// <summary>Tracking name of the location-free model step (for incremental caching tests).</summary>
    public const string ModelsTrackingName = "TypedIdModels";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Candidate> candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, ct) => Transform(ctx, ct));

        // Location-free model: unchanged by edits that only move the declaration, so emission stays cached.
        IncrementalValuesProvider<TypedIdModel> models = candidates
            .Select(static (c, _) => c.Model)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .WithTrackingName(ModelsTrackingName);

        context.RegisterSourceOutput(models, static (spc, model) =>
            spc.AddSource(model.MetadataName + ".TypedId.g.cs", SourceEmitter.Emit(model)));

        IncrementalValuesProvider<EquatableArray<DiagnosticInfo>> diagnostics = candidates
            .Select(static (c, _) => c.Diagnostics)
            .Where(static d => d.Length > 0);

        context.RegisterSourceOutput(diagnostics, static (spc, items) =>
        {
            foreach (DiagnosticInfo diagnostic in items)
            {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }
        });

        IncrementalValueProvider<ImmutableArray<PrefixUse>> prefixes = candidates
            .Where(static c => c.Model is not null)
            .Select(static (c, _) => new PrefixUse(c.Model!.Prefix, c.Model.MetadataName, c.Location))
            .Collect();

        context.RegisterSourceOutput(prefixes, static (spc, uses) => ReportDuplicatePrefixes(spc, uses));
    }

    internal static bool IsValidPrefix(string? prefix)
    {
        if (prefix is null || prefix.Length < 2 || prefix.Length > 8 || prefix[0] is < 'a' or > 'z')
        {
            return false;
        }

        for (int i = 1; i < prefix.Length; i++)
        {
            if (prefix[i] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    private static Candidate Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var node = (TypeDeclarationSyntax)ctx.TargetNode;
        LocationInfo? location = LocationInfo.From(node.Identifier.GetLocation());
        var diagnostics = new List<DiagnosticInfo>();

        if (!node.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.NotPartial, location, symbol.Name));
        }

        bool isReadonlyRecordStruct = node is RecordDeclarationSyntax record
            && record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
            && record.ParameterList is null
            && symbol.IsReadOnly
            && symbol.TypeParameters.IsEmpty
            && symbol.ContainingType is null;
        if (!isReadonlyRecordStruct)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.NotReadonlyRecordStruct, location, symbol.Name));
        }

        if (symbol.IsFileLocal)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.FileLocalNotSupported, location, symbol.Name));
        }

        AttributeData attribute = ctx.Attributes[0];
        ImmutableArray<TypedConstant> args = attribute.ConstructorArguments;
        string? prefix = args.Length > 0 ? args[0].Value as string : null;
        int kind = args.Length > 1 && args[1].Value is int k ? k : (int)IdKindValue.UuidV7;

        if (!IsValidPrefix(prefix))
        {
            LocationInfo? attributeLocation = LocationInfo.From(
                attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation()) ?? location;
            diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidPrefix, attributeLocation, prefix ?? "null"));
        }

        if (!Enum.IsDefined(typeof(IdKindValue), kind))
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidKind, location, symbol.Name, kind.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        TypedIdModel? model = null;
        if (diagnostics.Count == 0)
        {
            var parts = new List<string>();
            for (INamespaceSymbol n = symbol.ContainingNamespace; !n.IsGlobalNamespace; n = n.ContainingNamespace)
            {
                parts.Insert(0, n.Name);
            }

            string? ns = parts.Count == 0 ? null : string.Join(".", parts);
            string? escapedNs = parts.Count == 0 ? null : string.Join(".", parts.Select(Escape));
            model = new TypedIdModel(
                escapedNs,
                Escape(symbol.Name),
                ns is null ? symbol.Name : $"{ns}.{symbol.Name}",
                symbol.Name,
                prefix!,
                (IdKindValue)kind);
        }

        return new Candidate(model, location, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
    }

    private static void ReportDuplicatePrefixes(SourceProductionContext spc, ImmutableArray<PrefixUse> uses)
    {
        foreach (IGrouping<string, PrefixUse> group in uses.GroupBy(static u => u.Prefix, StringComparer.Ordinal))
        {
            PrefixUse[] all = [.. group.OrderBy(static u => u.FullName, StringComparer.Ordinal)];
            if (all.Length < 2)
            {
                continue;
            }

            foreach (PrefixUse use in all)
            {
                string others = string.Join(", ", all.Where(o => !ReferenceEquals(o, use)).Select(static o => o.FullName));
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicatePrefix, use.Location?.ToLocation(), use.Prefix, others));
            }
        }
    }

    // Symbol names are unescaped ("class"); emitted code needs "@class".
    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;
}
