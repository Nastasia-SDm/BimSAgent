using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public static class EmbeddingInputBuilder
{
    public static bool Boilerplate(string text) => Regex.IsMatch(text.Trim(),
        @"^(?:В этой статье описано|ОБЩАЯ ИНФОРМАЦИЯ$|ПАРАМЕТРЫ И СВОЙСТВА$|РАЗМЕЩЕНИЕ В МОДЕЛИ$|РАБОТА С ЭКЗЕМПЛЯРОМ$)", RegexOptions.IgnoreCase);
    public static string Build(string text, string section, string[] names, FamilyCard? family)
    {
        var meaningful = string.Join("\n", text.Split('\n').Where(line => !Boilerplate(line)));
        return string.Join("\n", new[] { string.Join("; ", names), family?.Purpose ?? "", section, meaningful }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    }
}
