using System.Text.RegularExpressions;

namespace HnHMapperServer.Web.Components.Pages;

/// <summary>
/// Code-behind for the registration form's input patterns. [GeneratedRegex] needs a partial
/// method on a partial type, which a .razor @code block cannot declare.
/// </summary>
public partial class Register
{
    /// <summary>Allowed username characters — length is checked separately.</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_]+$")]
    private static partial Regex UsernameCharacters();
}
