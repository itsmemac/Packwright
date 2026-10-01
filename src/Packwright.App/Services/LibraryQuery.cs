using System.Globalization;
using Packwright.App.Models;

namespace Packwright.App.Services;

/// <summary>
/// The library search grammar: plain words, quoted phrases, <c>field:value</c> terms
/// (<c>title id content category role region version fw feature path size</c>), <c>-exclude</c>,
/// <c>|</c> for OR and <c>=</c> after the colon for an exact match (<c>id:=PPSA12345</c>).
/// Comparisons work on size and version (<c>size:&gt;50GB</c>, <c>version:&gt;=01.05</c>).
/// </summary>
public sealed class LibraryQuery
{
    private sealed record Term(string Field, string Value, bool Exclude, bool Exact, string Op);

    private readonly List<List<Term>> _alternatives = [];

    public List<string> Problems { get; } = [];

    public bool IsEmpty => _alternatives.Count == 0 || _alternatives.All(group => group.Count == 0);

    public static LibraryQuery Parse(string? text)
    {
        var query = new LibraryQuery();
        var current = new List<Term>();
        query._alternatives.Add(current);
        foreach (string token in Tokenize(text ?? string.Empty))
        {
            if (token == "|")
            {
                current = [];
                query._alternatives.Add(current);
                continue;
            }
            string body = token;
            bool exclude = body.Length > 1 && body[0] == '-';
            if (exclude) body = body[1..];
            string field = string.Empty;
            int colon = body.IndexOf(':');
            if (colon > 0 && IsField(body[..colon]))
            {
                field = body[..colon].ToLowerInvariant();
                body = body[(colon + 1)..];
            }
            else if (colon > 0 && body[..colon].All(char.IsLetter) && !body.Contains(' '))
            {
                query.Problems.Add(body[..colon]);
            }
            string op = string.Empty;
            bool exact = false;
            if (body.StartsWith('=')) { exact = true; body = body[1..]; }
            else
            {
                foreach (string candidate in new[] { ">=", "<=", ">", "<" })
                    if (body.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; body = body[candidate.Length..]; break; }
            }
            if (body.Length == 0) continue;
            current.Add(new Term(field, body, exclude, exact, op));
        }
        return query;
    }

    public bool Matches(LibraryItem item)
    {
        if (IsEmpty) return true;
        return _alternatives.Any(group => group.All(term => Evaluate(term, item)));
    }

    private bool Evaluate(Term term, LibraryItem item)
    {
        bool result = term.Field switch
        {
            "" => Any(term, item.Title, item.TitleId, item.ContentId, item.Version, item.Category, item.Path,
                item.Region, item.Format),
            "title" => Text(term, item.Title),
            "id" => Text(term, item.TitleId),
            "content" => Text(term, item.ContentId),
            "category" => Text(term, item.Category),
            "role" => Text(term, item.Role),
            "region" => Text(term, item.Region),
            "format" or "source" => Text(term, item.Format),
            "path" => Text(term, item.Path),
            "version" => Compare(term, item.Version),
            "fw" => Compare(term, item.Firmware),
            "feature" => item.Game.DeclaredFeatures.Any(feature => Text(term, feature)),
            "missing" => item.HasMissing,
            "size" => CompareSize(term, item.SizeBytes),
            _ => true
        };
        return term.Exclude ? !result : result;
    }

    private static bool Any(Term term, params string[] values) => values.Any(value => Text(term, value));

    private static bool Text(Term term, string value) => term.Exact
        ? value.Equals(term.Value, StringComparison.OrdinalIgnoreCase)
        : value.Contains(term.Value, StringComparison.OrdinalIgnoreCase);

    private static bool Compare(Term term, string value)
    {
        if (term.Op.Length == 0) return Text(term, value);
        int order = CompareVersions(value, term.Value);
        return Apply(term.Op, order);
    }

    private static bool CompareSize(Term term, long bytes)
    {
        if (!TryParseSize(term.Value, out long wanted)) return false;
        return term.Op.Length == 0 ? bytes == wanted : Apply(term.Op, bytes.CompareTo(wanted));
    }

    private static bool Apply(string op, int order) => op switch
    {
        ">" => order > 0,
        ">=" => order >= 0,
        "<" => order < 0,
        "<=" => order <= 0,
        _ => order == 0
    };

    private static int CompareVersions(string left, string right)
    {
        string[] a = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] b = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            bool okA = i < a.Length && double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            bool okB = i < b.Length && double.TryParse(b[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            double x = okA ? double.Parse(a[i], CultureInfo.InvariantCulture) : 0;
            double y = okB ? double.Parse(b[i], CultureInfo.InvariantCulture) : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();
        int split = text.Length;
        while (split > 0 && !char.IsDigit(text[split - 1]) && text[split - 1] != '.') split--;
        if (!double.TryParse(text[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return false;
        double factor = text[split..].Trim().ToUpperInvariant() switch
        {
            "" or "B" => 1,
            "K" or "KB" or "KIB" => 1024d,
            "M" or "MB" or "MIB" => 1024d * 1024,
            "G" or "GB" or "GIB" => 1024d * 1024 * 1024,
            "T" or "TB" or "TIB" => 1024d * 1024 * 1024 * 1024,
            _ => -1
        };
        if (factor < 0) return false;
        bytes = (long)(number * factor);
        return true;
    }

    private static bool IsField(string name) => name.ToLowerInvariant() is "title" or "id" or "content" or "category"
        or "role" or "region" or "format" or "source" or "path" or "version" or "fw" or "feature" or "size";

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char ch in text)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (token.Length > 0) { yield return token.ToString(); token.Clear(); }
                continue;
            }
            if (!quoted && ch == '|')
            {
                if (token.Length > 0) { yield return token.ToString(); token.Clear(); }
                yield return "|";
                continue;
            }
            token.Append(ch);
        }
        if (token.Length > 0) yield return token.ToString();
    }
}
