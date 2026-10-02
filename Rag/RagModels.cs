using System.Security.Cryptography;
using System.Text;

namespace BimSAgentApp.Rag;

public static class RagDefaults
{
    public const string EmbeddingModel = "text-embedding-3-small";
    public const int Dimensions = 1536;
    public const string AnswerModel = "gpt-4.1-nano";
    public const string DataDirectory = @"D:\BIM-S_TestArtifacts\rag-data";
    public const string Unknown = "Не знаю: в найденных фрагментах недостаточно информации.";
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static void ValidateStrategy(string strategy)
    {
        if (strategy is not ("fixed" or "structural"))
            throw new ArgumentException("Стратегия должна быть fixed или structural.");
    }
}

public sealed record DocumentBlock(int Ordinal, string Section, string Text, string Kind, string[] FamilyNames);
public sealed record ExtractedDocument(string Source, string Title, string File, string ContentHash,
    IReadOnlyList<DocumentBlock> Blocks);

public sealed record RagChunk(string Source, string Title, string File, string Section, string ChunkId,
    string Strategy, string Text, string EmbeddingInput, float[] Embedding, string EmbeddingModel,
    int TokenCount, string[] FamilyNames, int[] BlockOrdinals);

public sealed record RagIndex(int SchemaVersion, string BuildId, string Source, string ContentHash,
    string Strategy, string EmbeddingModel, int Dimensions, int ChunkSize, int Overlap,
    DateTimeOffset CreatedAt, RagChunk[] Chunks);

public sealed record RetrievalHit(RagChunk Chunk, double SimilarityScore);
public sealed record AnswerOptions(double Temperature = 0, int MaxOutputTokens = 600);
public sealed record RagAnswerRequest(string Question, IReadOnlyList<RetrievalHit> Context,
    bool NoRag, AnswerOptions Options);
public sealed record RagAnswer(string Text, IReadOnlyList<RetrievalHit> Sources, bool NoRag);
public sealed record RagComparison(RagAnswer Fixed, RagAnswer Structural);

public interface IEmbeddingClient
{
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public interface IRagAnswerGenerator
{
    Task<string> GenerateAsync(RagAnswerRequest request, CancellationToken cancellationToken);
}

public interface IIndexStore
{
    RagIndex? Load(string strategy);
    Task SavePairAsync(RagIndex fixedIndex, RagIndex structuralIndex, CancellationToken cancellationToken);
}

public interface IRetriever
{
    IReadOnlyList<RetrievalHit> Search(RagIndex index, float[] query, int topK);
}

public interface IChunker
{
    string Strategy { get; }
    IReadOnlyList<RagChunk> Split(ExtractedDocument document);
}
