namespace BimSAgentApp.Rag;

// Compatibility for saved indexes whose extractor treated a multi-family heading as an article.
// All names and purpose text come from persisted source blocks, never from a built-in catalogue.
public static class SharedFamilySections
{
    public static RagIndex Enrich(RagIndex index)
    {
        var groups = index.Blocks.Where(b => b.Kind == "heading" && FamilyIdentity.HeadingNames(b.Text).Length > 1)
            .GroupBy(b => b.Section).ToDictionary(g => g.Key, g => g.First());
        if (groups.Count == 0) return index;
        var families = index.Families.ToList();
        var blocks = index.Blocks.Select(b => groups.TryGetValue(b.Section, out var heading) && b.OwnerFamilyId == null
            ? b with { OwnerFamilyId = FamilyIdentity.Id(index.Source, heading.Text.Trim()),
                FamilyNames = FamilyIdentity.HeadingNames(heading.Text), Classification = "shared_family_content" } : b).ToArray();
        foreach (var heading in groups.Values)
        {
            var id = FamilyIdentity.Id(index.Source, heading.Text.Trim());
            if (families.Any(f => f.FamilyId == id)) continue;
            var owned = blocks.Where(b => b.OwnerFamilyId == id).ToArray();
            var purpose = owned.Where(b => b.Warnings.Length == 0 && System.Text.RegularExpressions.Regex.IsMatch(b.Text,
                @"(?:используют для|предназначен|семейство содержит|позволяет моделировать)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            families.Add(new(id, heading.Text.Trim(), [], string.Join("\n", purpose.Select(b => b.Text)), "",
                owned.Select(b => b.Ordinal).ToArray(), "shared", [])
                { MemberNames = FamilyIdentity.HeadingNames(heading.Text), AssetIds = owned.SelectMany(b => b.AssetIds).Distinct().ToArray() });
        }
        return index with
        {
            Blocks = blocks, Families = families.ToArray(),
            Assets = index.Assets.Select(a => a.OwnerFamilyId != null ? a : a with
                { OwnerFamilyId = blocks.FirstOrDefault(b => b.AssetIds.Contains(a.AssetId))?.OwnerFamilyId }).ToArray(),
            Chunks = index.Chunks.Select(c =>
            {
                if (c.OwnerFamilyId != null || !groups.TryGetValue(c.Section, out var heading)) return c;
                var family = families.Single(f => f.FamilyId == FamilyIdentity.Id(index.Source, heading.Text.Trim()));
                return c with { OwnerFamilyId = family.FamilyId, FamilyNames = family.MemberNames,
                    FamilyPurpose = family.Purpose, PurposeBlockIds = blocks.Where(b => b.OwnerFamilyId == family.FamilyId
                        && b.Warnings.Length == 0 && family.Purpose.Contains(b.Text, StringComparison.Ordinal)).Select(b => b.BlockId).ToArray(),
                    Spans = c.Spans.Select(s => s with { OwnerFamilyId = family.FamilyId }).ToArray() };
            }).ToArray()
        };
    }
}
