using System.Text.RegularExpressions;

namespace BimSAgentApp.Rag;

public static class QuestionParser
{
    // A list starts with 1. and uses consecutive numbers followed by whitespace.
    // Decimal numbers, version numbers and numbering embedded in ordinary prose are not lists.
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var markers = Regex.Matches(text, @"(?:^|(?<=[?!])\s*)([1-9][0-9]*)\.\s*");
        if (markers.Count < 2 || !string.IsNullOrWhiteSpace(text[..markers[0].Index])) return [text];
        var questions = new List<string>();
        for (var i = 0; i < markers.Count; i++)
        {
            if (markers[i].Groups[1].Value != (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) return [text];
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            var question = text[start..end].Trim();
            if (question.Length == 0) return [text];
            questions.Add(question);
        }
        return questions;
    }
}
