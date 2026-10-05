using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

// Linked evidence expansion after normal retrieval. Never reuse another turn's evidence.
public sealed class ChatVisualContext(OpenAiRagClient client, string directory, RagTokenizer tokenizer)
{
    public static bool IsAppearance(string question) => Regex.IsMatch(question,
        @"выгляд|внешн\p{L}*\s+вид|форм[аыуе]\b|изображен|картинк|эскиз|визуальн", RegexOptions.IgnoreCase);

    public async Task<RetrievalSelection> ExpandAsync(RagIndex index, RetrievalSelection selection,
        string question, int? topK, CancellationToken token)
    {
        var named = index.Families.Where(f => FamilyIdentity.Contains(question, f.CanonicalName)
            || f.MemberNames.Concat(f.Aliases).Any(a => FamilyIdentity.Contains(question, a))).Select(f => f.FamilyId).Distinct().ToArray();
        var owners = named.Length > 0 ? named : selection.Candidates.Where(h => h.Selected)
            .Select(h => h.Chunk.OwnerFamilyId).OfType<string>().Distinct().ToArray();
        // Ambiguous semantic matches are not authorization to describe an arbitrary family.
        if (owners.Length == 0 || named.Length == 0 && owners.Length != 1) return selection;
        var visual = new List<RetrievalHit>();
        foreach (var owner in owners)
        {
            var family = index.Families.FirstOrDefault(f => f.FamilyId == owner);
            if (family == null || family.Warnings.Any(w => w.StartsWith("source_conflict", StringComparison.Ordinal))) continue;
            var existing = index.Chunks.Where(c => c.OwnerFamilyId == owner && c.VisualEvidence.Length > 0
                && !c.Warnings.Any(w => w.StartsWith("source_conflict", StringComparison.Ordinal)))
                .OrderByDescending(c => selection.Candidates.FirstOrDefault(h => h.Chunk.ChunkId == c.ChunkId)?.RerankScore ?? double.MinValue)
                .ThenBy(c => c.BlockOrdinals.FirstOrDefault()).DistinctBy(c => string.Join("\n", c.VisualEvidence)).Take(2).ToArray();
            foreach (var chunk in existing) visual.Add(Linked(chunk));
            if (existing.Length > 0) continue;

            // Read bytes only for assets whose ownership was established during extraction.
            ZipArchive? archive = null;
            try
            {
                foreach (var asset in index.Assets.Where(a => a.OwnerFamilyId == owner && a.MediaPart != null && a.ContentHash != null)
                    .DistinctBy(a => a.ContentHash))
                {
                    token.ThrowIfCancellationRequested();
                    var version = VisualIndexing.Version;
                    var key = RagDefaults.Hash(index.Source + "|" + index.ContentHash + "|" + owner + "|" + asset.AssetId + "|" + asset.ContentHash + "|" + version);
                    var path = Path.Combine(directory, key + ".json");
                    SavedVisual? saved = null;
                    if (File.Exists(path))
                    {
                        try { saved = JsonSerializer.Deserialize<SavedVisual>(await File.ReadAllTextAsync(path, token), JsonIndexStore.JsonOptions); }
                        catch (JsonException) { }
                    }
                    if (saved?.Version != version || saved.Asset.ContentHash != asset.ContentHash
                        || saved.Asset.AssetId != asset.AssetId || saved.Asset.OwnerFamilyId != owner
                        || saved.SourceHash != index.ContentHash) saved = null;
                    if (saved == null)
                    {
                        var recognized = asset;
                        if (string.IsNullOrWhiteSpace(recognized.VisionText))
                        {
                            if (archive == null)
                            {
                                await using var source = File.OpenRead(index.Source);
                                var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token)).ToLowerInvariant();
                                if (hash != index.ContentHash) throw new InvalidDataException("DOCX изменился. Выполните rag index перед распознаванием изображений.");
                                archive = ZipFile.OpenRead(index.Source);
                            }
                            var entry = archive.GetEntry(asset.MediaPart!);
                            if (entry == null) continue;
                            using var memory = new MemoryStream();
                            await using (var input = entry.Open()) await input.CopyToAsync(memory, token);
                            var bytes = memory.ToArray();
                            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != asset.ContentHash)
                                throw new InvalidDataException("Изображение DOCX не соответствует индексу. Выполните rag index.");
                            recognized = await client.DescribeAssetAsync(asset, bytes, token);
                        }
                        RagChunk? chunk = null;
                        if (!string.IsNullOrWhiteSpace(recognized.VisionText))
                        {
                            var text = "[vision_interpretation: внешний вид по изображению " + asset.MediaPart + "]\n" + recognized.VisionText;
                            chunk = new(index.Source, Path.GetFileName(index.Source), Path.GetFileName(index.Source), family.CanonicalName,
                                RagDefaults.Hash(key + "|" + text), index.Strategy, text, "", [], index.EmbeddingModel,
                                tokenizer.Count(text), family.MemberNames.Length > 0 ? family.MemberNames : [family.CanonicalName], [])
                            {
                                OwnerFamilyId = owner, Kind = "vision_interpretation", AssetIds = [asset.AssetId],
                                Warnings = ["machine_extracted_unverified"], VisualEvidence = [text]
                            };
                        }
                        saved = new(version, index.ContentHash, recognized, chunk);
                        Directory.CreateDirectory(directory);
                        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(saved, JsonIndexStore.JsonOptions), token);
                        File.Move(temporary, path, true);
                    }
                    if (saved.Chunk is { } evidence && evidence.OwnerFamilyId == owner && evidence.AssetIds.Contains(asset.AssetId)
                        && evidence.Text.Contains(saved.Asset.VisionText, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(saved.Asset.VisionText))
                    {
                        visual.Add(Linked(evidence with { Strategy = index.Strategy }));
                        if (visual.Count(h => h.Chunk.OwnerFamilyId == owner) >= 2) break;
                    }
                }
            }
            finally { archive?.Dispose(); }
        }
        if (visual.Count == 0) return selection;
        // Round-robin preserves multiple explicitly requested families. Linked evidence has no fabricated score.
        var ordered = visual.GroupBy(h => h.Chunk.OwnerFamilyId).ToArray();
        var priority = Enumerable.Range(0, 2).SelectMany(n => ordered.Where(g => g.Count() > n).Select(g => g.ElementAt(n))).ToArray();
        var all = priority.Concat(selection.Candidates).DistinctBy(h => h.Chunk.ChunkId).ToArray();
        var selected = all.Where(h => h.Selected).Take(topK ?? 8).Select(h => h.Chunk.ChunkId).ToHashSet();
        return selection with { Candidates = all.Select(h => selected.Contains(h.Chunk.ChunkId) ? h : h with
            { Selected = false, SelectionReason = h.Selected ? "visual_context_limit" : h.SelectionReason }).ToArray() };

        RetrievalHit Linked(RagChunk chunk) => (selection.Candidates.FirstOrDefault(h => h.Chunk.ChunkId == chunk.ChunkId)
            ?? new RetrievalHit(chunk, 0) { RerankerMode = "linked_image:no_similarity" }) with
            { Selected = true, SelectionReason = "linked_family_visual_evidence" };
    }

    public sealed record SavedVisual(string Version, string SourceHash, DocumentAsset Asset, RagChunk? Chunk);
}
