using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Superadmin "who joined how" overview: every account with its sign-in methods (password / Steam / Discord),
/// registration source, last login and memberships (with how each was joined). Paged + filterable.
/// </summary>
public interface IAccountOverviewService
{
    Task<AccountOverviewPageDto> GetAccountsAsync(AccountOverviewQuery query, CancellationToken cancellationToken = default);
}
