namespace BimSAgentApp.Rag;

public sealed class IndexingService(DocxDocumentExtractor extractor, RagTokenizer tokenizer,
    IEmbeddingClient embeddings, IIndexStore store)
{
    public async Task<(int Fixed, int Structural, bool Unchanged)> IndexAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = extractor.Extract(path);
        // Explicit indexing also repairs an incompatible/corrupt index; normal retrieval still reports it.
        RagIndex? LoadExisting(string strategy)
        {
            try { return store.Load(strategy); }
            catch (InvalidDataException) { return null; }
        }
        var existingFixed = LoadExisting("fixed");
        var existingStructural = LoadExisting("structural");
        bool Matches(RagIndex? i) => i != null && i.Source == document.Source && i.ContentHash == document.ContentHash
            && i.ChunkSize == 600 && i.Overlap == 100;
        if (Matches(existingFixed) && Matches(existingStructural) && existingFixed!.BuildId == existingStructural!.BuildId)
            return (existingFixed.Chunks.Length, existingStructural.Chunks.Length, true);

        var fixedChunks = new FixedWindowChunker(tokenizer).Split(document).ToArray();
        var structuralChunks = new StructuralChunker(tokenizer).Split(document).ToArray();
        var all = fixedChunks.Concat(structuralChunks).ToArray();
        if (all.Any(c => tokenizer.Count(c.EmbeddingInput) > 8191))
            throw new InvalidDataException("Слишком длинный embedding-вход; проверьте заголовки и параметры разбиения.");
        var cache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var chunk in (existingFixed?.Chunks ?? []).Concat(existingStructural?.Chunks ?? []))
            cache.TryAdd(chunk.EmbeddingInput, chunk.Embedding);
        var missing = all.Select(c => c.EmbeddingInput).Distinct(StringComparer.Ordinal).Where(t => !cache.ContainsKey(t)).ToArray();
        // Each input is validated above; 16 inputs stay below the API's aggregate token limit.
        foreach (var batch in missing.Chunk(16))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vectors = await embeddings.EmbedAsync(batch, cancellationToken);
            if (vectors.Length != batch.Length) throw new InvalidDataException("Неверное количество embeddings.");
            for (var i = 0; i < batch.Length; i++)
            {
                CosineRetriever.ValidateVector(vectors[i], RagDefaults.Dimensions);
                cache.Add(batch[i], vectors[i]);
            }
        }
        var buildId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        RagIndex Build(string strategy, RagChunk[] chunks) => new(1, buildId, document.Source, document.ContentHash,
            strategy, RagDefaults.EmbeddingModel, RagDefaults.Dimensions, 600, 100, now,
            chunks.Select(c => c with { Embedding = cache[c.EmbeddingInput] }).ToArray());
        await store.SavePairAsync(Build("fixed", fixedChunks), Build("structural", structuralChunks), cancellationToken);
        return (fixedChunks.Length, structuralChunks.Length, false);
    }
}
