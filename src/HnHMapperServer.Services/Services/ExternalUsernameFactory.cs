using System.Text;
using System.Text.RegularExpressions;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Turns an external display name (Steam persona, Discord username) into a valid, unique mapper username
/// (<c>^[a-zA-Z0-9_]{3,20}$</c>, the same rule the register form enforces).
/// </summary>
public static partial class ExternalUsernameFactory
{
    public const int MinLength = 3;
    public const int MaxLength = 20;
    public const string Fallback = "hearthling";
    private const int MaxAttempts = 50;

    /// <summary>Strips everything that is not [A-Za-z0-9_], pads/trims to the allowed length.</summary>
    public static string Sanitize(string? displayName)
    {
        var sb = new StringBuilder();
        foreach (var ch in displayName ?? string.Empty)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '.')
                sb.Append('_');
        }

        var name = CollapseUnderscores().Replace(sb.ToString(), "_").Trim('_');

        if (name.Length == 0)
            name = Fallback;
        if (name.Length < MinLength)
            name = (name + "___")[..MinLength];   // e.g. "ab" -> "ab_"
        if (name.Length > MaxLength)
            name = name[..MaxLength].TrimEnd('_');

        return name;
    }

    /// <summary>
    /// Finds a free username: the sanitized name, then name_2, name_3 ... and finally a random 4-digit suffix.
    /// <paramref name="existsAsync"/> answers "is this username taken?" (case-insensitively).
    /// </summary>
    public static async Task<string> MakeUniqueAsync(string? displayName, Func<string, Task<bool>> existsAsync)
    {
        var baseName = Sanitize(displayName);
        if (!await existsAsync(baseName))
            return baseName;

        for (var i = 2; i <= MaxAttempts; i++)
        {
            var candidate = WithSuffix(baseName, $"_{i}");
            if (!await existsAsync(candidate))
                return candidate;
        }

        for (var i = 0; i < MaxAttempts; i++)
        {
            var candidate = WithSuffix(baseName, $"_{Random.Shared.Next(1000, 10000)}");
            if (!await existsAsync(candidate))
                return candidate;
        }

        throw new InvalidOperationException($"Could not find a free username for '{baseName}'");
    }

    private static string WithSuffix(string baseName, string suffix)
    {
        var room = MaxLength - suffix.Length;
        var head = baseName.Length > room ? baseName[..room].TrimEnd('_') : baseName;
        return head + suffix;
    }

    [GeneratedRegex("_{2,}")]
    private static partial Regex CollapseUnderscores();
}
