using System.Text.Json;

namespace BimSAgentApp.Rag;

public sealed class ContextAssembler(RagTokenizer tokenizer)
{
    public RetrievalSelection AssembleLocal(RetrievalSelection retrieval, int budget, string question, int maxChunks)
    {
        maxChunks = Math.Clamp(maxChunks, 1, 3);
        var result = new Dictionary<string, RetrievalHit>();
        var groups = new Dictionary<string, int>();
        var count = 0;
        var terms = SearchLanguage.RetrievalTokens(question).Distinct().ToHashSet();
        var exact = retrieval.Candidates.Any(h => h.ExactFamilyMatch);
        // Only top-K/family-limit reserves have already passed relevance, conflict and duplicate filters.
        foreach (var original in retrieval.Candidates.OrderByDescending(h => h.Selected))
        {
            if (!original.Selected && original.SelectionReason != "family_limit")
            { result[original.Chunk.ChunkId] = original; continue; }
            var group = original.Chunk.OwnerFamilyId ?? (original.Chunk.FamilyNames.Length == 1
                ? original.Chunk.FamilyNames[0] : "general:" + original.Chunk.Section);
            if (count >= maxChunks || groups.GetValueOrDefault(group) >= (exact ? 8 : 2))
            { result[original.Chunk.ChunkId] = original with { Selected = false, SelectionReason = "family_limit" }; continue; }
            var hit = original with { Chunk = original.Chunk with
                { FamilyPurpose = "", Tables = [], InheritedContext = [], VisualEvidence = [] } };
            var fullCost = tokenizer.Count(JsonSerializer.Serialize(LocalFragment(hit), JsonIndexStore.JsonOptions)) + 4;
            if (fullCost > Math.Min(budget, 360))
            {
                var empty = hit with { Chunk = hit.Chunk with { Text = "" } };
                var overhead = tokenizer.Count(JsonSerializer.Serialize(LocalFragment(empty), JsonIndexStore.JsonOptions)) + 8;
                var size = Math.Min(320, budget - overhead);
                if (size < 32)
                { result[hit.Chunk.ChunkId] = hit with { Selected = false, SelectionReason = "token_budget" }; continue; }
                var window = tokenizer.Windows(hit.Chunk.Text, size, Math.Min(48, size / 4))
                    .Select(w => new { Window = w, Terms = SearchLanguage.RetrievalTokens(hit.Chunk.Text[w.Start..w.End]) })
                    .OrderByDescending(w => w.Terms.Distinct().Count(terms.Contains))
                    .ThenByDescending(w => w.Terms.Count(terms.Contains)).ThenBy(w => w.Window.Start).First().Window;
                hit = hit with { Chunk = hit.Chunk with
                {
                    Text = hit.Chunk.Text[window.Start..window.End], TokenCount = tokenizer.Count(hit.Chunk.Text[window.Start..window.End]),
                    // Only the text actually sent to Ollama is evidence on this path.
                    FamilyPurpose = "", Tables = [], InheritedContext = [], VisualEvidence = [],
                    Spans = hit.Chunk.Spans.Where(s => s.Start < window.End && s.End > window.Start).Select(s => s with
                    { Start = Math.Max(s.Start, window.Start) - window.Start, End = Math.Min(s.End, window.End) - window.Start,
                        SourceStart = s.SourceStart + Math.Max(0, window.Start - s.Start),
                        SourceEnd = s.SourceEnd - Math.Max(0, s.End - window.End) }).ToArray()
                }, MatchedPassage = hit.Chunk.Text[window.Start..window.End] };
            }
            var cost = tokenizer.Count(JsonSerializer.Serialize(LocalFragment(hit), JsonIndexStore.JsonOptions)) + 4;
            if (cost > budget)
            { result[hit.Chunk.ChunkId] = hit with { Selected = false, SelectionReason = "token_budget" }; continue; }
            budget -= cost;
            count++;
            groups[group] = groups.GetValueOrDefault(group) + 1;
            result[hit.Chunk.ChunkId] = hit with { Selected = true,
                SelectionReason = hit.MatchedPassage == null ? "selected" : "selected_excerpt" };
        }
        return retrieval with { Candidates = retrieval.Candidates.Select(h => result[h.Chunk.ChunkId]).ToArray() };
    }

    public static object LocalFragment(RetrievalHit hit) => new
    {
        text = hit.Chunk.Text,
        section = hit.Chunk.Section[..Math.Min(240, hit.Chunk.Section.Length)]
    };

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
        tables = hit.Chunk.Tables,
        inherited_context = hit.Chunk.InheritedContext.Select(b => new { block_id = b.BlockId, text = b.Text, kind = b.Kind, warnings = b.Warnings }),
        // Full per-character ownership ranges remain in the persisted chunk. Sending them
        // to the generator can consume the context budget before a second family fits.
        warnings = hit.Chunk.Warnings, asset_ids = hit.Chunk.AssetIds,
        visual_evidence = hit.Chunk.VisualEvidence,
        family_source_warnings = hit.Chunk.FamilyWarnings,
        image_notice = hit.Chunk.AssetIds.Length > 0 ? "Используй только явно приведённые OCR/vision-данные; наличие asset_id само по себе не подтверждает внешний вид." : null
    };
}
