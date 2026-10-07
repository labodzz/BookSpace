using BookSpace.Application.Auth;
using BookSpace.Application.Calendar;
using BookSpace.Application.Common;
using BookSpace.Application.Tenants;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Tenants.Delete;

public sealed class DeleteTenantCommandHandlerTests
{
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IInvitationRepository> _invitationRepository = new();
    private readonly Mock<ICalendarSubscriptionRepository> _calendarSubscriptionRepository = new();
    private readonly Mock<IRefreshTokenRepository> _refreshTokenRepository = new();

    private readonly Tenant _tenant =
        new() { Id = Guid.NewGuid(), Name = "Acme Coworking", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow };

    public DeleteTenantCommandHandlerTests()
    {
        _tenantRepository.Setup(r => r.FindByIdAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_tenant);
        _userRepository.Setup(r => r.WithdrawAllForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid>)[]);
    }

    private DeleteTenantCommandHandler CreateSut() => new(
        _tenantRepository.Object,
        _userRepository.Object,
        _invitationRepository.Object,
        _calendarSubscriptionRepository.Object,
        _refreshTokenRepository.Object);

    [Fact]
    public async Task Handle_WithAValidRequest_SetsTenantStatusToDeletedAndReturnsIt()
    {
        var sut = CreateSut();
        var result = await sut.Handle(new DeleteTenantCommandRequest(_tenant.Id, "Acme Coworking"), CancellationToken.None);

        Assert.Equal(TenantStatus.Deleted, _tenant.Status);
        Assert.Equal(_tenant.Id, result.Id);
        Assert.Equal("Acme Coworking", result.Name);
        Assert.Equal(TenantStatus.Deleted, result.Status);
    }

    // The five-effect cascade (see the handler's own doc comment): every repository is driven off the
    // SAME tenant id, the refresh-token revocation is scoped to EXACTLY the ids WithdrawAllForTenantAsync
    // itself returned (never a broader "every refresh token" call), and SaveChangesAsync is the one, final
    // commit - never called by any of the other four repositories, confirming the handler itself owns the
    // single atomic commit point.
    [Fact]
    public async Task Handle_WithAValidRequest_CascadesWithdrawalAndRevocationForExactlyThisTenantThenCommitsOnce()
    {
        var withdrawnUserIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _userRepository.Setup(r => r.WithdrawAllForTenantAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(withdrawnUserIds);

        var sut = CreateSut();
        await sut.Handle(new DeleteTenantCommandRequest(_tenant.Id, "Acme Coworking"), CancellationToken.None);

        _userRepository.Verify(r => r.WithdrawAllForTenantAsync(_tenant.Id, It.IsAny<CancellationToken>()), Times.Once);
        _invitationRepository.Verify(r => r.RevokeAllActiveForTenantAsync(_tenant.Id, It.IsAny<CancellationToken>()), Times.Once);
        _calendarSubscriptionRepository.Verify(r => r.RevokeAllActiveForTenantAsync(_tenant.Id, It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokenRepository.Verify(
            r => r.RevokeAllForUsersAsync(
                It.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(withdrawnUserIds)), It.IsAny<CancellationToken>()),
            Times.Once);
        _tenantRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _invitationRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _refreshTokenRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheTenantIsNotFound_ThrowsNotFoundExceptionAndMutatesNothing()
    {
        _tenantRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);

        var sut = CreateSut();
        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.Handle(new DeleteTenantCommandRequest(Guid.NewGuid(), "Doesn't matter"), CancellationToken.None));

        _userRepository.Verify(r => r.WithdrawAllForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenantRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Idempotent, with no partial re-application - see the handler's own doc comment. A second delete
    // attempt against an already-Deleted tenant must fail BEFORE touching any of the other four
    // repositories, not merely fail to persist afterward.
    [Fact]
    public async Task Handle_WhenTheTenantIsAlreadyDeleted_ThrowsConflictExceptionAndMutatesNothingFurther()
    {
        _tenant.Status = TenantStatus.Deleted;

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => sut.Handle(new DeleteTenantCommandRequest(_tenant.Id, "Acme Coworking"), CancellationToken.None));

        Assert.Equal("Tenant.StatusConflict", exception.ErrorCode);
        _userRepository.Verify(r => r.WithdrawAllForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _invitationRepository.Verify(r => r.RevokeAllActiveForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _calendarSubscriptionRepository.Verify(r => r.RevokeAllActiveForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _refreshTokenRepository.Verify(r => r.RevokeAllForUsersAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenantRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheTenantIsSuspended_StillDeletesSuccessfully()
    {
        _tenant.Status = TenantStatus.Suspended;

        var sut = CreateSut();
        var result = await sut.Handle(new DeleteTenantCommandRequest(_tenant.Id, "Acme Coworking"), CancellationToken.None);

        Assert.Equal(TenantStatus.Deleted, result.Status);
    }

    [Theory]
    [InlineData("Acme coworking")] // case differs - ordinal comparison must reject this
    [InlineData("Acme Cowork")] // not an exact match
    [InlineData("")]
    public async Task Handle_WhenTheConfirmationNameDoesNotMatchExactly_ThrowsConflictExceptionAndMutatesNothing(string confirmName)
    {
        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => sut.Handle(new DeleteTenantCommandRequest(_tenant.Id, confirmName), CancellationToken.None));

        Assert.Equal("Tenant.DeleteConfirmationMismatch", exception.ErrorCode);
        Assert.Equal(TenantStatus.Active, _tenant.Status);
        _userRepository.Verify(r => r.WithdrawAllForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenantRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
