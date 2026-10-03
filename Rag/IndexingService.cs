namespace BimSAgentApp.Rag;

public sealed class IndexingService(DocxDocumentExtractor extractor, RagTokenizer tokenizer,
    IEmbeddingClient embeddings, IIndexStore store, IDocumentAssetProcessor? assetProcessor = null)
{
    public async Task<(int Fixed, int Structural, bool Unchanged)> IndexAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        embeddings.Options.Validate();
        var document = extractor.Extract(path);
        // Explicit indexing also repairs an incompatible/corrupt index; normal retrieval still reports it.
        RagIndex? LoadExisting(string strategy)
        {
            try { return store.Load(strategy); }
            catch (InvalidDataException) { return null; }
        }
        var existingFixed = LoadExisting("fixed");
        var existingStructural = LoadExisting("structural");
        var manifest = IndexManifest.Current with { AssetProcessing = assetProcessor?.Version ?? "unconfigured" };
        bool Matches(RagIndex? i) => i != null && i.Source == document.Source && i.ContentHash == document.ContentHash
            && i.SchemaVersion == 3 && i.Manifest == manifest && i.ChunkSize == (i.Strategy == "structural" ? 2000 : 600) && i.Overlap == 100
            && i.EmbeddingModel == embeddings.Options.Model && i.Dimensions == embeddings.Options.Dimensions;
        if (Matches(existingFixed) && Matches(existingStructural) && existingFixed!.BuildId == existingStructural!.BuildId)
            return (existingFixed.Chunks.Length, existingStructural.Chunks.Length, true);

        document = await DocumentAssetProcessing.ProcessAsync(document, existingStructural?.Assets ?? [], assetProcessor, cancellationToken);
        var fixedChunks = new FixedWindowChunker(tokenizer).Split(document).ToArray();
        var parents = new StructuralChunker(tokenizer).Split(document).ToArray();
        var structuralChunks = new StructuralChunker(tokenizer, 650).Split(document).Select(child => child with
        {
            ParentBlockId = parents.FirstOrDefault(p => child.BlockOrdinals.All(p.BlockOrdinals.Contains))?.ChunkId
        }).ToArray();
        var cards = document.Families.Where(f => !string.IsNullOrWhiteSpace(f.Purpose)).SelectMany(f =>
        {
            var sourceBlocks = document.Blocks.Where(b => b.OwnerFamilyId == f.FamilyId && b.Warnings.Length == 0
                && (f.Purpose.Contains(b.Text, StringComparison.Ordinal) || f.Constraints.Contains(b.Text, StringComparison.Ordinal))).ToArray();
            return sourceBlocks.Length == 0 ? [] : new StructuralChunker(tokenizer, 650).Split(document with { Blocks = sourceBlocks })
                .Select(c => c with { Kind = "family_card", ChunkId = RagDefaults.Hash("card|" + c.ChunkId) }).ToArray();
        }).ToArray();
        structuralChunks = structuralChunks.Concat(cards).ToArray();
        var all = fixedChunks.Concat(structuralChunks).ToArray();
        if (all.Any(c => tokenizer.Count(c.EmbeddingInput) > 8191))
            throw new InvalidDataException("Слишком длинный embedding-вход; проверьте заголовки и параметры разбиения.");
        var cache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var chunk in (existingFixed?.Chunks ?? []).Concat(existingStructural?.Chunks ?? []))
            if (chunk.EmbeddingModel == embeddings.Options.Model && chunk.Embedding.Length == embeddings.Options.Dimensions)
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
                CosineRetriever.ValidateVector(vectors[i], embeddings.Options.Dimensions);
                cache.Add(batch[i], vectors[i]);
            }
        }
        var buildId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        RagIndex Build(string strategy, RagChunk[] chunks) => new(3, buildId, document.Source, document.ContentHash,
            strategy, embeddings.Options.Model, embeddings.Options.Dimensions, strategy == "structural" ? 2000 : 600, 100, now,
            chunks.Select(c => c with { Embedding = cache[c.EmbeddingInput], EmbeddingModel = embeddings.Options.Model }).ToArray())
        { Manifest = manifest, Families = document.Families, Blocks = document.Blocks.ToArray(),
            Assets = document.Assets, Parents = strategy == "structural" ? parents : [] };
        await store.SavePairAsync(Build("fixed", fixedChunks), Build("structural", structuralChunks), cancellationToken);
        return (fixedChunks.Length, structuralChunks.Length, false);
    }
}
