using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

// Names are identities, not natural-language tokens. Never split at № or stem identifiers.
public static class FamilyIdentity
{
    private const string Identifier = @"[\p{L}\p{N}][\p{L}\p{N}_№.+\-]*_[\p{L}\p{N}_№.+\-]+";
    public static string? HeadingName(string text)
    {
        var match = Regex.Match(text.Trim(), "^(" + Identifier + @")(?:\s+\([^\r\n]*\))?$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }
    public static bool Contains(string text, string name) => Regex.IsMatch(text,
        @"(?<![\p{L}\p{N}_№.+\-])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_№+\-]|\.[\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string Id(string source, string name) => RagDefaults.Hash(source + "|family|" + name.ToUpperInvariant());
}

public sealed class FamilyCardBuilder
{
    public ExtractedDocument Build(ExtractedDocument document)
    {
        var registry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in document.Blocks)
        {
            var name = block.Kind == "heading" ? FamilyIdentity.HeadingName(block.Text) : null;
            if (name != null) registry.TryAdd(name, FamilyIdentity.Id(document.Source, name));
            if (block.Kind.StartsWith("table-row", StringComparison.Ordinal))
                foreach (var candidate in block.FamilyNames)
                    if (FamilyIdentity.HeadingName(candidate) is { } tableName)
                        registry.TryAdd(tableName, FamilyIdentity.Id(document.Source, tableName));
        }
        var blocks = new List<DocumentBlock>();
        string? owner = null, ownerName = null;
        var ownerLevel = int.MaxValue;
        foreach (var block in document.Blocks)
        {
            if (block.Kind == "heading")
            {
                var name = FamilyIdentity.HeadingName(block.Text);
                if (name != null && registry.TryGetValue(name, out var id))
                { owner = id; ownerName = name; ownerLevel = block.HeadingLevel ?? 0; }
                else if ((block.HeadingLevel ?? 0) <= ownerLevel)
                { owner = null; ownerName = null; ownerLevel = int.MaxValue; }
            }
            var localOwner = owner;
            var localName = ownerName;
            // A labelled table row is a card in its own right, not a change to following paragraphs.
            if (block.Kind.StartsWith("table-row", StringComparison.Ordinal) && block.FamilyNames.Length == 1
                && registry.TryGetValue(block.FamilyNames[0], out var tableOwner))
            { localOwner = tableOwner; localName = block.FamilyNames[0]; }
            var references = registry.Where(p => p.Value != localOwner && FamilyIdentity.Contains(block.Text, p.Key)).ToArray();
            var conflict = localOwner != null && references.Length > 0
                && Regex.IsMatch(block.Text, @"^В этой статье.*(?:инструмент|семейств)", RegexOptions.IgnoreCase);
            blocks.Add(block with
            {
                OwnerFamilyId = localOwner, FamilyNames = localName == null ? [] : [localName],
                ReferencedFamilyIds = references.Select(p => p.Value).ToArray(),
                Classification = conflict ? "ambiguous" : localOwner == null ? "general_article" : "family_content",
                Warnings = conflict ? ["source_conflict: введение называет другое семейство"] : []
            });
        }
        var cards = registry.Select(pair =>
        {
            var owned = blocks.Where(b => b.OwnerFamilyId == pair.Value).ToArray();
            var purpose = owned.Where(b => b.Warnings.Length == 0 && Regex.IsMatch(b.Text,
                @"(?:используют для|предназначен|семейство содержит|позволяет моделировать)", RegexOptions.IgnoreCase))
                .Select(b => b.Text).Distinct().ToArray();
            var constraints = owned.Where(b => b.Warnings.Length == 0 && Regex.IsMatch(b.Text,
                @"(?:нельзя|не допускается|ограничени|только для)", RegexOptions.IgnoreCase)).Select(b => b.Text).ToArray();
            var warnings = owned.SelectMany(b => b.Warnings).Distinct().ToArray();
            return new FamilyCard(pair.Value, pair.Key, [], string.Join("\n", purpose), string.Join("\n", constraints),
                owned.Select(b => b.Ordinal).ToArray(), warnings.Length == 0 ? "identified" : "conflict", warnings)
            {
                AssetIds = owned.SelectMany(b => b.AssetIds).Distinct().ToArray(),
                ReferencedFamilyIds = owned.SelectMany(b => b.ReferencedFamilyIds).Distinct().ToArray()
            };
        }).ToArray();
        var assets = document.Assets.Select(a => a with
        { OwnerFamilyId = blocks.FirstOrDefault(b => b.AssetIds.Contains(a.AssetId))?.OwnerFamilyId }).ToArray();
        return document with { Blocks = blocks, Families = cards, Assets = assets,
            Warnings = DocumentIntegrityValidator.Validate(blocks, cards, assets) };
    }
}

public static class DocumentIntegrityValidator
{
    public static string[] Validate(IReadOnlyList<DocumentBlock> blocks, FamilyCard[] families, DocumentAsset[] assets)
    {
        var warnings = blocks.SelectMany(b => b.Warnings.Select(w => b.BlockId + ": " + w)).ToList();
        warnings.AddRange(assets.Where(a => a.Status != "processed").Select(a => a.AssetId + ": image_" + a.Status));
        warnings.AddRange(families.Where(f => f.BlockOrdinals.Length == 0).Select(f => f.FamilyId + ": empty_card"));
        return warnings.ToArray();
    }
}
