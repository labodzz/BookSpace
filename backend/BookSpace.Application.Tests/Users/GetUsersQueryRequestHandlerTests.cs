using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class GetUsersQueryRequestHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();

    private GetUsersQueryRequestHandler CreateSut() => new(_userRepository.Object);

    private static User CreateUser(Guid? id = null, Guid? tenantId = null, UserStatus status = UserStatus.Active) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TenantId = tenantId ?? Guid.NewGuid(),
        FirstName = "Test",
        LastName = "User",
        Email = "test.user@bookspace.test",
        PasswordHash = "stored-hash",
        Status = status,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    // GetPagedAsync itself is a Mock<IUserRepository> setup, so any (page, pageSize, search, role, ids,
    // status) combination not explicitly configured just returns this default - individual tests only
    // ever override what they're actually asserting on.
    private void SetUpPagedResult(PagedResult<User> result) =>
        _userRepository
            .Setup(r => r.GetPagedAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<Guid>?>(), It.IsAny<UserStatus?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void SetUpNoRoles() =>
        _userRepository
            .Setup(r => r.GetRolesByUserIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<string>>());

    [Fact]
    public async Task Handle_MapsRepositoryUsersToSummaryResponsesPreservingPagingAndStatus()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser(tenantId: tenantId, status: UserStatus.Inactive);
        SetUpPagedResult(new PagedResult<User>([user], 2, 10, 1));
        SetUpNoRoles();
        var sut = CreateSut();

        var result = await sut.Handle(new GetUsersQueryRequest(Page: 2, PageSize: 10), CancellationToken.None);

        var summary = Assert.Single(result.Items);
        Assert.Equal(user.Id, summary.Id);
        Assert.Equal(user.FirstName, summary.FirstName);
        Assert.Equal(user.LastName, summary.LastName);
        Assert.Equal(user.Email, summary.Email);
        Assert.Equal(tenantId, summary.TenantId);
        Assert.Equal(UserStatus.Inactive, summary.Status);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task Handle_WithNoUsers_ReturnsEmptyList()
    {
        SetUpPagedResult(new PagedResult<User>([], 1, 20, 0));
        SetUpNoRoles();
        var sut = CreateSut();

        var result = await sut.Handle(new GetUsersQueryRequest(), CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Handle_PassesPageAndPageSizeThroughToTheRepository()
    {
        SetUpPagedResult(new PagedResult<User>([], 3, 5, 0));
        SetUpNoRoles();
        var sut = CreateSut();

        await sut.Handle(new GetUsersQueryRequest(Page: 3, PageSize: 5), CancellationToken.None);

        _userRepository.Verify(r => r.GetPagedAsync(
            3, 5, null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PassesSearchRoleIdsAndStatusThroughToTheRepositoryUnchanged()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        SetUpPagedResult(new PagedResult<User>([], 1, 20, 0));
        SetUpNoRoles();
        var sut = CreateSut();

        await sut.Handle(new GetUsersQueryRequest(Search: "jane", Role: "Approver", Ids: ids, Status: UserStatus.Invited), CancellationToken.None);

        _userRepository.Verify(r => r.GetPagedAsync(
            1, 20, "jane", "Approver", ids, UserStatus.Invited, It.IsAny<CancellationToken>()), Times.Once);
    }

    // GetRolesByUserIdsAsync resolves the whole page's roles in ONE call, not one GetRolesAsync per
    // user - a user with no entry in the returned dictionary (never assigned any role) must default to
    // an empty list rather than throwing.
    [Fact]
    public async Task Handle_ResolvesRolesForTheWholePageInOneBatchedCallAndDefaultsMissingEntriesToEmpty()
    {
        var userWithRoles = CreateUser();
        var userWithoutRoles = CreateUser();
        SetUpPagedResult(new PagedResult<User>([userWithRoles, userWithoutRoles], 1, 20, 2));
        _userRepository
            .Setup(r => r.GetRolesByUserIdsAsync(
                It.Is<IReadOnlyList<Guid>>(list => list.Contains(userWithRoles.Id) && list.Contains(userWithoutRoles.Id)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<string>> { [userWithRoles.Id] = ["Approver", "TenantAdmin"] });
        var sut = CreateSut();

        var result = await sut.Handle(new GetUsersQueryRequest(), CancellationToken.None);

        Assert.Equal(["Approver", "TenantAdmin"], result.Items.Single(i => i.Id == userWithRoles.Id).Roles);
        Assert.Empty(result.Items.Single(i => i.Id == userWithoutRoles.Id).Roles);
        _userRepository.Verify(
            r => r.GetRolesByUserIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
