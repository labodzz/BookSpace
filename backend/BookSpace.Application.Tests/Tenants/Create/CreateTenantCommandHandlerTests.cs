using System.Text.Json;
using System.Web;
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

namespace BookSpace.Application.Tests.Tenants.Create;

public sealed class CreateTenantCommandHandlerTests
{
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IInvitationRepository> _invitationRepository = new();
    private readonly Mock<INotificationOutboxWriter> _notificationOutboxWriter = new();
    private readonly Guid _sysAdminTenantId = Guid.NewGuid();
    private readonly Guid _sysAdminUserId = Guid.NewGuid();
    private static readonly Guid TenantAdminRoleId = Guid.NewGuid();

    private const string PublicBaseUrl = "https://bookspace.test";

    private static readonly CreateTenantCommandRequest ValidRequest =
        new("Acme Coworking", "Europe/Zagreb", "Ana", "Anic", "ana@acme.test");

    public CreateTenantCommandHandlerTests()
    {
        _tenantRepository.Setup(r => r.ExistsByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _userRepository.Setup(r => r.FindByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        _userRepository.Setup(r => r.FindRoleByNameAsync("TenantAdmin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = TenantAdminRoleId, Name = "TenantAdmin" });
        _notificationOutboxWriter.Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid()));
    }

    private CreateTenantCommandHandler CreateSut() => new(
        _tenantRepository.Object,
        _userRepository.Object,
        _invitationRepository.Object,
        _notificationOutboxWriter.Object,
        new FixedCurrentUserContext(_sysAdminTenantId, _sysAdminUserId),
        Options.Create(new AppOptions { PublicBaseUrl = PublicBaseUrl }));

    [Fact]
    public async Task Handle_WithAValidRequest_CreatesATenantAndReturnsItsIdAndName()
    {
        Tenant? addedTenant = null;
        _tenantRepository.Setup(r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()))
            .Callback<Tenant, CancellationToken>((tenant, _) => addedTenant = tenant).Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.Handle(ValidRequest, CancellationToken.None);

        Assert.NotNull(addedTenant);
        Assert.Equal("Acme Coworking", addedTenant!.Name);
        Assert.Equal("Europe/Zagreb", addedTenant.DefaultTimeZoneId);
        Assert.Equal(addedTenant.Id, result.TenantId);
        Assert.Equal("Acme Coworking", result.TenantName);
    }

    [Fact]
    public async Task Handle_WithAValidRequest_CreatesTheFirstAdminAsInvitedWithNoPasswordAndNoRawTokenEverReturned()
    {
        User? addedUser = null;
        _userRepository.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((user, _) => addedUser = user).Returns(Task.CompletedTask);

        var sut = CreateSut();
        var result = await sut.Handle(ValidRequest, CancellationToken.None);

        Assert.NotNull(addedUser);
        Assert.Equal(UserStatus.Invited, addedUser!.Status);
        Assert.Equal(string.Empty, addedUser.PasswordHash);
        Assert.Equal("Ana", addedUser.FirstName);
        Assert.Equal("Anic", addedUser.LastName);
        Assert.Equal("ana@acme.test", addedUser.Email);
        Assert.Equal(addedUser.Id, result.AdminUserId);
        Assert.Equal("ana@acme.test", result.AdminEmail);
    }

    [Fact]
    public async Task Handle_WithAValidRequest_TiesTheNewAdminToTheNewTenantAndAssignsExactlyTheTenantAdminRole()
    {
        Tenant? addedTenant = null;
        _tenantRepository.Setup(r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()))
            .Callback<Tenant, CancellationToken>((tenant, _) => addedTenant = tenant).Returns(Task.CompletedTask);
        User? addedUser = null;
        _userRepository.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((user, _) => addedUser = user).Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.Handle(ValidRequest, CancellationToken.None);

        Assert.Equal(addedTenant!.Id, addedUser!.TenantId);
        _userRepository.Verify(r => r.AddRoleAsync(addedUser.Id, TenantAdminRoleId, It.IsAny<CancellationToken>()), Times.Once);
        _userRepository.Verify(r => r.AddRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithAValidRequest_CreatesAnInvitationAttributedToTheCallingSysAdmin()
    {
        User? addedUser = null;
        _userRepository.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((user, _) => addedUser = user).Returns(Task.CompletedTask);
        Invitation? addedInvitation = null;
        _invitationRepository.Setup(r => r.AddAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()))
            .Callback<Invitation, CancellationToken>((invitation, _) => addedInvitation = invitation).Returns(Task.CompletedTask);

        var sut = CreateSut();
        var before = DateTimeOffset.UtcNow;
        await sut.Handle(ValidRequest, CancellationToken.None);

        Assert.NotNull(addedInvitation);
        Assert.Equal(addedUser!.Id, addedInvitation!.UserId);
        Assert.Equal(_sysAdminUserId, addedInvitation.CreatedByUserId);
        Assert.InRange(addedInvitation.ExpiresAtUtc, before.AddHours(72), DateTimeOffset.UtcNow.AddHours(72));
        Assert.False(string.IsNullOrWhiteSpace(addedInvitation.TokenHash));
    }

    [Fact]
    public async Task Handle_WithAValidRequest_EnqueuesAnInvitationEmailWithALinkWhoseTokenHashesToTheStoredInvitation()
    {
        Invitation? addedInvitation = null;
        _invitationRepository.Setup(r => r.AddAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()))
            .Callback<Invitation, CancellationToken>((invitation, _) => addedInvitation = invitation).Returns(Task.CompletedTask);
        NotificationOutboxRequest? enqueued = null;
        _notificationOutboxWriter.Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .Callback<NotificationOutboxRequest, CancellationToken>((request, _) => enqueued = request)
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid()));

        var sut = CreateSut();
        await sut.Handle(ValidRequest, CancellationToken.None);

        Assert.NotNull(enqueued);
        Assert.Equal(TenantNotificationTypes.AdminInvitation, enqueued!.NotificationType);
        var payload = JsonSerializer.Deserialize<TenantAdminInvitationNotificationPayload>(enqueued.PayloadJson)!;
        Assert.StartsWith($"{PublicBaseUrl}/accept-invitation?token=", payload.InvitationUrl);

        // The link is built from the configured public base URL, never from any caller-suppliable value -
        // and the token embedded in it is exactly the one whose hash was stored, never the hash itself.
        var rawToken = HttpUtility.ParseQueryString(new Uri(payload.InvitationUrl).Query)["token"]!;
        Assert.Equal(addedInvitation!.TokenHash, SecureTokenGenerator.HashToken(rawToken));
    }

    [Fact]
    public async Task Handle_WithAnAlreadyUsedTenantName_ThrowsConflictAndCreatesNothing()
    {
        _tenantRepository.Setup(r => r.ExistsByNameAsync("Acme Coworking", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(ValidRequest, CancellationToken.None));

        Assert.Equal(ErrorCodes.TenantNameConflict, exception.ErrorCode);
        _tenantRepository.Verify(r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Never);
        _userRepository.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAnEmailAlreadyBelongingToAnExistingUserInAnyTenant_ThrowsConflictAndCreatesNoTenant()
    {
        var existingUser = new User { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Email = "ana@acme.test", Status = UserStatus.Active };
        _userRepository.Setup(r => r.FindByEmailAsync("ana@acme.test", It.IsAny<CancellationToken>())).ReturnsAsync(existingUser);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(ValidRequest, CancellationToken.None));

        Assert.Equal(ErrorCodes.UserEmailConflict, exception.ErrorCode);
        // Never moves the existing account, changes its tenant, or assigns it a new role - a brand-new
        // Tenant row is simply never created when the admin email already belongs to someone.
        _tenantRepository.Verify(r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Never);
        _userRepository.Verify(r => r.AddRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheTenantAdminRoleIsMissingFromTheRolesTable_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindRoleByNameAsync("TenantAdmin", It.IsAny<CancellationToken>())).ReturnsAsync((Role?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(ValidRequest, CancellationToken.None));

        Assert.Equal(ErrorCodes.RoleNotFound, exception.ErrorCode);
    }

    // The outbox's own enqueue call is the handler's single commit point (see the class doc comment) - a
    // ConflictException thrown from it (a genuine Tenant.Name/User.Email race lost at the database, not
    // caught by the pre-checks above) must propagate to the caller completely unhandled, not be swallowed
    // or rewrapped, since NotificationOutboxWriter is already the layer responsible for that translation.
    [Fact]
    public async Task Handle_WhenEnqueueingRaceLoses_PropagatesTheConflictExceptionUnmodified()
    {
        var raceException = new ConflictException("The request conflicts with existing data.");
        _notificationOutboxWriter.Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(raceException);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(ValidRequest, CancellationToken.None));

        Assert.Same(raceException, exception);
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid userId) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => ["SysAdmin"];
    }
}
