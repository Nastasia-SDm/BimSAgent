using System.Text.Json;

namespace BimSAgentApp.Rag;

public sealed class ContextAssembler(RagTokenizer tokenizer)
{
    public RetrievalSelection Assemble(RetrievalSelection retrieval, int budget)
    {
        var candidates = new List<RetrievalHit>();
        foreach (var hit in retrieval.Candidates)
        {
            if (!hit.Selected) { candidates.Add(hit); continue; }
            var count = tokenizer.Count(JsonSerializer.Serialize(Fragment(hit))) + 4;
            if (count > budget) candidates.Add(hit with { Selected = false, SelectionReason = "token_budget" });
            else { budget -= count; candidates.Add(hit); }
        }
        return retrieval with { Candidates = candidates };
    }
    public static object Fragment(RetrievalHit hit) => new
    {
        source = hit.Chunk.Source, section = hit.Chunk.Section, chunk_id = hit.Chunk.ChunkId,
        text = hit.Chunk.Text, family_names = hit.Chunk.FamilyNames, owner_family_id = hit.Chunk.OwnerFamilyId,
        warnings = hit.Chunk.Warnings, asset_ids = hit.Chunk.AssetIds,
        image_notice = hit.Chunk.AssetIds.Length > 0 ? "Изображения могут быть не обработаны; не восстанавливай отсутствующие сведения." : null
    };
}
