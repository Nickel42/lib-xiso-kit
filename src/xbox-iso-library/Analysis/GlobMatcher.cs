using System.Text;
using System.Text.RegularExpressions;

namespace XboxIsoLibrary.Analysis;

/// <summary>
/// Case-insensitive glob matching for image paths ('/'-separated, rooted at '/').
/// Supports <c>*</c> (within a segment), <c>?</c>, <c>**</c> (any number of segments) and <c>[abc]</c>.
/// Patterns without a leading '/' or "**/" match at any depth, so "Demos/**" matches "/Demos/x" and "/A/Demos/x".
/// A pattern ending in "/**" also matches the directory itself.
/// </summary>
public sealed class GlobMatcher
{
    private readonly (string Pattern, Regex Regex)[] _patterns;

    public GlobMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (p, Compile(p)))
            .ToArray();
    }

    public bool IsEmpty => _patterns.Length == 0;

    /// <summary>True when any pattern matches <paramref name="path"/>.</summary>
    public bool IsMatch(string path) => FirstMatch(path) is not null;

    /// <summary>The first pattern matching <paramref name="path"/>, or null.</summary>
    public string? FirstMatch(string path)
    {
        string p = Normalize(path);
        foreach (var (pattern, regex) in _patterns)
        {
            if (regex.IsMatch(p))
            {
                return pattern;
            }
        }
        return null;
    }

    internal static string Normalize(string path)
    {
        string p = path.Replace('\\', '/');
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }
        return p.TrimEnd('/');
    }

    private static Regex Compile(string glob)
    {
        string g = glob.Trim().Replace('\\', '/');
        if (g.EndsWith('/'))
        {
            g += "**";
        }

        bool anchored = g.StartsWith('/');
        g = g.TrimStart('/');

        var sb = new StringBuilder("^");
        sb.Append(anchored ? "/" : "(?:/|/.*/)");

        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            if (c == '*')
            {
                bool doubleStar = i + 1 < g.Length && g[i + 1] == '*';
                if (doubleStar)
                {
                    bool atEnd = i + 2 >= g.Length;
                    bool followedBySlash = i + 2 < g.Length && g[i + 2] == '/';
                    bool precededBySlash = i == 0 || g[i - 1] == '/';
                    if (atEnd && precededBySlash)
                    {
                        // "dir/**" matches the directory itself and everything below it.
                        if (sb.Length > 0 && sb[^1] == '/')
                        {
                            sb.Length--;
                        }
                        sb.Append("(?:/.*)?");
                        i++;
                        continue;
                    }
                    if (followedBySlash && precededBySlash)
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                        continue;
                    }
                    sb.Append(".*");
                    i++;
                    continue;
                }
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if (c == '[')
            {
                int close = g.IndexOf(']', i + 1);
                if (close < 0)
                {
                    sb.Append(@"\[");
                    continue;
                }
                string body = g.Substring(i + 1, close - i - 1);
                if (body.StartsWith('!'))
                {
                    body = "^" + body[1..];
                }
                sb.Append('[').Append(body.Replace(@"\", @"\\")).Append(']');
                i = close;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
