using System.Text.Json;

namespace BimSAgentApp.Rag;

public sealed class JsonIndexStore(string directory) : IIndexStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };

    private string IndexPath(string strategy)
    {
        RagDefaults.ValidateStrategy(strategy);
        return Path.Combine(directory, strategy + ".json");
    }

    public RagIndex? Load(string strategy)
    {
        var path = IndexPath(strategy);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var index = JsonSerializer.Deserialize<RagIndex>(stream, JsonOptions)
                ?? throw new InvalidDataException("Пустой индекс.");
            Validate(index, strategy);
            return index;
        }
        catch (JsonException e) { throw new InvalidDataException($"Повреждён индекс {strategy}. Повторите rag index.", e); }
    }

    public static void Validate(RagIndex index, string strategy)
    {
        if (index.SchemaVersion != 1 || index.Strategy != strategy || index.EmbeddingModel != RagDefaults.EmbeddingModel
            || index.Dimensions != RagDefaults.Dimensions || string.IsNullOrWhiteSpace(index.BuildId)
            || string.IsNullOrWhiteSpace(index.ContentHash) || index.Chunks == null || index.Chunks.Length == 0)
            throw new InvalidDataException("Несовместимый или пустой индекс. Повторите rag index.");
        var ids = new HashSet<string>();
        foreach (var chunk in index.Chunks)
        {
            if (chunk == null || string.IsNullOrWhiteSpace(chunk.ChunkId) || !ids.Add(chunk.ChunkId)
                || chunk.Strategy != strategy || chunk.Source != index.Source || string.IsNullOrWhiteSpace(chunk.Text)
                || string.IsNullOrWhiteSpace(chunk.Section) || chunk.FamilyNames == null || chunk.BlockOrdinals == null
                || chunk.EmbeddingModel != index.EmbeddingModel || string.IsNullOrWhiteSpace(chunk.EmbeddingInput))
                throw new InvalidDataException("Некорректная запись чанка. Повторите rag index.");
            CosineRetriever.ValidateVector(chunk.Embedding, index.Dimensions);
        }
    }

    public async Task SavePairAsync(RagIndex fixedIndex, RagIndex structuralIndex, CancellationToken cancellationToken)
    {
        Validate(fixedIndex, "fixed");
        Validate(structuralIndex, "structural");
        RagService.ValidatePair(fixedIndex, structuralIndex);
        Directory.CreateDirectory(directory);
        // No generations: prepare both files before replacement. BuildId detects an interrupted pair update.
        using var guard = new FileStream(Path.Combine(directory, "index.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var paths = new[] { IndexPath("fixed"), IndexPath("structural") };
        var staged = paths.Select(p => p + "." + Guid.NewGuid().ToString("N") + ".tmp").ToArray();
        try
        {
            var indices = new[] { fixedIndex, structuralIndex };
            for (var i = 0; i < indices.Length; i++)
            {
                await using var file = new FileStream(staged[i], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(file, indices[i], JsonOptions, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Once publication begins, complete both replacements even if the caller cancels.
            for (var i = 0; i < paths.Length; i++) File.Move(staged[i], paths[i], overwrite: true);
        }
        finally
        {
            foreach (var path in staged) if (File.Exists(path)) File.Delete(path);
        }
    }
}
