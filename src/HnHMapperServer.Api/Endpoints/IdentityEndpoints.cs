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

namespace HnHMapperServer.Api.Endpoints;

public static class IdentityEndpoints
{
	public static void MapIdentityEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/auth");

		group.MapPost("/login", Login).DisableAntiforgery();
		group.MapPost("/logout", Logout).DisableAntiforgery();
		group.MapPost("/register", Register).DisableAntiforgery();
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

	private static async Task<IResult> Register(
		[FromBody] RegisterRequest request,
		UserManager<ApplicationUser> userManager,
		IConfiguration configuration,
		IInvitationService invitationService,
		ApplicationDbContext db)
	{
		// Validate inputs
		if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
			return Results.BadRequest(new { error = "Username and password are required" });

		// Invitation code is now optional
		// If provided: user assigned to tenant with pending approval
		// If not provided: user created but not assigned to any tenant (SuperAdmin assigns later)

		// Password validation (6+ chars minimum)
		if (request.Password.Length < 6)
			return Results.BadRequest(new { error = "Password must be at least 6 characters long" });

		var discordName = request.DiscordName?.Trim() ?? string.Empty;
		if (discordName.Length < 2 || discordName.Length > 32)
			return Results.BadRequest(new { error = "Discord name is required (2-32 characters)" });

		// Validate invitation code (if provided)
		bool hasInvitation = !string.IsNullOrWhiteSpace(request.InviteCode);
		HnHMapperServer.Core.DTOs.InvitationDto? usedInvitation = null;

		if (hasInvitation)
		{
			try
			{
				var invitation = await invitationService.ValidateInvitationAsync(request.InviteCode!);
				if (!invitation.IsValid)
					return Results.BadRequest(new { error = invitation.ErrorMessage ?? "Invalid invitation code" });
			}
			catch (Exception ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		}

		// Check if username already exists
		var existingUser = await userManager.FindByNameAsync(request.Username);
		if (existingUser != null)
			return Results.Conflict(new { error = "Username already exists" });

		// Create new user with Identity
		var user = new ApplicationUser
		{
			UserName = request.Username,
			Email = string.Empty,
			DiscordName = discordName,
			CreatedAt = DateTime.UtcNow
		};
		var result = await userManager.CreateAsync(user, request.Password);

		if (!result.Succeeded)
		{
			var errors = string.Join(", ", result.Errors.Select(e => e.Description));
			return Results.BadRequest(new { error = errors });
		}

		// If invitation code provided: assign user to tenant with pending approval
		if (hasInvitation)
		{
			// Use invitation code to create TenantUser with pending approval
			usedInvitation = await invitationService.UseInvitationAsync(request.InviteCode!, user.Id);

			// Create TenantUser entry with pending approval (JoinedAt = default)
			var tenantUser = new TenantUserEntity
			{
				TenantId = usedInvitation.TenantId,
				UserId = user.Id,
				Role = TenantRole.TenantUser,
				JoinedAt = default // Pending approval - will be set when approved
			};
			db.TenantUsers.Add(tenantUser);
			await db.SaveChangesAsync();

			// Success - user created with pending approval
			return Results.Created($"/api/auth/users/{user.UserName}", new
			{
				userId = user.Id,
				username = user.UserName,
				tenantId = usedInvitation.TenantId,
				pendingApproval = true,
				message = "Registration successful. Waiting for tenant admin approval."
			});
		}
		else
		{
			// No invitation code - user created but not assigned to any tenant
			// SuperAdmin will assign them later
			return Results.Created($"/api/auth/users/{user.UserName}", new
			{
				userId = user.Id,
				username = user.UserName,
				awaitingAssignment = true,
				message = "Registration successful. Waiting for administrator to assign you to a tenant."
			});
		}
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
			.Where(tu => tu.UserId == identityUser.Id && tu.JoinedAt != default)
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
				role = tenantUser.Role.ToClaimValue()
			});
		}

		return Results.Ok(tenants);
	}

	private static async Task<IResult> SelectTenant(
		[FromBody] SelectTenantRequest request,
		ClaimsPrincipal user,
		UserManager<ApplicationUser> userManager,
		SignInManager<ApplicationUser> signInManager,
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

		// Verify user is member of requested tenant
		var tenantUser = await db.TenantUsers
			.IgnoreQueryFilters()
			.FirstOrDefaultAsync(tu => tu.UserId == identityUser.Id && tu.TenantId == request.TenantId);

		if (tenantUser == null)
			return Results.StatusCode(403); // User not member of this tenant

		// Skip pending approval users
		if (tenantUser.JoinedAt == default)
			return Results.StatusCode(403); // User pending approval in this tenant

		// Get permissions for this tenant
		var permissions = await db.TenantPermissions
			.IgnoreQueryFilters()
			.Where(tp => tp.TenantUserId == tenantUser.Id)
			.Select(tp => tp.Permission)
			.ToListAsync();

		// Update cookie claims to add TenantId and TenantRole
		var claims = new List<Claim>
		{
			new Claim(ClaimTypes.NameIdentifier, identityUser.Id),
			new Claim(ClaimTypes.Name, identityUser.UserName ?? string.Empty),
			new Claim(AuthorizationConstants.ClaimTypes.TenantId, request.TenantId),
			new Claim(AuthorizationConstants.ClaimTypes.TenantRole, tenantUser.Role.ToClaimValue()),
			new Claim(ClaimTypes.Role, tenantUser.Role.ToClaimValue())  // Add as Role claim for [Authorize(Roles=...)]
		};

		// Add permission claims (must match TenantPermissionHandler)
		foreach (var permission in permissions)
		{
			claims.Add(new Claim(AuthorizationConstants.ClaimTypes.TenantPermission, permission.ToClaimValue()));
		}

		var claimsIdentity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme);
		var claimsPrincipal = new ClaimsPrincipal(claimsIdentity);

		// Sign in with updated claims
		await signInManager.SignInAsync(identityUser, new Microsoft.AspNetCore.Authentication.AuthenticationProperties
		{
			IsPersistent = true
		}, IdentityConstants.ApplicationScheme);

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
