using System.Security.Cryptography;
using System.Text;

namespace BimSAgentApp.Rag;

public static class RagDefaults
{
    public const string EmbeddingModel = "text-embedding-3-small";
    public const int Dimensions = 1536;
    public const string AnswerModel = "gpt-5.4-mini";
    public const string DataDirectory = @"D:\BIM-S_TestArtifacts\rag-data";
    public const string Unknown = "Не знаю: в найденных фрагментах недостаточно информации.";
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static void ValidateStrategy(string strategy)
    {
        if (strategy is not ("fixed" or "structural"))
            throw new ArgumentException("Стратегия должна быть fixed или structural.");
    }
}

public sealed record DocumentBlock(int Ordinal, string Section, string Text, string Kind, string[] FamilyNames)
{
    public string BlockId { get; init; } = "";
    public string XmlPart { get; init; } = "word/document.xml";
    public string XmlPath { get; init; } = "";
    public int? HeadingLevel { get; init; }
    public string? OwnerFamilyId { get; init; }
    public string[] ReferencedFamilyIds { get; init; } = [];
    public string Classification { get; init; } = "general_article";
    public string[] Warnings { get; init; } = [];
    public string? TableId { get; init; }
    public int? Row { get; init; }
    public TableCellData[] Cells { get; init; } = [];
    public string[] TableHeaders { get; init; } = [];
    public string[] AssetIds { get; init; } = [];
}
public sealed record TableCellData(int Column, string Text, int ColumnSpan, string? VerticalMerge, string XmlPath);
public sealed record DocumentAsset(string AssetId, string RelationshipId, string XmlPart, string XmlPath,
    string? MediaPart, string? ContentHash, string Status)
{
    public string? OwnerFamilyId { get; init; }
    public string AltText { get; init; } = "";
    public string Caption { get; init; } = "";
    public string OcrText { get; init; } = "";
    public string VisionText { get; init; } = "";
    public string ProcessorVersion { get; init; } = "unconfigured";
}
public sealed record FamilyCard(string FamilyId, string CanonicalName, string[] Aliases,
    string Purpose, string Constraints, int[] BlockOrdinals, string Status, string[] Warnings)
{
    public string[] AssetIds { get; init; } = [];
    public string[] ReferencedFamilyIds { get; init; } = [];
}
public sealed record IndexManifest(string Extraction, string CardBuilding, string Chunking, string EmbeddingTemplate,
    string AssetProcessing, string ConfigurationHash)
{
    public static IndexManifest Current => new("3", "4", "4", "3", "unconfigured", RagDefaults.Hash("parent=2000;child=650;overlap=100"));
}
public sealed record ExtractedDocument(string Source, string Title, string File, string ContentHash,
    IReadOnlyList<DocumentBlock> Blocks)
{
    public FamilyCard[] Families { get; init; } = [];
    public DocumentAsset[] Assets { get; init; } = [];
    public string[] Warnings { get; init; } = [];
}

public sealed record RagChunk(string Source, string Title, string File, string Section, string ChunkId,
    string Strategy, string Text, string EmbeddingInput, float[] Embedding, string EmbeddingModel,
    int TokenCount, string[] FamilyNames, int[] BlockOrdinals)
{
    public string? ParentBlockId { get; init; }
    public string? OwnerFamilyId { get; init; }
    public string Kind { get; init; } = "passage";
    public string EmbeddingInputHash { get; init; } = "";
    public string EmbeddingTemplateVersion { get; init; } = "3";
    public string[] Warnings { get; init; } = [];
    public string[] AssetIds { get; init; } = [];
    public EvidenceSpan[] Spans { get; init; } = [];
    public string FamilyPurpose { get; init; } = "";
    public string[] PurposeBlockIds { get; init; } = [];
    public TableContext[] Tables { get; init; } = [];
    public DocumentBlock[] InheritedContext { get; init; } = [];
    public string[] FamilyWarnings { get; init; } = [];
}
public sealed record TableContext(string TableId, string[] Headers, string HeaderBlockId, bool InferredFromFirstRow);
public sealed record EvidenceSpan(string BlockId, string? OwnerFamilyId, int Start, int End, int SourceStart, int SourceEnd);

public sealed record RagIndex(int SchemaVersion, string BuildId, string Source, string ContentHash,
    string Strategy, string EmbeddingModel, int Dimensions, int ChunkSize, int Overlap,
    DateTimeOffset CreatedAt, RagChunk[] Chunks)
{
    public IndexManifest Manifest { get; init; } = IndexManifest.Current;
    public FamilyCard[] Families { get; init; } = [];
    public DocumentBlock[] Blocks { get; init; } = [];
    public DocumentAsset[] Assets { get; init; } = [];
    public RagChunk[] Parents { get; init; } = [];
}

public sealed record RetrievalHit(RagChunk Chunk, double SimilarityScore)
{
    public double? RerankScore { get; init; }
    public bool ExactFamilyMatch { get; init; }
    public bool ExactFamilyInText { get; init; }
    public string[] ExactTerms { get; init; } = [];
    public bool Selected { get; init; } = true;
    public double LexicalScore { get; init; }
    public double FusionScore { get; init; }
    public int DenseRank { get; init; }
    public int LexicalRank { get; init; }
    public string RerankerMode { get; init; } = "none";
    public string SelectionReason { get; init; } = "candidate";
    public string? MatchedPassage { get; init; }
}
public sealed record RetrievalSelection(IReadOnlyList<RetrievalHit> Candidates, int ScannedCount);
public sealed record AnswerOptions(double Temperature = 0, int MaxOutputTokens = 2000);
public sealed record RagQuestionContext(string Question, IReadOnlyList<RetrievalHit> Context)
{
    public int ScannedCount { get; init; }
}
public sealed record RagAnswerRequest(string Question, IReadOnlyList<RetrievalHit> Context,
    bool NoRag, AnswerOptions Options, IReadOnlyList<RagQuestionContext>? Questions = null);
public sealed record RagAnswer(string Text, IReadOnlyList<RetrievalHit> Sources, bool NoRag)
{
    // Expanded candidates with final Selected flags, grouped by question.
    public IReadOnlyList<RagQuestionContext> RetrievedQuestions { get; init; } = [];
}
public sealed record RagComparison(RagAnswer Fixed, RagAnswer Structural);

public interface IEmbeddingClient
{
    EmbeddingOptions Options => new();
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
    RetrievalSelection Retrieve(RagIndex index, float[] query, string question, int? limit) =>
        new(Search(index, query, limit ?? 5), index.Chunks.Length);
    Task<RetrievalSelection> RetrieveAsync(RagIndex index, float[] query, string question, int? limit, CancellationToken cancellationToken)
        => Task.FromResult(Retrieve(index, query, question, limit));
}

public interface IChunker
{
    string Strategy { get; }
    IReadOnlyList<RagChunk> Split(ExtractedDocument document);
}
