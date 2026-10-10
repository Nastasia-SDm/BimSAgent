using System.Text.RegularExpressions;

internal static class PassportCommand
{
    internal sealed record Arguments(string Version, string FileName);
    internal static Arguments Parse(string arguments)
    {
        var match = Regex.Match(arguments, "^\\s*(V\\d{3,})\\s+\"([^\"]+)\"\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException("Использование: mcp6-call recommend-family-from-passport V001 \"Имя паспорта.pdf\"");
        var name = match.Groups[2].Value;
        if (name != name.Trim() || name.Length > 240 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) || !name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Укажите только имя PDF в кавычках, без пути.");
        return new(match.Groups[1].Value.ToUpperInvariant(), name.Normalize(System.Text.NormalizationForm.FormC));
    }
}
