using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Api.Endpoints;

public static class IdentityEndpoints
{
	public static void MapIdentityEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/auth");

		group.MapPost("/login", Login).RequireRateLimiting("Login").DisableAntiforgery();
		group.MapPost("/logout", Logout).DisableAntiforgery();
		group.MapPost("/register", Register).RequireRateLimiting("Register").DisableAntiforgery();
		group.MapPost("/select-tenant", SelectTenant).RequireAuthorization().DisableAntiforgery();
		group.MapGet("/me", Me).DisableAntiforgery();
		group.MapGet("/tenants", GetUserTenants).RequireAuthorization().DisableAntiforgery();

		// User self-service token endpoints
		app.MapGet("/api/user/tokens", GetOwnTokens)
			.RequireAuthorization()
			.DisableAntiforgery();
		app.MapPost("/api/user/tokens", CreateOwnToken)
			.RequireAuthorization()
			.DisableAntiforgery();

		// User self-service password change endpoint
		group.MapPost("/change-password", ChangePassword)
			.RequireAuthorization()
			.DisableAntiforgery();
	}

	private static async Task<IResult> Login(
		[FromBody] LoginRequest request,
		SignInManager<ApplicationUser> signInManager,
		UserManager<ApplicationUser> userManager,
		ApplicationDbContext db)
	{
		if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
			return Results.BadRequest(new { error = "Missing username or password" });

		var user = await userManager.FindByNameAsync(request.Username);
		if (user == null)
			return Results.Unauthorized();

		var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: true, lockoutOnFailure: false);
		if (!result.Succeeded)
			return Results.Unauthorized();

		// Get all tenants user belongs to with roles and permissions
		var tenantUsers = await db.TenantUsers
			.IgnoreQueryFilters()
			.Where(tu => tu.UserId == user.Id)
			.ToListAsync();

		var tenants = new List<object>();
		foreach (var tenantUser in tenantUsers)
		{
			// Skip pending approval users
			if (tenantUser.JoinedAt == default)
				continue;

			var tenant = await db.Tenants
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(t => t.Id == tenantUser.TenantId);

			if (tenant == null)
				continue;

			var permissions = await db.TenantPermissions
				.IgnoreQueryFilters()
				.Where(tp => tp.TenantUserId == tenantUser.Id)
				.Select(tp => tp.Permission.ToClaimValue())
				.ToListAsync();

			tenants.Add(new
			{
				tenantId = tenant.Id,
				tenantName = tenant.Name,
				role = tenantUser.Role.ToClaimValue(),
				permissions = permissions
			});
		}

		// Check if user is unassigned (no tenants)
		var hasNoTenant = tenants.Count == 0;

		return Results.Ok(new
		{
			userId = user.Id,
			username = user.UserName,
			tenants = tenants,
			hasNoTenant = hasNoTenant
		});
	}

	private static async Task<IResult> Logout(SignInManager<ApplicationUser> signInManager)
	{
		await signInManager.SignOutAsync();
		return Results.Ok();
	}

	/// <summary>
	/// POST /api/auth/register - creates a password account.
	/// With a valid invite link the user joins that tenant IMMEDIATELY (the link is the approval) - this works
	/// even when open self-registration is disabled. Without a link, registration is gated by
	/// SelfRegistration:Enabled and the new account lands on the create-or-join screen.
	/// </summary>
	private static async Task<IResult> Register(
		[FromBody] RegisterRequest request,
		UserManager<ApplicationUser> userManager,
		IConfiguration configuration,
		IInvitationService invitationService,
		ITenantMembershipService membershipService,
		IAuthSettingsStore authSettings,
		IAuditService auditService,
		ILogger<object> logger)
	{
		// Validate inputs
		if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
			return Results.BadRequest(new { error = "Username and password are required" });

		// Password validation (6+ chars minimum)
		if (request.Password.Length < 6)
			return Results.BadRequest(new { error = "Password must be at least 6 characters long" });

		var discordName = request.DiscordName?.Trim() ?? string.Empty;
		if (discordName.Length < 2 || discordName.Length > 32)
			return Results.BadRequest(new { error = "Discord name is required (2-32 characters)" });

		var inviteCode = request.InviteCode?.Trim();
		var hasInvitation = !string.IsNullOrWhiteSpace(inviteCode);

		if (hasInvitation)
		{
			// Validate BEFORE creating the account so a dead link never leaves an orphan user behind
			var invitation = await invitationService.ValidateInvitationAsync(inviteCode!);
			if (!invitation.IsValid)
				return Results.BadRequest(new { error = invitation.ErrorMessage ?? "Invitation is invalid or expired" });
		}
		else
		{
			// Superadmin-managed (SuperAdmin → Sign-in & onboarding); deployment config only seeds the default
			var policy = await authSettings.GetPolicyAsync();
			if (!policy.SelfRegistrationEnabled)
				return Results.Json(new { error = "Registration requires an invitation link" }, statusCode: StatusCodes.Status403Forbidden);
		}

		// Check if username already exists
		var existingUser = await userManager.FindByNameAsync(request.Username);
		if (existingUser != null)
			return Results.Conflict(new { error = "Username already exists" });

		var user = new ApplicationUser
		{
			UserName = request.Username,
			Email = string.Empty,
			DiscordName = discordName,
			CreatedAt = DateTime.UtcNow,
			RegistrationSource = RegistrationSources.Password
		};
		var result = await userManager.CreateAsync(user, request.Password);

		if (!result.Succeeded)
		{
			var errors = string.Join(", ", result.Errors.Select(e => e.Description));
			return Results.BadRequest(new { error = errors });
		}

		await auditService.LogAsync(new AuditEntry
		{
			UserId = user.Id,
			Action = "UserRegistered",
			EntityType = "User",
			EntityId = user.Id,
			NewValue = $"source={RegistrationSources.Password}; invite={(hasInvitation ? "yes" : "no")}"
		});

		if (!hasInvitation)
		{
			// No invite: the account exists, the welcome screen offers "create a map" / "join with a code"
			return Results.Created($"/api/auth/users/{user.UserName}", new
			{
				userId = user.Id,
				username = user.UserName,
				joined = false,
				awaitingAssignment = true,
				message = "Registration successful."
			});
		}

		var redeem = await membershipService.RedeemInvitationAsync(inviteCode!, user.Id);
		if (!redeem.Succeeded)
		{
			// The link died between validation and redemption (revoked / last use taken). The account still
			// exists and can redeem another link from the welcome screen.
			logger.LogWarning("User {UserId} registered but invite redemption failed: {Error}", user.Id, redeem.Error);
			return Results.Created($"/api/auth/users/{user.UserName}", new
			{
				userId = user.Id,
				username = user.UserName,
				joined = false,
				awaitingAssignment = true,
				inviteError = redeem.Error,
				message = "Account created, but the invitation could no longer be used."
			});
		}

		return Results.Created($"/api/auth/users/{user.UserName}", new
		{
			userId = user.Id,
			username = user.UserName,
			joined = true,
			tenantId = redeem.TenantId,
			tenantName = redeem.TenantName,
			permissions = redeem.Permissions.Select(p => p.ToClaimValue()).ToList(),
			message = $"Welcome to {redeem.TenantName}."
		});
	}

	private static IResult Me(ClaimsPrincipal user)
	{
		var isAuth = user.Identity?.IsAuthenticated ?? false;
		if (!isAuth)
			return Results.Json(new { authenticated = false }, statusCode: 401);

		var username = user.Identity?.Name ?? string.Empty;
		var roles = user.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToArray();
		var auths = user.Claims.Where(c => c.Type == "auth").Select(c => c.Value).ToArray();
		return Results.Ok(new { authenticated = true, username, roles, auths });
	}

	/// <summary>
	/// GET /api/auth/tenants - Get all tenants the current user belongs to
	/// </summary>
	private static async Task<IResult> GetUserTenants(
		ClaimsPrincipal user,
		UserManager<ApplicationUser> userManager,
		ApplicationDbContext db)
	{
		var userName = user.Identity?.Name;
		if (string.IsNullOrEmpty(userName))
			return Results.Unauthorized();

		var identityUser = await userManager.FindByNameAsync(userName);
		if (identityUser == null)
			return Results.Unauthorized();

		// Get all tenants user belongs to (approved only)
		var tenantUsers = await db.TenantUsers
			.IgnoreQueryFilters()
			.Include(tu => tu.Permissions)
			.Where(tu => tu.UserId == identityUser.Id && tu.JoinedAt != default)
			.OrderBy(tu => tu.JoinedAt)
			.ToListAsync();

		var tenants = new List<object>();
		foreach (var tenantUser in tenantUsers)
		{
			var tenant = await db.Tenants
				.IgnoreQueryFilters()
				.FirstOrDefaultAsync(t => t.Id == tenantUser.TenantId);

			if (tenant == null || !tenant.IsActive)
				continue;

			tenants.Add(new
			{
				tenantId = tenant.Id,
				tenantName = tenant.Name,
				role = tenantUser.Role.ToClaimValue(),
				permissions = tenantUser.Permissions.Select(p => p.Permission.ToClaimValue()).ToList(),
				storageUsageMB = tenant.CurrentStorageMB,
				storageQuotaMB = tenant.StorageQuotaMB,
				isActive = tenant.IsActive,
				joinedAt = tenantUser.JoinedAt,
				joinSource = tenantUser.JoinSource
			});
		}

		return Results.Ok(tenants);
	}

	/// <summary>
	/// POST /api/auth/select-tenant - persists the user's active tenant. Programmatic use only: this process
	/// cannot re-issue the browser cookie (the Web app owns it - see Web /api/tenant/select), but the shared
	/// claims factory reads ActiveTenantId on the next revalidation, so API-side tenant context follows within
	/// the validation interval.
	/// </summary>
	private static async Task<IResult> SelectTenant(
		[FromBody] SelectTenantRequest request,
		ClaimsPrincipal user,
		UserManager<ApplicationUser> userManager,
		ApplicationDbContext db)
	{
		if (string.IsNullOrWhiteSpace(request.TenantId))
			return Results.BadRequest(new { error = "Tenant ID is required" });

		var userName = user.Identity?.Name;
		if (string.IsNullOrEmpty(userName))
			return Results.Unauthorized();

		var identityUser = await userManager.FindByNameAsync(userName);
		if (identityUser == null)
			return Results.Unauthorized();

		// Verify user is an approved member of an active tenant
		var tenantUser = await db.TenantUsers
			.IgnoreQueryFilters()
			.FirstOrDefaultAsync(tu => tu.UserId == identityUser.Id && tu.TenantId == request.TenantId);

		if (tenantUser == null || tenantUser.JoinedAt == default)
			return Results.StatusCode(403); // not a member, or legacy pending approval

		var tenantActive = await db.Tenants
			.IgnoreQueryFilters()
			.AnyAsync(t => t.Id == request.TenantId && t.IsActive);
		if (!tenantActive)
			return Results.StatusCode(403);

		var permissions = await db.TenantPermissions
			.IgnoreQueryFilters()
			.Where(tp => tp.TenantUserId == tenantUser.Id)
			.Select(tp => tp.Permission)
			.ToListAsync();

		if (!string.Equals(identityUser.ActiveTenantId, request.TenantId, StringComparison.Ordinal))
		{
			identityUser.ActiveTenantId = request.TenantId;
			await userManager.UpdateAsync(identityUser);
		}

		return Results.Ok(new
		{
			selectedTenant = request.TenantId,
			role = tenantUser.Role.ToClaimValue(),
			permissions = permissions.Select(p => p.ToClaimValue()).ToList()
		});
	}

	// GET /api/user/tokens - list own tokens (no plaintext)
	private static async Task<IResult> GetOwnTokens(
		ClaimsPrincipal user,
		ApplicationDbContext db,
		UserManager<ApplicationUser> userManager,
		IConfigRepository configRepository,
		HttpContext httpContext)
	{
		var userName = user.Identity?.Name;
		if (string.IsNullOrEmpty(userName)) return Results.Unauthorized();
		var identityUser = await userManager.FindByNameAsync(userName);
		if (identityUser == null) return Results.Unauthorized();

		// Get tenant ID from claims (user must have selected a tenant after login)
		var tenantId = user.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
		if (string.IsNullOrEmpty(tenantId))
		{
			return Results.BadRequest(new { error = "Unable to determine your tenant. Please logout and login again." });
		}

		// Get the prefix configuration for URL construction (GLOBAL setting)
		var prefix = await configRepository.GetGlobalValueAsync("prefix") ?? string.Empty;

		// Get permissions from TenantPermissions table
		var tenantUser = await db.TenantUsers
			.IgnoreQueryFilters()
			.Include(tu => tu.Permissions)
			.FirstOrDefaultAsync(tu => tu.UserId == identityUser.Id && tu.TenantId == tenantId);

		var permissions = tenantUser?.Permissions
			.Select(p => p.Permission.ToClaimValue())
			.ToList() ?? new List<string>();

		var tokens = await db.Tokens
			.Where(t => t.UserId == identityUser.Id && t.TenantId == tenantId)
			.ToListAsync();
		var items = tokens.Select(t => new
		{
			Value = t.DisplayToken ?? t.Id, // Return full token with tenant prefix
			Permissions = permissions,
			Url = string.IsNullOrEmpty(prefix)
				? $"/client/{t.DisplayToken ?? t.Id}"
				: $"{prefix}/client/{t.DisplayToken ?? t.Id}"
		}).ToList();

		return Results.Ok(items);
	}

	// POST /api/user/tokens - create token (display once)
	private static async Task<IResult> CreateOwnToken(
		ClaimsPrincipal user,
		ApplicationDbContext db,
		UserManager<ApplicationUser> userManager,
		IConfigRepository configRepository,
		ITokenService tokenService)
	{
		var userName = user.Identity?.Name;
		if (string.IsNullOrEmpty(userName)) return Results.Unauthorized();
		var identityUser = await userManager.FindByNameAsync(userName);
		if (identityUser == null) return Results.Unauthorized();

		// Get tenant ID from claims (user must have selected a tenant after login)
		var tenantId = user.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
		if (string.IsNullOrEmpty(tenantId))
		{
			return Results.BadRequest(new { error = "Unable to determine your tenant. Please logout and login again." });
		}

		// Use TokenService to create token with tenant prefix
		var tokenName = $"Self-{DateTime.UtcNow:yyyyMMddHHmmss}";
		var fullToken = await tokenService.CreateTokenAsync(
			tenantId,
			identityUser.Id,
			tokenName,
			"upload");

		// Get the prefix configuration for URL construction (GLOBAL setting)
		var prefix = await configRepository.GetGlobalValueAsync("prefix") ?? string.Empty;
		var url = string.IsNullOrEmpty(prefix) ? $"/client/{fullToken}" : $"{prefix}/client/{fullToken}";
		return Results.Ok(new { Success = true, Token = fullToken, Url = url });
	}

	// POST /api/auth/change-password - change own password
	private static async Task<IResult> ChangePassword(
		[FromBody] ChangePasswordRequest request,
		ClaimsPrincipal user,
		UserManager<ApplicationUser> userManager,
		ILogger<object> logger)
	{
		// Validate inputs
		if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
			return Results.BadRequest(new { error = "Current password and new password are required" });

		// Validate new password length
		if (request.NewPassword.Length < 6)
			return Results.BadRequest(new { error = "New password must be at least 6 characters long" });

		// Get current user
		var userName = user.Identity?.Name;
		if (string.IsNullOrEmpty(userName))
			return Results.Unauthorized();

		var identityUser = await userManager.FindByNameAsync(userName);
		if (identityUser == null)
			return Results.Unauthorized();

		// Attempt to change password
		var result = await userManager.ChangePasswordAsync(identityUser, request.CurrentPassword, request.NewPassword);

		if (!result.Succeeded)
		{
			var errors = string.Join(", ", result.Errors.Select(e => e.Description));
			logger.LogWarning("Password change failed for user {Username}: {Errors}", userName, errors);
			return Results.BadRequest(new { error = errors });
		}

		logger.LogInformation("Password changed successfully for user {Username}", userName);
		return Results.Ok(new { message = "Password changed successfully" });
	}

	private static string ComputeSha256(string value)
	{
		using var sha = SHA256.Create();
		var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}

	public sealed class LoginRequest
	{
		public string Username { get; set; } = string.Empty;
		public string Password { get; set; } = string.Empty;
	}

	public sealed class RegisterRequest
	{
		public string Username { get; set; } = string.Empty;
		public string Password { get; set; } = string.Empty;
		public string InviteCode { get; set; } = string.Empty;
		public string DiscordName { get; set; } = string.Empty;
	}

	public sealed class SelectTenantRequest
	{
		public string TenantId { get; set; } = string.Empty;
	}

	public sealed class ChangePasswordRequest
	{
		public string CurrentPassword { get; set; } = string.Empty;
		public string NewPassword { get; set; } = string.Empty;
	}
}
