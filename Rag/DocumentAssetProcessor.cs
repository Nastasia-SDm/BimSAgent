using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BimSAgentApp.Rag;

public interface IDocumentAssetProcessor
{
    string Version { get; }
    Task<DocumentAsset> ProcessAsync(DocumentAsset asset, byte[] media, CancellationToken cancellationToken);
}

// OCR and interpretation stay separate; neither is promoted to verified source text.
public sealed class HttpDocumentAssetProcessor(HttpClient http, Uri endpoint, string version, string? key) : IDocumentAssetProcessor
{
    public string Version => version;
    public async Task<DocumentAsset> ProcessAsync(DocumentAsset asset, byte[] media, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new { asset_id = asset.AssetId, media_part = asset.MediaPart,
            content_hash = asset.ContentHash, image_base64 = Convert.ToBase64String(media), processor_version = Version }), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Asset processor HTTP " + (int)response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        return asset with { OcrText = root.GetProperty("ocr_text").GetString() ?? "",
            VisionText = root.GetProperty("vision_text").GetString() ?? "",
            Status = root.TryGetProperty("status", out var status) ? status.GetString() ?? "processed_unverified" : "processed_unverified", ProcessorVersion = Version };
    }
}

public static class DocumentAssetProcessing
{
    public static async Task<ExtractedDocument> ProcessAsync(ExtractedDocument document, IEnumerable<DocumentAsset> previous,
        IDocumentAssetProcessor? processor, CancellationToken cancellationToken)
    {
        var cache = previous.Where(a => a.ContentHash != null && (processor == null || a.ProcessorVersion == processor.Version) && a.Status is "processed_unverified" or "ocr_only_unverified")
            .GroupBy(a => a.ContentHash!).ToDictionary(g => g.Key, g => g.First());
        using var archive = ZipFile.OpenRead(document.Source);
        var assets = new List<DocumentAsset>();
        foreach (var asset in document.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.ContentHash == null || asset.MediaPart == null) { assets.Add(asset); continue; }
            if (cache.TryGetValue(asset.ContentHash, out var cached))
            { assets.Add(asset with { OcrText = cached.OcrText, VisionText = cached.VisionText, Status = cached.Status, ProcessorVersion = cached.ProcessorVersion }); continue; }
            if (processor == null) { assets.Add(asset); continue; }
            var entry = archive.GetEntry(asset.MediaPart);
            if (entry == null) { assets.Add(asset with { Status = "missing" }); continue; }
            try
            {
                await using var input = entry.Open(); using var memory = new MemoryStream();
                await input.CopyToAsync(memory, cancellationToken);
                var bytes = memory.ToArray();
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != asset.ContentHash)
                    throw new InvalidDataException("DOCX изменился во время обработки изображений.");
                var result = await processor.ProcessAsync(asset, bytes, cancellationToken);
                assets.Add(result); cache[asset.ContentHash] = result;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { assets.Add(asset with { Status = "processing_failed", ProcessorVersion = processor.Version }); }
        }
        var blocks = document.Blocks.ToList();
        foreach (var asset in assets.Where(a => a.Status is "processed_unverified" or "ocr_only_unverified"))
        {
            var original = document.Blocks.FirstOrDefault(b => b.AssetIds.Contains(asset.AssetId));
            if (original == null) continue;
            foreach (var (kind, text) in new[] { ("ocr", asset.OcrText), ("vision_interpretation", asset.VisionText) })
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                blocks.Add(original with { Ordinal = blocks.Count + 1, BlockId = RagDefaults.Hash(asset.AssetId + "|" + kind + "|" + asset.ProcessorVersion),
                    Kind = kind, Text = $"[{kind}: машинное извлечение, требуется проверка по изображению {asset.MediaPart}]\n{text}",
                    AssetIds = [asset.AssetId], Warnings = ["machine_extracted_unverified"], Cells = [], TableHeaders = [], TableId = null, Row = null });
            }
        }
        return document with { Assets = assets.ToArray(), Blocks = blocks };
    }
}
