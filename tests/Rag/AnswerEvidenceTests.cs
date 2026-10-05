using System.Text.Json;
using BimSAgentApp.Rag;

static class AnswerEvidenceTests
{
    public static void Run(Action<bool, string> check, RagIndex index)
    {
        var first = index.Chunks[0] with { Text = "Высота площадки 1.25 м.", FamilyPurpose = "", InheritedContext = [], Tables = [], Warnings = [] };
        var second = first with { ChunkId = "second", Text = "Ширина балки 0.35 м." };
        var contexts = new[] { new RagQuestionContext("Высота?", [new(first, 1)]), new RagQuestionContext("Ширина?", [new(second, 1)]) };
        var request = new RagAnswerRequest("1. Высота? 2. Ширина?", [], false, new(), contexts);
        string Payload(string quote) => JsonSerializer.Serialize(new { answers = new[] {
            new { number = 1, text = "Высота 1.25 м.", status = "supported", evidence = new[] { new { quote } } },
            new { number = 2, text = "Ширина 0.35 м.", status = "supported", evidence = new[] { new { quote = second.Text } } }
        }});
        var rendered = AnswerEvidenceValidator.ValidateAndRender(Payload(first.Text), request);
        check(rendered.Contains("1. Высота 1.25 м.") && rendered.Contains("2. Ширина 0.35 м."), "numbered answers preserve decimals and question order");
        var invalid = AnswerEvidenceValidator.ValidateAndRender(Payload(second.Text), request);
        check(invalid.Contains("1. " + RagDefaults.Unknown) && invalid.Contains("2. Ширина"), "quote from another question cannot support an answer");
        check(AnswerEvidenceValidator.ValidateAndRender("{}", request).Contains("2. " + RagDefaults.Unknown), "invalid response retains one fallback per question");
        check(AnswerEvidenceValidator.ValidateAndRender(Payload(""), request with { NoRag = true }).Contains("2. Ширина"), "NO-RAG renders separate answers without evidence");
    }
}

sealed class RetryAssetProcessor : IDocumentAssetProcessor
{
    public string Version => "unconfigured";
    public Task<DocumentAsset> ProcessAsync(DocumentAsset asset, byte[] media, CancellationToken cancellationToken)
        => Task.FromResult(asset with { Status = "ocr_only_unverified" });
}
