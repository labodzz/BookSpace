using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Users;

public sealed record GetUsersQueryRequest(int Page = 1, int PageSize = 20) : IRequest<PagedResult<UserSummaryResponse>>;

public sealed record UserSummaryResponse(Guid Id, string FirstName, string LastName, string Email, Guid TenantId);

public sealed class GetUsersQueryRequestValidator : AbstractValidator<GetUsersQueryRequest>
{
    public GetUsersQueryRequestValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class GetUsersQueryRequestHandler(IUserRepository userRepository)
    : IRequestHandler<GetUsersQueryRequest, PagedResult<UserSummaryResponse>>
{
    public async Task<PagedResult<UserSummaryResponse>> Handle(GetUsersQueryRequest request, CancellationToken cancellationToken)
    {
        var paged = await userRepository.GetPagedAsync(request.Page, request.PageSize, cancellationToken);

        return new PagedResult<UserSummaryResponse>(
            paged.Items.Select(user => new UserSummaryResponse(user.Id, user.FirstName, user.LastName, user.Email, user.TenantId)).ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
