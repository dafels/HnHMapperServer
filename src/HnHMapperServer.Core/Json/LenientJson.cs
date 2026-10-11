using System.Text;
using System.Text.Json;

namespace HnHMapperServer.Core.Json;

/// <summary>
/// Repairs JSON-shaped payloads that contain bare (unquoted) value tokens by quoting them.
/// Current Haven client builds (Hurricane) carry their shared-marker ids as a UID type whose
/// toString() is unsigned base-16, and the client-side org.json writes any Number verbatim —
/// producing tokens like {"id":531a9f2e44c81b07} that strict parsers reject.
/// </summary>
public static class LenientJson
{
    /// <summary>
    /// Quotes bare tokens that are not valid JSON literals or numbers. Content inside string
    /// literals is never touched. Returns the original instance when nothing needed repair; it
    /// runs on every marker payload, so a valid one costs one scan and no copy (the builder is
    /// only allocated at the first bare token).
    /// </summary>
    public static string QuoteBareTokens(string json)
    {
        StringBuilder? sb = null;   // json[..copied] is already in sb
        var copied = 0;
        var inString = false;
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];
            if (inString)
            {
                if (c == '\\')
                    i++;            // skip the escaped character
                else if (c == '"')
                    inString = false;
                i++;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                i++;
                continue;
            }
            if (IsStructural(c) || char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < json.Length && json[i] != '"' && !IsStructural(json[i]) && !char.IsWhiteSpace(json[i]))
                i++;
            var token = json.AsSpan(start, i - start);
            if (IsJsonPrimitive(token))
                continue;

            sb ??= new StringBuilder(json.Length + 32);
            sb.Append(json, copied, start - copied).Append('"');
            AppendEscaped(sb, token);
            sb.Append('"');
            copied = i;
        }
        return sb is null ? json : sb.Append(json, copied, json.Length - copied).ToString();
    }

    /// <summary>
    /// Parses a possibly lenient payload in one pass: bare tokens are quoted first, so the
    /// serializer only throws for structurally broken JSON (it used to parse strictly, throw,
    /// repair and parse again — two first-chance exceptions per current Hurricane marker post).
    /// <paramref name="repaired"/> reports whether quoting changed the input.
    /// </summary>
    /// <exception cref="JsonException">The payload is not JSON even after quoting.</exception>
    public static T? Deserialize<T>(string json, JsonSerializerOptions options, out bool repaired)
    {
        var normalized = QuoteBareTokens(json);
        repaired = !ReferenceEquals(normalized, json);
        return JsonSerializer.Deserialize<T>(normalized, options);
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
