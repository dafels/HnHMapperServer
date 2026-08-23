using System.Text.RegularExpressions;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// The one place invite links are built and parsed. Links are always the short form <c>{base}/invite/{code}</c>;
/// pasted input may be that link, the legacy <c>/register?invite=</c> form, or the bare code.
/// </summary>
public static partial class InviteLinks
{
    public static string Build(string baseUri, string inviteCode) =>
        $"{baseUri.TrimEnd('/')}/invite/{Uri.EscapeDataString(inviteCode)}";

    /// <summary>Extracts an invite code from whatever the user pasted; null when nothing code-like is there.</summary>
    public static string? TryExtractCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = input.Trim();

        var pathMatch = InvitePathPattern().Match(text);
        if (pathMatch.Success)
            return Uri.UnescapeDataString(pathMatch.Groups["code"].Value);

        var queryMatch = InviteQueryPattern().Match(text);
        if (queryMatch.Success)
            return Uri.UnescapeDataString(queryMatch.Groups["code"].Value);

        // Bare code: our codes are GUIDs, but accept any reasonable token (the server is the judge)
        return CodePattern().IsMatch(text) ? text : null;
    }

    [GeneratedRegex(@"/invite/(?<code>[A-Za-z0-9\-_%]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InvitePathPattern();

    [GeneratedRegex(@"[?&]invite=(?<code>[A-Za-z0-9\-_%]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InviteQueryPattern();

    [GeneratedRegex(@"^[A-Za-z0-9\-_]{8,64}$")]
    private static partial Regex CodePattern();
}
