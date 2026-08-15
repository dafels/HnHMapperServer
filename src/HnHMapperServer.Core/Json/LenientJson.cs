using System.Text;

namespace HnHMapperServer.Core.Json;

/// <summary>
/// Repairs JSON-shaped payloads that contain bare (unquoted) value tokens by quoting them.
/// Newer Haven client builds carry their shared-marker ids as a UID type whose toString()
/// is unsigned base-16, and the client-side org.json writes any Number verbatim — producing
/// tokens like {"id":531a9f2e44c81b07} that strict parsers reject.
/// </summary>
public static class LenientJson
{
    /// <summary>
    /// Quotes bare tokens that are not valid JSON literals or numbers. Content inside string
    /// literals is never touched. Returns the original instance when nothing needed repair.
    /// </summary>
    public static string QuoteBareTokens(string json)
    {
        var sb = new StringBuilder(json.Length + 32);
        var repaired = false;
        var inString = false;
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '"')
                    inString = false;
                i++;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (IsStructural(c) || char.IsWhiteSpace(c))
            {
                sb.Append(c);
                i++;
                continue;
            }

            var start = i;
            while (i < json.Length && json[i] != '"' && !IsStructural(json[i]) && !char.IsWhiteSpace(json[i]))
                i++;
            var token = json.AsSpan(start, i - start);
            if (IsJsonPrimitive(token))
            {
                sb.Append(token);
            }
            else
            {
                sb.Append('"');
                AppendEscaped(sb, token);
                sb.Append('"');
                repaired = true;
            }
        }
        return repaired ? sb.ToString() : json;
    }

    private static bool IsStructural(char c) => c is '{' or '}' or '[' or ']' or ',' or ':';

    private static bool IsJsonPrimitive(ReadOnlySpan<char> token) =>
        token is "true" or "false" or "null" || IsJsonNumber(token);

    private static bool IsJsonNumber(ReadOnlySpan<char> s)
    {
        var i = 0;
        if (i < s.Length && s[i] == '-')
            i++;
        if (i == s.Length)
            return false;
        if (s[i] == '0')
            i++;
        else if (s[i] is >= '1' and <= '9')
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                i++;
        else
            return false;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            if (i == s.Length || !char.IsAsciiDigit(s[i]))
                return false;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                i++;
            if (i == s.Length || !char.IsAsciiDigit(s[i]))
                return false;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                i++;
        }
        return i == s.Length;
    }

    private static void AppendEscaped(StringBuilder sb, ReadOnlySpan<char> token)
    {
        foreach (var c in token)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
    }
}
