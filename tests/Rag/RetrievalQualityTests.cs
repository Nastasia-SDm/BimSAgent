using System.Text.Json;
using BimSAgentApp.Rag;

static class RetrievalQualityTests
{
    public static async Task Run(Action<bool, string> check, RagIndex template, RagTokenizer tokenizer, string fixture, string root)
    {
        var retriever = new HybridRetriever();
        float[] Vector(double similarity)
        {
            var v = new float[1536]; v[0] = (float)similarity; v[1] = (float)Math.Sqrt(1 - similarity * similarity); return v;
        }
        RagChunk Chunk(string id, string text, double score, string[]? families = null) => template.Chunks[0] with
        {
            ChunkId = id, Text = text, Section = "Раздел", EmbeddingInput = text, Embedding = Vector(score), FamilyNames = families ?? []
        };
        var query = Vector(1);
        const string family = "КркНес_ЛПлощадка_Монолитная";
        var many = Enumerable.Range(0, 60).Select(i => Chunk("semantic" + i, "Описание постороннего элемента " + i, 0.85 - i * 0.001)).ToList();
        many.Add(Chunk("exact", "Инструкция: " + family + " устанавливается на опорную грань.", 0.05, [family]));
        many.Add(Chunk("partial", "Другая конструкция " + family + "_Другая", 0.04));
        var result = retriever.Retrieve(template with { Chunks = many.ToArray() }, query, "Как установить " + family + "?", null);
        check(result.Candidates.Single(c => c.Chunk.ChunkId == "exact") is { ExactFamilyMatch: true, Selected: true },
            "exact family survives outside semantic candidate window despite low cosine");
        check(result.Candidates.Where(c => c.Chunk.ChunkId == "partial").All(c => !c.ExactFamilyMatch), "family substring is not exact match");
        check(result.ScannedCount == 62 && result.Candidates.Count >= 40, "expanded candidate pool and scan diagnostics");
        check(result.Candidates.First(c => c.Selected).Chunk.ChunkId == "exact", "semantic search cannot displace exact family");
        check(result.Candidates.All(c => c.RerankScore.HasValue && double.IsFinite(c.RerankScore.Value)), "all expanded candidates have rerank scores");
        var recorder = new RecordingReranker();
        new HybridRetriever(recorder).Retrieve(template with { Chunks = many.ToArray() }, query, "Исходный вопрос " + family, 1);
        check(recorder.Question == "Исходный вопрос " + family && recorder.Count >= 40,
            "reranker sees original question and expanded pool before final limit");
        var rescueIndex = template with { Chunks = [Chunk("irrelevant", "Общие сведения о конструкциях", 0.95),
            Chunk("instruction", "Фиксатор арматуры: настройте диаметр фиксатора.", 0.08)] };
        var rescue = retriever.Retrieve(rescueIndex, query, "фиксатор арматуры диаметр", 1);
        check(rescue.Candidates.Single(c => c.Selected).Chunk.ChunkId == "instruction"
            && rescue.Candidates.Single(c => c.Chunk.ChunkId == "instruction").SimilarityScore < 0.1,
            "second stage rescues relevant low-cosine instruction before cutoff");
        var reranker = new Bm25Reranker();
        var equalCandidates = new[] { new RetrievalHit(Chunk("landing", "Лестничная площадка: опирание на стену", 0.5), 0.5),
            new RetrievalHit(Chunk("beam", "Балка: настройка армирования", 0.5), 0.5) };
        check(reranker.Rerank("площадка опирание", equalCandidates)[0].Chunk.ChunkId == "landing"
            && reranker.Rerank("балка армирования", equalCandidates)[0].Chunk.ChunkId == "beam",
            "reranking is question dependent rather than a fixed reordering");
        check(reranker.Rerank("балка армирования", equalCandidates).All(c => c.SimilarityScore == 0.5),
            "reranking preserves original cosine scores");

        var gapIndex = template with { Chunks = [Chunk("a", "Уникальная инструкция размещения", 0.8),
            Chunk("b", "Другой способ настройки геометрии", 0.78), Chunk("c", "Слабосвязанный текст", 0.4), Chunk("d", "Совсем другое", 0.2)] };
        var gap = retriever.Retrieve(gapIndex, query, "Что это?", null);
        check(gap.Candidates.Count(c => c.Selected) == 2, "automatic count cuts weak candidates at cosine gap");
        var multipleGaps = template with { Chunks = [Chunk("first", "Первый", 0.9), Chunk("second", "Второй", 0.79), Chunk("weak", "Слабый", 0.5)] };
        check(retriever.Retrieve(multipleGaps, query, "Что это?", null).Candidates.Count(c => c.Selected) == 2,
            "largest cosine gap wins over earlier smaller gap");
        var unrelated = retriever.Retrieve(template with { Chunks = [Chunk("none", "другие сведения", 0.02)] }, query, "насос", null);
        check(unrelated.Candidates.All(c => !c.Selected), "weak unrelated candidates produce empty selection");
        var lexical = retriever.Retrieve(template with { Chunks = [Chunk("semantic", "Общая информация", 0.8),
            Chunk("term", "Используйте фиксатор арматуры", 0.72)] }, query, "фиксатор арматуры", null);
        check(lexical.Candidates[0].Chunk.ChunkId == "term" && lexical.Candidates[0].ExactTerms.Length == 2,
            "important exact terms contribute to local ranking");

        var longText = "Разместите компонент на рабочей плоскости затем настройте ширину высоту и положение ручек после этого проверьте привязку граней заполните сведения проекта и сохраните необходимые параметры для последующего использования";
        var duplicates = template with { Chunks = [Chunk("a", longText, 0.9), Chunk("b", longText.ToUpperInvariant(), 0.89),
            Chunk("c", longText + " далее", 0.88), Chunk("d", "Не " + longText, 0.87)] };
        var deduped = retriever.Retrieve(duplicates, query, "Что это?", null);
        check(deduped.Candidates.Count(c => c.Selected) == 2 && deduped.Candidates.Single(c => c.Chunk.ChunkId == "d").Selected,
            "exact and near duplicates removed without dropping negated instruction");
        check(deduped.Candidates.Select(c => c.Chunk.ChunkId).SequenceEqual(retriever.Retrieve(duplicates, query, "Что это?", null)
            .Candidates.Select(c => c.Chunk.ChunkId)), "hybrid ordering deterministic");
        check(retriever.Retrieve(gapIndex, query, "Что это?", 1).Candidates.Count(c => c.Selected) == 1, "legacy top-K remains an optional upper bound");

        var paragraph = string.Concat(Enumerable.Repeat("Инструкция размещения арматуры. ", 65));
        var doc = new ExtractedDocument("fixture", "fixture", "fixture.docx", "hash", [
            new(1, family, family, "heading", [family]), new(2, family, paragraph, "paragraph", [family]),
            new(3, family + " / Настройка", "Настройка", "heading", [family]),
            new(4, family + " / Настройка", "Параметр | Значение", "table-row:1", [family]),
            new(5, family + " / Настройка", "Проверьте привязку к грани.", "paragraph", [family])]);
        var chunks = new StructuralChunker(tokenizer).Split(doc);
        check(chunks.Count == 1 && chunks[0].Text.Contains("Параметр | Значение") && chunks[0].Text.Contains("Проверьте привязку"),
            "family heading paragraphs subheading table and instructions stay together");
        var large = doc with { Blocks = doc.Blocks.Concat(new[] { new DocumentBlock(6, family, string.Concat(Enumerable.Repeat(paragraph, 8)), "paragraph", [family]) }).ToArray() };
        var children = new StructuralChunker(tokenizer).Split(large);
        check(children.Count > 1 && children.Select(c => c.ParentBlockId).Distinct().Count() == 1
            && children.All(c => c.FamilyNames.Contains(family)), "split children retain family and parent ID");
        var oversizedChildren = children.Where(c => c.BlockOrdinals.SequenceEqual(new[] { 6 })).ToArray();
        check(oversizedChildren.Length > 1 && oversizedChildren.Zip(oversizedChildren.Skip(1)).All(pair => Enumerable.Range(1, Math.Min(pair.First.Text.Length, pair.Second.Text.Length))
            .Any(n => pair.First.Text.EndsWith(pair.Second.Text[..n], StringComparison.Ordinal) && tokenizer.Count(pair.Second.Text[..n]) >= 30)),
            "only oversized atomic block children retain boundary overlap");

        var small = new ConfigurableEmbeddings(new("text-embedding-3-small", 4));
        var indexStore = new JsonIndexStore(Path.Combine(root, "quality-index"));
        await new IndexingService(new(), tokenizer, small, indexStore).IndexAsync(fixture, default);
        var largeModel = new ConfigurableEmbeddings(new("text-embedding-3-large", 4));
        await new IndexingService(new(), tokenizer, largeModel, indexStore).IndexAsync(fixture, default);
        check(largeModel.Calls > 0 && indexStore.Load("structural")!.EmbeddingModel == "text-embedding-3-large",
            "model change rebuilds embeddings even at same dimension");
        var calls = largeModel.Calls;
        check((await new IndexingService(new(), tokenizer, largeModel, indexStore).IndexAsync(fixture, default)).Unchanged
            && largeModel.Calls == calls, "unchanged compatible index still avoids API calls");
        var bad = new RagService(small, new FakeGenerator(), indexStore, retriever, tokenizer);
        var previousCalls = small.Calls;
        try { await bad.AskAsync("вопрос", "structural"); throw new Exception("Mismatch accepted"); }
        catch (InvalidOperationException) { check(small.Calls == previousCalls, "model mismatch rejected before query embedding"); }
        var old = indexStore.Load("structural")! with { SchemaVersion = 1 };
        File.WriteAllText(Path.Combine(root, "quality-index", "structural.json"), JsonSerializer.Serialize(old, JsonIndexStore.JsonOptions));
        try { indexStore.Load("structural"); throw new Exception("Old index accepted"); }
        catch (InvalidDataException) { check(true, "old chunking schema explicitly requires reindex"); }
        check(!(await new IndexingService(new(), tokenizer, largeModel, indexStore).IndexAsync(fixture, default)).Unchanged,
            "index command rebuilds old chunking even when document unchanged");
        var previousSize = indexStore.Load("structural")! with { ChunkSize = 1200 };
        File.WriteAllText(Path.Combine(root, "quality-index", "structural.json"), JsonSerializer.Serialize(previousSize, JsonIndexStore.JsonOptions));
        try { await new RagService(largeModel, new FakeGenerator(), indexStore, retriever, tokenizer).AskAsync("вопрос", "structural"); throw new Exception("Old size accepted"); }
        catch (InvalidOperationException) { check(true, "old structural size explicitly requires reindex"); }
        check(!(await new IndexingService(new(), tokenizer, largeModel, indexStore).IndexAsync(fixture, default)).Unchanged
            && indexStore.Load("structural")!.ChunkSize == 2000, "index command rebuilds for 2000-token chunks without source changes");
    }

    sealed class RecordingReranker : IReranker
    {
        public string? Question { get; private set; }
        public int Count { get; private set; }
        public IReadOnlyList<RetrievalHit> Rerank(string question, IReadOnlyList<RetrievalHit> candidates)
        {
            Question = question; Count = candidates.Count;
            return new Bm25Reranker().Rerank(question, candidates);
        }
    }

    sealed class ConfigurableEmbeddings(EmbeddingOptions options) : IEmbeddingClient
    {
        public EmbeddingOptions Options => options;
        public int Calls { get; private set; }
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(texts.Select(t => { var v = new float[Options.Dimensions]; v[0] = 1; return v; }).ToArray());
        }
    }
}
