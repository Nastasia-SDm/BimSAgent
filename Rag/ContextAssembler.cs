using System.Text.Json;

namespace BimSAgentApp.Rag;

public sealed class ContextAssembler(RagTokenizer tokenizer)
{
    public RetrievalSelection Assemble(RetrievalSelection retrieval, int budget, RagIndex? index = null)
    {
        var candidates = new List<RetrievalHit>();
        foreach (var original in retrieval.Candidates)
        {
            var hit = original;
            if (index != null && hit.Selected)
            {
                var blocks = index.Blocks.Where(b => hit.Chunk.BlockOrdinals.Contains(b.Ordinal)).ToArray();
                var tables = blocks.Where(b => b.TableId != null).GroupBy(b => b.TableId!)
                    .Select(g => new TableContext(g.Key, g.First().TableHeaders,
                        index.Blocks.First(b => b.TableId == g.Key).BlockId, true)).ToArray();
                var previous = blocks.Length == 0 ? null : index.Blocks.LastOrDefault(b => b.Ordinal < blocks[0].Ordinal);
                var inherit = previous != null && previous.OwnerFamilyId == hit.Chunk.OwnerFamilyId && previous.Text.Length < 200
                    && (previous.Kind == "heading" || previous.Text.TrimEnd().EndsWith(':') || System.Text.RegularExpressions.Regex.IsMatch(previous.Text.Trim(), @"^[\p{L}\p{N}_]+_[\p{L}\p{N}_]+$"));
                hit = hit with { Chunk = hit.Chunk with { Tables = tables, InheritedContext = inherit ? [previous!] : [],
                    FamilyWarnings = index.Families.FirstOrDefault(f => f.FamilyId == hit.Chunk.OwnerFamilyId)?.Warnings ?? [] } };
            }
            if (!hit.Selected) { candidates.Add(hit); continue; }
            var count = tokenizer.Count(JsonSerializer.Serialize(Fragment(hit), JsonIndexStore.JsonOptions)) + 4;
            if (count > budget) candidates.Add(hit with { Selected = false, SelectionReason = "token_budget" });
            else { budget -= count; candidates.Add(hit); }
        }
        return retrieval with { Candidates = candidates };
    }
    public static object Fragment(RetrievalHit hit) => new
    {
        source = hit.Chunk.Source, section = hit.Chunk.Section, chunk_id = hit.Chunk.ChunkId,
        text = hit.Chunk.Text, family_names = hit.Chunk.FamilyNames, owner_family_id = hit.Chunk.OwnerFamilyId,
        family_purpose = hit.Chunk.FamilyPurpose, purpose_source_blocks = hit.Chunk.PurposeBlockIds,
        tables = hit.Chunk.Tables, inherited_context = hit.Chunk.InheritedContext,
        ownership_ranges = hit.Chunk.Spans,
        warnings = hit.Chunk.Warnings, asset_ids = hit.Chunk.AssetIds,
        family_source_warnings = hit.Chunk.FamilyWarnings,
        image_notice = hit.Chunk.AssetIds.Length > 0 ? "Изображения могут быть не обработаны; не восстанавливай отсутствующие сведения." : null
    };
}
