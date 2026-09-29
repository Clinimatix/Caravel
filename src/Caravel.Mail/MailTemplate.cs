using System.Text.Encodings.Web;
using System.Text;
using System.Text.RegularExpressions;

namespace Caravel.Mail;

/// <summary>Trusted templates with named {{placeholders}}. HTML values are encoded; no expressions or raw HTML substitution.</summary>
public static partial class MailTemplate
{
    public static string RenderHtml(string template, IReadOnlyDictionary<string, string> values) => Render(template, values, true);
    public static string RenderText(string template, IReadOnlyDictionary<string, string> values) => Render(template, values, false);

    private static string Render(string template, IReadOnlyDictionary<string, string> values, bool html)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        if (template.Length > 1024 * 1024) throw new ArgumentException("The template exceeds the character limit.", nameof(template));
        var result = new StringBuilder();
        var offset = 0;
        foreach (Match match in Placeholder().Matches(template))
        {
            Append(template[offset..match.Index]);
            if (!values.TryGetValue(match.Groups[1].Value, out var value) || value is null)
                throw new ArgumentException("A template placeholder has no value.", nameof(values));
            if (value.Length > 64 * 1024) throw new ArgumentException("A template value exceeds the character limit.", nameof(values));
            Append(html ? HtmlEncoder.Default.Encode(value) : value);
            offset = match.Index + match.Length;
        }
        Append(template[offset..]);
        return result.ToString();

        void Append(string text)
        {
            if ((long)result.Length + text.Length > 1024 * 1024)
                throw new ArgumentException("The rendered template exceeds the character limit.");
            result.Append(text);
        }
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9_]*)\}\}")]
    private static partial Regex Placeholder();
}
