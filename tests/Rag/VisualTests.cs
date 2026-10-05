using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BimSAgentApp.Rag;

static class VisualTests
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        const string name = "ФНК_Тест";
        const string description = "На схеме виден прямоугольный контур с внутренним углублением.";
        byte[] bytes = [137, 80, 78, 71, 0];
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(root, "visual.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("word/media/image.png").Open()) stream.Write(bytes);
        var asset = new DocumentAsset("asset", "r1", "word/document.xml", "/p[1]", "word/media/image.png", hash, "ocr_only_unverified")
            { OwnerFamilyId = "owner", OcrText = "Размеры на рисунке" };
        var block = new DocumentBlock(1, name, "Изображение", "paragraph", [name]) { BlockId = "original", OwnerFamilyId = "owner", AssetIds = [asset.AssetId] };
        var doc = new ExtractedDocument(path, "test", "test.docx", "doc", [block]) { Assets = [asset], Families = [new("owner", name, [], "", "", [1], "identified", [])] };
        var handler = new VisionHandler(description);
        using var http = new HttpClient(handler);
        var client = new OpenAiRagClient(http, () => "test-key");
        var result = await VisualIndexing.ProcessAsync(doc, [], client, name, default);
        check(handler.Calls == 1 && handler.SawImage, "vision sends actual image input, not family-name inference");
        check(result.Assets[0].VisionStatus == "recognized" && result.Assets[0].OcrText == asset.OcrText, "vision preserves OCR independently");
        var visual = result.Blocks.Single(b => b.Kind == "vision_interpretation");
        check(visual.OwnerFamilyId == "owner" && visual.AssetIds.SequenceEqual(["asset"]) && visual.Text.Contains(description), "visual block retains image and family provenance");
        var again = await VisualIndexing.ProcessAsync(doc, result.Assets, client, name, default);
        check(handler.Calls == 1 && again.Blocks.Last().BlockId == visual.BlockId, "vision cache avoids repeated calls and preserves IDs");
        var preserved = await VisualIndexing.ProcessAsync(doc, result.Assets, null, null, default);
        check(preserved.Blocks.Any(b => b.Text.Contains(description)), "ordinary reindex retains recognized vision");
        var chunks = new StructuralChunker(new(), 650).Split(result);
        check(chunks.Count == 2 && chunks.Last().EmbeddingInput.Contains(description), "vision is independently chunked and embedded");
        var hit = new RetrievalHit(chunks.Last(), 0.8);
        var request = new RagAnswerRequest("Как выглядит?", [hit], false, new());
        string Payload(string status, string quote) => JsonSerializer.Serialize(new { answers = new[] { new { number = 1, text = "По распознанному изображению виден прямоугольный контур.", status, evidence = new[] { new { quote } } } } });
        var answer = AnswerEvidenceValidator.ValidateAndRender(Payload("uncertain", description), request);
        check(answer.Contains("chunk_id: " + hit.Chunk.ChunkId) && answer.Contains(description), "visual answer uses C# resolved source and verified quote");
        check(AnswerEvidenceValidator.ValidateAndRender(Payload("supported", description), request).Contains("По распознанному изображению"), "visual observation receives recognition caveat");
        check(!AnswerEvidenceValidator.ValidateAndRender(Payload("supported", description), request with { Question = "Какой размер?" }).Contains("chunk_id:"), "vision not promoted to verified technical fact");
        check(!AnswerEvidenceValidator.ValidateAndRender(Payload("uncertain", "Невидимая арматура расположена внутри"), request).Contains("chunk_id:"), "invented visual quote rejected");
        var absent = await VisualIndexing.ProcessAsync(doc, [], null, null, default);
        check(absent.Blocks.All(b => b.Kind != "vision_interpretation"), "OCR alone never fabricates geometry");
        using var emptyHttp = new HttpClient(new VisionHandler(""));
        var insufficient = await VisualIndexing.ProcessAsync(doc, [], new OpenAiRagClient(emptyHttp, () => "test"), null, default);
        check(insufficient.Assets[0].VisionStatus == "insufficient" && insufficient.Blocks.Count == 1, "interface-only image adds no shape evidence");
        var other = doc with { Families = doc.Families.Append(new FamilyCard("other", "ФНК_Другой", [], "", "", [], "identified", [])).ToArray() };
        await VisualIndexing.ProcessAsync(other, [], client, "ФНК_Другой", default);
        check(handler.Calls == 1, "family scope never processes another owner's images");
        foreach (var marker in new[] { "⬪", "➜➜", "➤" })
        foreach (var headings in new[] { new[] { "Источники", "Цитаты" }, new[] { "Цитаты", "Источники" } })
        {
            var rendered = $"{marker} 1. Ответ один\n▫️ {headings[0]}:\nстарая цитата\n▫️ {headings[1]}:\nchunk_id: old\n{marker} 2. Ответ два";
            check(RagChatEngine.HistoryText(rendered) == $"{marker} 1. Ответ один\n{marker} 2. Ответ два", "history excludes evidence in both display formats and orders");
        }
    }

    private sealed class VisionHandler(string description) : HttpMessageHandler
    {
        public int Calls;
        public bool SawImage;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            SawImage = input.RootElement.GetProperty("input")[0].GetProperty("content")[1].GetProperty("image_url").GetString()!.StartsWith("data:image/png;base64,");
            var json = JsonSerializer.Serialize(new { status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(new { kind = description.Length == 0 ? "interface" : "geometry", description }) } } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
