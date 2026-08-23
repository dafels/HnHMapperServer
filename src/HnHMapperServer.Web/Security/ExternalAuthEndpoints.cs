using System.Security.Claims;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace HnHMapperServer.Web.Security;

/// <summary>
/// External sign-in (Steam OpenID, Discord OAuth) - provider-agnostic.
///
/// <list type="bullet">
/// <item><c>GET  /auth/{provider}/challenge?invite=&amp;returnUrl=</c> - starts the round-trip. State (invite code, return URL)
/// rides in AuthenticationProperties.Items, i.e. the correlation cookie - tamper-proof.</item>
/// <item><c>POST /auth/{provider}/link</c> / <c>unlink</c> - antiforgery-protected form posts from the account page. A bare GET
/// would let a crafted link start a linking flow in a victim's browser.</item>
/// <item><c>GET  /auth/{provider}/callback</c> - the provider handler has already validated the assertion and signed the
/// external cookie; this resolves/creates the account, redeems a carried invite IN-PROCESS (no HnH.Auth cookie
/// exists yet, so a proxied API call could not authenticate) and issues the real session cookie.</item>
/// </list>
/// Only mapped when at least one provider is configured.
/// </summary>
public static class ExternalAuthEndpoints
{
    private const string ItemInvite = "invite";
    private const string ItemReturnUrl = "returnUrl";
    private const string ItemMode = "mode";
    private const string ModeLink = "link";

    public static void MapExternalAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/{provider}/challenge", Challenge).DisableAntiforgery();
        app.MapPost("/auth/{provider}/link", StartLink).RequireAuthorization();
        app.MapPost("/auth/{provider}/unlink", Unlink).RequireAuthorization();
        app.MapGet("/auth/{provider}/callback", Callback).DisableAntiforgery();
    }

    private static IResult Challenge(string provider, string? invite, string? returnUrl, ExternalAuthProviders providers)
    {
        var p = providers.Find(provider);
        if (p == null)
            return Results.NotFound();

        var props = new AuthenticationProperties { RedirectUri = $"/auth/{p.Scheme.ToLowerInvariant()}/callback" };
        if (!string.IsNullOrWhiteSpace(invite)) props.Items[ItemInvite] = invite.Trim();
        if (IsLocal(returnUrl)) props.Items[ItemReturnUrl] = returnUrl!;

        return Results.Challenge(props, new[] { p.Scheme });
    }

    private static async Task<IResult> StartLink(string provider, HttpContext context, ExternalAuthProviders providers, IAntiforgery antiforgery)
    {
        var p = providers.Find(provider);
        if (p == null)
            return Results.NotFound();

        if (!await TryValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid request token.");

        var props = new AuthenticationProperties { RedirectUri = $"/auth/{p.Scheme.ToLowerInvariant()}/callback" };
        props.Items[ItemMode] = ModeLink;
        props.Items[ItemReturnUrl] = "/account";

        return Results.Challenge(props, new[] { p.Scheme });
    }

    private static async Task<IResult> Unlink(
        string provider,
        HttpContext context,
        ExternalAuthProviders providers,
        IAntiforgery antiforgery,
        UserManager<ApplicationUser> userManager,
        IExternalUserProvisioner provisioner)
    {
        var p = providers.Find(provider);
        if (p == null)
            return Results.NotFound();

        if (!await TryValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid request token.");

        var user = await userManager.GetUserAsync(context.User);
        if (user == null)
            return Results.Redirect("/login");

        var outcome = await provisioner.UnlinkAsync(user, p.Scheme);
        return Results.LocalRedirect($"/account?unlink={outcome.ToString().ToLowerInvariant()}&provider={p.Scheme.ToLowerInvariant()}");
    }

    private static async Task<IResult> Callback(
        string provider,
        HttpContext context,
        ExternalAuthProviders providers,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IExternalUserProvisioner provisioner,
        ITenantMembershipService membership,
        HnHMapperServer.Infrastructure.Data.ApplicationDbContext db,
        ILogger<Program> logger)
    {
        var p = providers.Find(provider);
        if (p == null)
            return Results.NotFound();

        var errorRedirect = $"/login?error={p.Scheme.ToLowerInvariant()}";

        var auth = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
        if (!auth.Succeeded || auth.Principal == null)
        {
            logger.LogWarning("{Provider} callback without a valid external principal", p.Scheme);
            return Results.Redirect(errorRedirect);
        }

        var providerKey = auth.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var displayName = auth.Principal.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrEmpty(providerKey))
        {
            logger.LogWarning("{Provider} callback without a NameIdentifier claim", p.Scheme);
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            return Results.Redirect(errorRedirect);
        }

        var identity = new ExternalIdentity(p.Scheme, providerKey, displayName);
        var items = auth.Properties?.Items ?? new Dictionary<string, string?>();
        items.TryGetValue(ItemMode, out var mode);
        items.TryGetValue(ItemInvite, out var invite);
        items.TryGetValue(ItemReturnUrl, out var returnUrl);

        // External cookie has served its purpose either way
        await context.SignOutAsync(IdentityConstants.ExternalScheme);

        // ---------------- Link mode: attach the identity to the signed-in account ----------------
        if (mode == ModeLink)
        {
            var current = context.User.Identity?.IsAuthenticated == true ? await userManager.GetUserAsync(context.User) : null;
            if (current == null)
                return Results.Redirect("/login?returnUrl=%2Faccount");

            var outcome = await provisioner.LinkAsync(current, identity);
            return Results.LocalRedirect($"/account?link={outcome.ToString().ToLowerInvariant()}&provider={p.Scheme.ToLowerInvariant()}");
        }

        // ---------------- Sign-in mode: find or create ----------------
        var user = await provisioner.FindAsync(identity);
        var isNew = user == null;
        if (user == null)
        {
            try
            {
                user = await provisioner.ProvisionAsync(identity);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not provision a user for {Provider}", p.Scheme);
                return Results.Redirect(errorRedirect);
            }
        }

        // Carried invite: join in-process so the first real cookie already carries the map's claims
        string? inviteFailure = null;
        if (!string.IsNullOrWhiteSpace(invite))
        {
            var redeem = await membership.RedeemInvitationAsync(invite, user.Id);
            if (!redeem.Succeeded)
            {
                inviteFailure = redeem.Error;
                logger.LogInformation("Invite carried through {Provider} sign-in could not be redeemed for {UserId}: {Error}", p.Scheme, user.Id, redeem.Error);
            }
        }

        user.LastLoginAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

        await signInManager.SignInAsync(user, isPersistent: true);
        logger.LogInformation("User {UserId} signed in via {Provider}{New}", user.Id, p.Scheme, isNew ? " (new account)" : string.Empty);

        if (inviteFailure != null && !string.IsNullOrWhiteSpace(invite))
            return Results.LocalRedirect($"/invite/{Uri.EscapeDataString(invite)}");   // landing page shows the precise reason

        if (IsLocal(returnUrl) && !returnUrl!.StartsWith("/invite/", StringComparison.OrdinalIgnoreCase))
            return Results.LocalRedirect(returnUrl);

        var activeTenant = await ActiveTenantMembershipResolver.ResolveTenantIdAsync(db, user.Id, user.ActiveTenantId);
        return Results.LocalRedirect(activeTenant != null ? "/" : "/tenant/select");
    }

    private static async Task<bool> TryValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static bool IsLocal(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal);
}
