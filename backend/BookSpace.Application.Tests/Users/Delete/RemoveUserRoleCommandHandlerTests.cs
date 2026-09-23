using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class RemoveUserRoleCommandHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Guid _callerId = Guid.NewGuid();

    // Safe default for every test that isn't specifically exercising the ResourceApprover-assignment
    // conflict below - an empty list means "Approver" removal never gets blocked by it.
    public RemoveUserRoleCommandHandlerTests() =>
        _resourceApproverRepository.Setup(r => r.GetResourceIdsByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

    private RemoveUserRoleCommandHandler CreateSut() =>
        new(_userRepository.Object, _resourceApproverRepository.Object, new FixedCurrentUserContext(_callerId));

    private void SetUpUser(Guid userId, UserStatus status, IReadOnlyList<string> roles)
    {
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(new User { Id = userId, Status = status });
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(roles);
    }

    [Fact]
    public async Task Handle_RemovingAPlainMemberRole_Succeeds()
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Active, ["Member", "Approver"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("Approver", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "Approver" });

        var sut = CreateSut();
        await sut.Handle(new RemoveUserRoleCommandRequest(userId, "Approver"), CancellationToken.None);

        _userRepository.Verify(r => r.RemoveRoleAsync(userId, roleId, It.IsAny<CancellationToken>()), Times.Once);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithARoleTheUserDoesNotHold_ThrowsNotFoundWithoutRemoving()
    {
        var userId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Active, ["Member"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("Approver", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = Guid.NewGuid(), Name = "Approver" });

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new RemoveUserRoleCommandRequest(userId, "Approver"), CancellationToken.None));

        Assert.Equal("User.RoleNotAssigned", exception.ErrorCode);
        _userRepository.Verify(r => r.RemoveRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CallerRemovingTheirOwnTenantAdminRole_ThrowsSelfLockoutConflict()
    {
        SetUpUser(_callerId, UserStatus.Active, ["TenantAdmin"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("TenantAdmin", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = Guid.NewGuid(), Name = "TenantAdmin" });

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new RemoveUserRoleCommandRequest(_callerId, "TenantAdmin"), CancellationToken.None));

        Assert.Equal("User.SelfLockout", exception.ErrorCode);
        _userRepository.Verify(r => r.RemoveRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Removing your own Member/Approver role is not a lockout concern - only TenantAdmin/SysAdmin.
    [Fact]
    public async Task Handle_CallerRemovingTheirOwnMemberRole_Succeeds()
    {
        var roleId = Guid.NewGuid();
        SetUpUser(_callerId, UserStatus.Active, ["Member", "TenantAdmin"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("Member", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "Member" });

        var sut = CreateSut();
        await sut.Handle(new RemoveUserRoleCommandRequest(_callerId, "Member"), CancellationToken.None);

        _userRepository.Verify(r => r.RemoveRoleAsync(_callerId, roleId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Removing TenantAdmin from someone who is ALSO SysAdmin leaves them still administrative - no
    // last-admin check should even trigger a repository call.
    [Fact]
    public async Task Handle_RemovingOneAdminRoleWhenTheTargetHoldsBoth_Succeeds()
    {
        var otherUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(otherUserId, UserStatus.Active, ["TenantAdmin", "SysAdmin"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("TenantAdmin", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "TenantAdmin" });

        var sut = CreateSut();
        await sut.Handle(new RemoveUserRoleCommandRequest(otherUserId, "TenantAdmin"), CancellationToken.None);

        _userRepository.Verify(r => r.RemoveRoleAsync(otherUserId, roleId, It.IsAny<CancellationToken>()), Times.Once);
        _userRepository.Verify(r => r.CountActiveAdministratorsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RemovingTheLastActiveAdministratorsOnlyAdminRole_ThrowsLastAdminConflict()
    {
        var otherUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(otherUserId, UserStatus.Active, ["TenantAdmin"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("TenantAdmin", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "TenantAdmin" });
        _userRepository.Setup(r => r.CountActiveAdministratorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new RemoveUserRoleCommandRequest(otherUserId, "TenantAdmin"), CancellationToken.None));

        Assert.Equal("User.LastAdminRemaining", exception.ErrorCode);
        _userRepository.Verify(r => r.RemoveRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RemovingAnAdminRoleWhenAnotherActiveAdministratorExists_Succeeds()
    {
        var otherUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(otherUserId, UserStatus.Active, ["TenantAdmin"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("TenantAdmin", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "TenantAdmin" });
        _userRepository.Setup(r => r.CountActiveAdministratorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);

        var sut = CreateSut();
        await sut.Handle(new RemoveUserRoleCommandRequest(otherUserId, "TenantAdmin"), CancellationToken.None);

        _userRepository.Verify(r => r.RemoveRoleAsync(otherUserId, roleId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // An Inactive/Invited holder of TenantAdmin never counted toward the active-administrator set in
    // the first place, so removing their role can never be the thing that empties it.
    [Fact]
    public async Task Handle_RemovingAnAdminRoleFromAnInactiveUser_NeverChecksTheLastAdminInvariant()
    {
        var otherUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(otherUserId, UserStatus.Inactive, ["TenantAdmin"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("TenantAdmin", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "TenantAdmin" });

        var sut = CreateSut();
        await sut.Handle(new RemoveUserRoleCommandRequest(otherUserId, "TenantAdmin"), CancellationToken.None);

        _userRepository.Verify(r => r.CountActiveAdministratorsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _userRepository.Verify(r => r.RemoveRoleAsync(otherUserId, roleId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RemovingApproverRoleWhileResourceApproverAssignmentsExist_ThrowsConflictWithoutRemoving()
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Active, ["Approver"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("Approver", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "Approver" });
        _resourceApproverRepository.Setup(r => r.GetResourceIdsByUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync([Guid.NewGuid()]);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new RemoveUserRoleCommandRequest(userId, "Approver"), CancellationToken.None));

        Assert.Equal("User.ApproverAssignmentsExist", exception.ErrorCode);
        _userRepository.Verify(r => r.RemoveRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RemovingApproverRoleWithNoResourceApproverAssignments_Succeeds()
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Active, ["Approver"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("Approver", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "Approver" });
        _resourceApproverRepository.Setup(r => r.GetResourceIdsByUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var sut = CreateSut();
        await sut.Handle(new RemoveUserRoleCommandRequest(userId, "Approver"), CancellationToken.None);

        _userRepository.Verify(r => r.RemoveRoleAsync(userId, roleId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RemovingANonApproverRole_NeverChecksResourceApproverAssignments()
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Active, ["Member"]);
        _userRepository.Setup(r => r.FindRoleByNameAsync("Member", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "Member" });

        var sut = CreateSut();
        await sut.Handle(new RemoveUserRoleCommandRequest(userId, "Member"), CancellationToken.None);

        _resourceApproverRepository.Verify(r => r.GetResourceIdsByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixedCurrentUserContext(Guid userId) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => Guid.NewGuid();
        public IReadOnlyCollection<string> Roles => [];
    }
}
