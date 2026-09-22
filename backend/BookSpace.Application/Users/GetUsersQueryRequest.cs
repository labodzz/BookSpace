using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Users;

// Ids exists for a different caller than Search/Role: Search/Role serve the "pick an eligible
// approver" typeahead (only ever matches users who currently qualify), while Ids resolves a SPECIFIC,
// already-known set of user ids back to display data - e.g. GetResourceApproversResponseItem only ever
// returns UserId, and the admin UI showing "who is this?" for an existing assignment must still resolve
// a user who no longer holds the Approver role, or who no longer exists at all, neither of which Role
// or Search could ever surface. Both stay tenant-scoped through the same IUserRepository.GetPagedAsync.
//
// Status has no default filter applied when omitted (unlike IResourceRepository.GetPagedAsync, which
// excludes Archived by default) - a user administration list is for admins managing the whole roster,
// including deactivated accounts they might reactivate, not end users browsing a catalog. Hiding
// Inactive users by default would make deactivated staff seem to vanish entirely from the admin's view.
public sealed record GetUsersQueryRequest(
    int Page = 1, int PageSize = 20, string? Search = null, string? Role = null, IReadOnlyList<Guid>? Ids = null, UserStatus? Status = null)
    : IRequest<PagedResult<UserSummaryResponse>>;

public sealed record UserSummaryResponse(
    Guid Id, string FirstName, string LastName, string Email, Guid TenantId, UserStatus Status, IReadOnlyList<string> Roles);

public sealed class GetUsersQueryRequestValidator : AbstractValidator<GetUsersQueryRequest>
{
    public GetUsersQueryRequestValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Search).MaximumLength(200);
        RuleFor(query => query.Role).MaximumLength(50);
        RuleFor(query => query.Ids).Must(ids => ids is null || ids.Count <= 100)
            .WithMessage("At most 100 ids may be resolved at a time.");
    }
}

public sealed class GetUsersQueryRequestHandler(IUserRepository userRepository)
    : IRequestHandler<GetUsersQueryRequest, PagedResult<UserSummaryResponse>>
{
    public async Task<PagedResult<UserSummaryResponse>> Handle(GetUsersQueryRequest request, CancellationToken cancellationToken)
    {
        var paged = await userRepository.GetPagedAsync(
            request.Page, request.PageSize, request.Search, request.Role, request.Ids, request.Status, cancellationToken);

        var rolesByUserId = await userRepository.GetRolesByUserIdsAsync(
            paged.Items.Select(user => user.Id).ToList(), cancellationToken);

        return new PagedResult<UserSummaryResponse>(
            paged.Items
                .Select(user => new UserSummaryResponse(
                    user.Id,
                    user.FirstName,
                    user.LastName,
                    user.Email,
                    user.TenantId,
                    user.Status,
                    rolesByUserId.TryGetValue(user.Id, out var roles) ? roles : []))
                .ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
