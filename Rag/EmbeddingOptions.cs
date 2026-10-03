namespace BimSAgentApp.Rag;

public sealed record EmbeddingOptions(string Model = RagDefaults.EmbeddingModel, int Dimensions = RagDefaults.Dimensions)
{
    public void Validate()
    {
        var maximum = Model switch { "text-embedding-3-small" => 1536, "text-embedding-3-large" => 3072,
            _ => throw new ArgumentException("Поддерживаются text-embedding-3-small и text-embedding-3-large.") };
        if (Dimensions < 1 || Dimensions > maximum) throw new ArgumentException("Недопустимая размерность embeddings.");
    }

    public static EmbeddingOptions FromEnvironment()
    {
        var model = Environment.GetEnvironmentVariable("BIMS_EMBEDDING_MODEL") ?? RagDefaults.EmbeddingModel;
        var raw = Environment.GetEnvironmentVariable("BIMS_EMBEDDING_DIMENSIONS");
        var dimensions = model == "text-embedding-3-large" ? 3072 : 1536;
        if (raw != null && !int.TryParse(raw, out dimensions)) throw new ArgumentException("BIMS_EMBEDDING_DIMENSIONS должно быть целым числом.");
        var options = new EmbeddingOptions(model, dimensions);
        options.Validate();
        return options;
    }
}
