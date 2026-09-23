using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Tests.Bookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class RemoveResourceApproverCommandHandlerTests
{
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    // PassThroughResourceBookingLock, not a mock - the lock's own keyed-acquisition behavior is proven
    // separately (RemoveResourceApproverConcurrencyTests, against real SQL Server); these tests only
    // care about what happens INSIDE the locked operation.
    private RemoveResourceApproverCommandHandler CreateSut() =>
        new(_resourceApproverRepository.Object, _resourceRepository.Object, new PassThroughResourceBookingLock());

    private static Resource CreateResource(Guid id, bool requiresApproval) => new()
    {
        Id = id,
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Desk 1",
        Capacity = 1,
        RequiresApproval = requiresApproval,
        Status = ResourceStatus.Active,
        TimeZoneId = "UTC",
    };

    [Fact]
    public async Task Handle_WithMatchingApproverOnAResourceThatDoesNotRequireApproval_RemovesItAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var approver = new ResourceApprover { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = userId };
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resourceId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(approver);
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, false));
        var sut = CreateSut();

        await sut.Handle(new RemoveResourceApproverCommandRequest(resourceId, userId), CancellationToken.None);

        _resourceApproverRepository.Verify(r => r.RemoveAsync(approver, It.IsAny<CancellationToken>()), Times.Once);
        _resourceApproverRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownApprover_ThrowsNotFoundException()
    {
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new RemoveResourceApproverCommandRequest(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("ResourceApprover.NotFound", exception.ErrorCode);
        _resourceApproverRepository.Verify(r => r.RemoveAsync(It.IsAny<ResourceApprover>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenRemovingTheLastApproverOfAResourceThatRequiresApproval_ThrowsConflictExceptionWithoutRemoving()
    {
        var resourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var approver = new ResourceApprover { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = userId };
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resourceId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(approver);
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, true));
        _resourceApproverRepository
            .Setup(r => r.GetByResourceIdAsync(resourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { approver });
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new RemoveResourceApproverCommandRequest(resourceId, userId), CancellationToken.None));

        Assert.Equal("ResourceApprover.LastRemaining", exception.ErrorCode);
        _resourceApproverRepository.Verify(r => r.RemoveAsync(It.IsAny<ResourceApprover>(), It.IsAny<CancellationToken>()), Times.Never);
        _resourceApproverRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenRemovingOneOfSeveralApproversOfAResourceThatRequiresApproval_RemovesAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var approver = new ResourceApprover { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = userId };
        var otherApprover = new ResourceApprover { Id = Guid.NewGuid(), TenantId = approver.TenantId, ResourceId = resourceId, UserId = Guid.NewGuid() };
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resourceId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(approver);
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, true));
        _resourceApproverRepository
            .Setup(r => r.GetByResourceIdAsync(resourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { approver, otherApprover });
        var sut = CreateSut();

        await sut.Handle(new RemoveResourceApproverCommandRequest(resourceId, userId), CancellationToken.None);

        _resourceApproverRepository.Verify(r => r.RemoveAsync(approver, It.IsAny<CancellationToken>()), Times.Once);
        _resourceApproverRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // Every other test above uses PassThroughResourceBookingLock, which ignores its resourceId argument
    // entirely - a bug that passed the wrong id would go undetected by any of them. This is the one test
    // that actually asserts the lock is acquired keyed by the request's own ResourceId, the same
    // convention UpdateResourceCommandHandlerTests already proves for its own handler.
    [Fact]
    public async Task Handle_AcquiresTheResourceBookingLockKeyedByTheRequestsOwnResourceId()
    {
        var resourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var approver = new ResourceApprover { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = userId };
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resourceId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(approver);
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, false));
        var lockMock = new Mock<IResourceBookingLock>();
        lockMock
            .Setup(l => l.RunExclusiveAsync(It.IsAny<Guid>(), It.IsAny<Func<CancellationToken, Task<Unit>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Func<CancellationToken, Task<Unit>>, CancellationToken>((_, operation, ct) => operation(ct));
        var sut = new RemoveResourceApproverCommandHandler(_resourceApproverRepository.Object, _resourceRepository.Object, lockMock.Object);

        await sut.Handle(new RemoveResourceApproverCommandRequest(resourceId, userId), CancellationToken.None);

        lockMock.Verify(
            l => l.RunExclusiveAsync(resourceId, It.IsAny<Func<CancellationToken, Task<Unit>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
