using System.Security.Cryptography;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace BimSAgentApp.Rag;

public static class PdfDocumentExtractor
{
    public static ExtractedDocument Extract(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        var blocks = new List<DocumentBlock>();
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            foreach (var page in pdf.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                if (text.Length == 0) continue;
                blocks.Add(new(blocks.Count, $"Страница {page.Number}", text, "paragraph", [])
                {
                    BlockId = $"pdf-page-{page.Number}", XmlPart = "pdf",
                    XmlPath = $"page/{page.Number}"
                });
            }
        }
        catch (Exception e) when (e is not IOException and not UnauthorizedAccessException and not OutOfMemoryException)
        {
            throw new InvalidDataException("Не удалось прочитать PDF. Проверьте файл и отсутствие пароля.", e);
        }
        if (blocks.Count == 0) throw new InvalidDataException("В PDF нет текстового слоя. Для скана требуется отдельный локальный OCR; облачный OCR не вызывается.");
        return new(fullPath, Path.GetFileNameWithoutExtension(path), Path.GetFileName(path),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), blocks);
    }
}
