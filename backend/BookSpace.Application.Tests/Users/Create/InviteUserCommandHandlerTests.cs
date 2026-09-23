using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class InviteUserCommandHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IInvitationRepository> _invitationRepository = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _callerId = Guid.NewGuid();

    private InviteUserCommandHandler CreateSut() =>
        new(_userRepository.Object, _invitationRepository.Object, new FixedCurrentUserContext(_tenantId, _callerId));

    private void SetUpRole(string name, Guid id) =>
        _userRepository.Setup(r => r.FindRoleByNameAsync(name, It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = id, Name = name });

    [Fact]
    public async Task Handle_WithAnUnusedEmail_CreatesAnInvitedUserAssignsRolesAndReturnsARawToken()
    {
        _userRepository.Setup(r => r.FindByEmailAsync("new.hire@bookspace.test", It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        var memberRoleId = Guid.NewGuid();
        SetUpRole("Member", memberRoleId);
        _invitationRepository.Setup(r => r.FindActiveByUserIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Invitation?)null);
        User? addedUser = null;
        _userRepository.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((user, _) => addedUser = user)
            .Returns(Task.CompletedTask);
        Invitation? addedInvitation = null;
        _invitationRepository.Setup(r => r.AddAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()))
            .Callback<Invitation, CancellationToken>((invitation, _) => addedInvitation = invitation)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.Handle(
            new InviteUserCommandRequest("new.hire@bookspace.test", "New", "Hire", ["Member"]), CancellationToken.None);

        Assert.NotNull(addedUser);
        Assert.Equal(UserStatus.Invited, addedUser!.Status);
        Assert.Equal(string.Empty, addedUser.PasswordHash);
        Assert.Equal(_tenantId, addedUser.TenantId);
        _userRepository.Verify(r => r.AddRoleAsync(addedUser.Id, memberRoleId, It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(addedInvitation);
        Assert.Equal(addedUser.Id, addedInvitation!.UserId);
        Assert.Equal(_tenantId, addedInvitation.TenantId);
        Assert.Equal(_callerId, addedInvitation.CreatedByUserId);
        Assert.True(addedInvitation.ExpiresAtUtc > DateTimeOffset.UtcNow);

        // The raw token is returned to the caller but never itself stored - only its hash is, and the
        // hash can never equal the raw value it was derived from.
        Assert.False(string.IsNullOrWhiteSpace(result.InvitationToken));
        Assert.NotEqual(result.InvitationToken, addedInvitation.TokenHash);
        Assert.Equal(addedUser.Id, result.UserId);
    }

    [Fact]
    public async Task Handle_WithAnEmailAlreadyBelongingToAnActiveUser_ThrowsConflictWithoutRevealingWhichTenant()
    {
        var existingUser = new User { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Email = "taken@bookspace.test", Status = UserStatus.Active };
        _userRepository.Setup(r => r.FindByEmailAsync("taken@bookspace.test", It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new InviteUserCommandRequest("taken@bookspace.test", "New", "Hire", []), CancellationToken.None));

        Assert.Equal("User.EmailConflict", exception.ErrorCode);
        _userRepository.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAnEmailAlreadyBelongingToAnInactiveUser_ThrowsConflict()
    {
        var existingUser = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "gone@bookspace.test", Status = UserStatus.Inactive };
        _userRepository.Setup(r => r.FindByEmailAsync("gone@bookspace.test", It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new InviteUserCommandRequest("gone@bookspace.test", "New", "Hire", []), CancellationToken.None));

        Assert.Equal("User.EmailConflict", exception.ErrorCode);
    }

    // Reissue: the same still-pending user, a fresh invitation, and the previous active invitation
    // explicitly revoked - never two simultaneously-active invitations for the same user.
    [Fact]
    public async Task Handle_ReinvitingAStillPendingUser_RevokesThePreviousInvitationAndIssuesAFreshOne()
    {
        var pendingUser = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "pending@bookspace.test", Status = UserStatus.Invited };
        _userRepository.Setup(r => r.FindByEmailAsync("pending@bookspace.test", It.IsAny<CancellationToken>())).ReturnsAsync(pendingUser);
        var previousInvitation = new Invitation { Id = Guid.NewGuid(), TenantId = _tenantId, UserId = pendingUser.Id, TokenHash = "old-hash" };
        _invitationRepository.Setup(r => r.FindActiveByUserIdAsync(pendingUser.Id, It.IsAny<CancellationToken>())).ReturnsAsync(previousInvitation);

        var sut = CreateSut();
        var result = await sut.Handle(new InviteUserCommandRequest("pending@bookspace.test", "Pending", "User", []), CancellationToken.None);

        Assert.NotNull(previousInvitation.RevokedAtUtc);
        Assert.Equal(pendingUser.Id, result.UserId);
        // No new User row and no roles re-applied on reissue - the same pending user, unchanged apart
        // from its invitation.
        _userRepository.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _userRepository.Verify(r => r.AddRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _invitationRepository.Verify(r => r.AddAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A role name with no matching Roles row (should never happen in practice - the delegable set is
    // fixed and always seeded - but defensively checked the same way AssignResourceApproverCommandHandler
    // checks its own referenced rows rather than assuming they exist).
    [Fact]
    public async Task Handle_WithARoleNameThatDoesNotExistInTheRolesTable_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        _userRepository.Setup(r => r.FindRoleByNameAsync("Approver", It.IsAny<CancellationToken>())).ReturnsAsync((Role?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new InviteUserCommandRequest("new.hire@bookspace.test", "New", "Hire", ["Approver"]), CancellationToken.None));

        Assert.Equal("Role.NotFound", exception.ErrorCode);
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid userId) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
