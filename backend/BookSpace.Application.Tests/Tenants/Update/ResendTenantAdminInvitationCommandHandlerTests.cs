using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Application.Tenants;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Tenants.Update;

public sealed class ResendTenantAdminInvitationCommandHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IInvitationRepository> _invitationRepository = new();
    private readonly Mock<INotificationOutboxWriter> _notificationOutboxWriter = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Guid _sysAdminTenantId = Guid.NewGuid();
    private readonly Guid _sysAdminUserId = Guid.NewGuid();
    private readonly Tenant _tenant = new() { Id = Guid.NewGuid(), Name = "Acme Coworking", DefaultTimeZoneId = "UTC", CreatedAtUtc = DateTimeOffset.UtcNow };
    private readonly User _invitedAdmin;

    public ResendTenantAdminInvitationCommandHandlerTests()
    {
        _invitedAdmin = new User
        {
            Id = Guid.NewGuid(), TenantId = _tenant.Id, FirstName = "Ana", LastName = "Anic",
            Email = "ana@acme.test", PasswordHash = string.Empty, Status = UserStatus.Invited, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _tenantRepository.Setup(r => r.FindByIdAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_tenant);
        _userRepository.Setup(r => r.FindByIdForTenantAdministrationAsync(_tenant.Id, _invitedAdmin.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_invitedAdmin);
        _notificationOutboxWriter.Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid()));
    }

    private ResendTenantAdminInvitationCommandHandler CreateSut() => new(
        _userRepository.Object,
        _invitationRepository.Object,
        _notificationOutboxWriter.Object,
        _tenantRepository.Object,
        new FixedCurrentUserContext(_sysAdminTenantId, _sysAdminUserId),
        Options.Create(new AppOptions { PublicBaseUrl = "https://bookspace.test" }));

    [Fact]
    public async Task Handle_ForAStillPendingAdmin_RevokesThePreviousActiveInvitationAndIssuesAFreshOne()
    {
        var previousInvitation = new Invitation
        {
            Id = Guid.NewGuid(), TenantId = _tenant.Id, UserId = _invitedAdmin.Id, TokenHash = "old-hash",
            CreatedByUserId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(71),
        };
        _invitationRepository.Setup(r => r.FindActiveByUserIdForTenantAdministrationAsync(_tenant.Id, _invitedAdmin.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousInvitation);
        Invitation? addedInvitation = null;
        _invitationRepository.Setup(r => r.AddAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()))
            .Callback<Invitation, CancellationToken>((invitation, _) => addedInvitation = invitation).Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.Handle(new ResendTenantAdminInvitationCommandRequest(_tenant.Id, _invitedAdmin.Id), CancellationToken.None);

        Assert.NotNull(previousInvitation.RevokedAtUtc);
        Assert.NotNull(addedInvitation);
        Assert.NotEqual(previousInvitation.Id, addedInvitation!.Id);
        Assert.Equal(_sysAdminUserId, addedInvitation.CreatedByUserId);
        Assert.Equal(_invitedAdmin.Id, result.AdminUserId);
        Assert.Equal(_tenant.Id, result.TenantId);
    }

    [Fact]
    public async Task Handle_WithNoPreviousActiveInvitation_StillIssuesANewOne()
    {
        _invitationRepository.Setup(r => r.FindActiveByUserIdForTenantAdministrationAsync(_tenant.Id, _invitedAdmin.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Invitation?)null);

        var sut = CreateSut();
        var result = await sut.Handle(new ResendTenantAdminInvitationCommandRequest(_tenant.Id, _invitedAdmin.Id), CancellationToken.None);

        Assert.Equal(_invitedAdmin.Id, result.AdminUserId);
        _invitationRepository.Verify(r => r.AddAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NeverCreatesANewUserOrNewTenant_OnlyANewInvitation()
    {
        _invitationRepository.Setup(r => r.FindActiveByUserIdForTenantAdministrationAsync(_tenant.Id, _invitedAdmin.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Invitation?)null);

        var sut = CreateSut();
        await sut.Handle(new ResendTenantAdminInvitationCommandRequest(_tenant.Id, _invitedAdmin.Id), CancellationToken.None);

        _userRepository.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenantRepository.Verify(r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Never);
        _userRepository.Verify(r => r.AddRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheTenantDoesNotExist_ThrowsNotFound()
    {
        _tenantRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new ResendTenantAdminInvitationCommandRequest(Guid.NewGuid(), _invitedAdmin.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.TenantNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenTheUserDoesNotExistInThatTenant_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindByIdForTenantAdministrationAsync(_tenant.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new ResendTenantAdminInvitationCommandRequest(_tenant.Id, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorCodes.UserNotFound, exception.ErrorCode);
    }

    [Theory]
    [InlineData(UserStatus.Active)]
    [InlineData(UserStatus.Inactive)]
    public async Task Handle_WhenTheAdminIsNoLongerInvited_ThrowsConflictAndNeverUsedAsAPasswordReset(UserStatus status)
    {
        _invitedAdmin.Status = status;

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ResendTenantAdminInvitationCommandRequest(_tenant.Id, _invitedAdmin.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.TenantAdminInvitationNotPending, exception.ErrorCode);
        _invitationRepository.Verify(r => r.AddAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A genuinely concurrent resend races on Invitation's own filtered unique index (at most one active
    // invitation per user) - the second request's EnqueueAsync call is where that race actually surfaces
    // (see NotificationOutboxWriter), and the resulting ConflictException must propagate unmodified.
    [Fact]
    public async Task Handle_WhenAConcurrentResendAlreadyWon_PropagatesTheConflictExceptionUnmodified()
    {
        var raceException = new ConflictException("The request conflicts with existing data.");
        _notificationOutboxWriter.Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(raceException);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ResendTenantAdminInvitationCommandRequest(_tenant.Id, _invitedAdmin.Id), CancellationToken.None));

        Assert.Same(raceException, exception);
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid userId) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => ["SysAdmin"];
    }
}
