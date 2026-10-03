using BimSAgentApp.Rag;
using System.Net;
using System.Text.Json;

static class FamilyPipelineTests
{
    public static async Task Run(Action<bool, string> check, RagIndex template)
    {
        const string beam = "IFC_Набор_Балка_№2";
        check(FamilyIdentity.HeadingName(beam + " (Ребро жесткости, 2 хомута)") == beam, "family heading with explanatory suffix");
        check(FamilyIdentity.Contains("Для чего " + beam + "?", beam), "full numbered family matched");
        check(!FamilyIdentity.Contains(beam + "z", beam) && !FamilyIdentity.Contains(beam + "0", beam), "number and variant suffix are identity boundaries");
        check(!FamilyIdentity.Contains("IFC_Набор_Балка_№4", beam), "different family numbers never exact match");
        check(SearchLanguage.Tokens("лестничными площадками").SequenceEqual(SearchLanguage.Tokens("лестничная площадка")), "Russian noun and adjective case normalization");
        check(SearchLanguage.Tokens("не 200 мм " + beam).Contains("не") && SearchLanguage.Tokens(beam).Single().EndsWith("№2"), "negation dimensions and family identifiers preserved");
        var document = new ExtractedDocument("source", "title", "a.docx", "hash", [
            new(1, "Сборный", "КркНес_ЛМарш_Сборный", "heading", []) { HeadingLevel = 0, BlockId = "one" },
            new(2, "Сборный", "В этой статье описано всё об инструменте КркНес_ЛМарш_Монолитный", "paragraph", []) { BlockId = "two" },
            new(3, "Сборный", "Данный инструмент используют для моделирования сборных маршей", "paragraph", []) { BlockId = "three", AssetIds = ["image"] },
            new(4, "Монолитный", "КркНес_ЛМарш_Монолитный", "heading", []) { HeadingLevel = 0, BlockId = "four" },
            new(5, "Монолитный", "Данный инструмент используют для моделирования монолитных маршей", "paragraph", []) { BlockId = "five" }
        ]) { Assets = [new("image", "rId1", "word/document.xml", "/p[3]", "word/media/a.png", "hash", "unprocessed")] };
        var built = new FamilyCardBuilder().Build(document);
        check(built.Families.Length == 2 && built.Blocks[1].OwnerFamilyId == built.Blocks[0].OwnerFamilyId,
            "reference does not change family owner");
        check(built.Blocks[1].Warnings.Length == 1 && built.Families[0].Status == "conflict", "contradictory introduction detected");
        check(!built.Families[0].Purpose.Contains("Монолитный") && built.Families[0].Purpose.Contains("сборных"), "conflicting introduction excluded from purpose");
        check(built.Assets[0].OwnerFamilyId == built.Families[0].FamilyId, "image inherits containing block owner");
        var chunks = new StructuralChunker(new(), 650).Split(built);
        check(chunks.All(c => c.FamilyNames.Length == 1) && chunks.All(c => c.Spans.Length > 0), "structural provenance retains one owner and block spans");
        using var http = new HttpClient(new ScoresHandler());
        var reranker = new SemanticReranker(http, new Uri("http://localhost/rerank"), "fixture-cross-encoder");
        var hits = template.Chunks.Take(1).Select(c => new RetrievalHit(c, 0.6)).ToArray();
        var result = await reranker.RerankAsync("исходный вопрос", hits, default);
        check(result.Count == 1 && result[0].RerankerMode.StartsWith("cross_encoder:") && result[0].MatchedPassage != null,
            "semantic endpoint returns score and matching passage without generation");
        using var badHttp = new HttpClient(new ScoresHandler(true));
        var fallback = await new SemanticReranker(badHttp, new Uri("http://localhost/rerank"), "fixture").RerankAsync("вопрос", hits, default);
        check(fallback.All(h => h.RerankerMode == "lexical_fallback:reranker_unavailable"), "unavailable semantic service explicitly reports fallback");
        var budgeted = new ContextAssembler(new()).Assemble(new(hits, 1), 0);
        check(budgeted.Candidates[0] is { Selected: false, SelectionReason: "token_budget" }, "budget exclusion is traceable");
    }
    private sealed class ScoresHandler(bool fail = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fail) return new(HttpStatusCode.ServiceUnavailable);
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var count = input.RootElement.GetProperty("texts").GetArrayLength();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new { index = i, score = 3.0 + i }))) };
        }
    }
}
