using System.Globalization;
using System.Text;

namespace CthExtract;

/// <summary>
/// java.util.Properties.load(Reader) for a UTF-8 file: comments (# !), key/value separators (= : whitespace), line
/// continuations, and escapes (\t \n \r \f \uXXXX, \x = x). The mod writes crafttheft.properties with Properties.store
/// (which escapes ':' and '\'), Melty writes plain "key=value" lines with forward slashes; both read the same.
/// </summary>
internal static class JavaProperties
{
    public static Dictionary<string, string> Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return Parse(text);
    }

    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart(' ', '\t', '\f');
            if (line.Length == 0 || line[0] == '#' || line[0] == '!') continue;
            // join continuation lines (odd number of trailing backslashes)
            var logical = new StringBuilder();
            while (true)
            {
                int trailing = 0;
                for (int k = line.Length - 1; k >= 0 && line[k] == '\\'; k--) trailing++;
                if (trailing % 2 == 1 && i + 1 < lines.Length)
                {
                    logical.Append(line, 0, line.Length - 1);
                    line = lines[++i].TrimStart(' ', '\t', '\f');
                    continue;
                }
                if (trailing % 2 == 1) line = line[..^1];
                logical.Append(line);
                break;
            }
            var l = logical.ToString();
            int p = 0;
            var key = new StringBuilder();
            while (p < l.Length)
            {
                char c = l[p];
                if (c == '\\' && p + 1 < l.Length) { p = Unescape(l, p, key); continue; }
                if (c is '=' or ':' or ' ' or '\t' or '\f') break;
                key.Append(c); p++;
            }
            while (p < l.Length && l[p] is ' ' or '\t' or '\f') p++;
            if (p < l.Length && l[p] is '=' or ':') p++;
            while (p < l.Length && l[p] is ' ' or '\t' or '\f') p++;
            var value = new StringBuilder();
            while (p < l.Length)
            {
                if (l[p] == '\\' && p + 1 < l.Length) { p = Unescape(l, p, value); continue; }
                value.Append(l[p]); p++;
            }
            result[key.ToString()] = value.ToString();
        }
        return result;
    }

    /// <summary>l[p] is a backslash; append the unescaped char(s) and return the next position.</summary>
    private static int Unescape(string l, int p, StringBuilder sb)
    {
        char n = l[p + 1];
        switch (n)
        {
            case 't': sb.Append('\t'); return p + 2;
            case 'n': sb.Append('\n'); return p + 2;
            case 'r': sb.Append('\r'); return p + 2;
            case 'f': sb.Append('\f'); return p + 2;
            case 'u' when p + 6 <= l.Length && int.TryParse(l.AsSpan(p + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp):
                sb.Append((char)cp); return p + 6;
            default: sb.Append(n); return p + 2;
        }
    }
}
