using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class AssignResourceApproverCommandHandlerTests
{
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private AssignResourceApproverCommandHandler CreateSut() =>
        new(_resourceApproverRepository.Object, _resourceRepository.Object, _userRepository.Object, _currentUserContext.Object);

    private static Resource CreateResource() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Desk 1",
        Capacity = 1,
        Status = ResourceStatus.Active,
        TimeZoneId = "UTC",
    };

    private static User CreateUser(Guid tenantId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        FirstName = "Ana",
        LastName = "Anić",
        Email = "ana@acme.test",
        PasswordHash = "hash",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Handle_WithValidCommand_CreatesAndReturnsApprover()
    {
        var resource = CreateResource();
        var tenantId = Guid.NewGuid();
        var user = CreateUser(tenantId);
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _userRepository.Setup(r => r.FindByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resource.Id, user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();
        var request = new AssignResourceApproverCommandRequest(resource.Id, user.Id);

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(resource.Id, result.ResourceId);
        Assert.Equal(user.Id, result.UserId);
        _resourceApproverRepository.Verify(r => r.AddAsync(
            It.Is<ResourceApprover>(approver => approver.TenantId == tenantId && approver.ResourceId == resource.Id && approver.UserId == user.Id),
            It.IsAny<CancellationToken>()), Times.Once);
        _resourceApproverRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundExceptionWithoutSaving()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();
        var request = new AssignResourceApproverCommandRequest(Guid.NewGuid(), Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));

        Assert.Equal("Resource.NotFound", exception.ErrorCode);
        _resourceApproverRepository.Verify(r => r.AddAsync(It.IsAny<ResourceApprover>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithUnknownUser_ThrowsNotFoundExceptionWithoutSaving()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _userRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        var sut = CreateSut();
        var request = new AssignResourceApproverCommandRequest(resource.Id, Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));

        Assert.Equal("User.NotFound", exception.ErrorCode);
        _resourceApproverRepository.Verify(r => r.AddAsync(It.IsAny<ResourceApprover>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAlreadyAssignedUser_ThrowsConflictExceptionWithoutSaving()
    {
        var resource = CreateResource();
        var user = CreateUser(resource.TenantId);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _userRepository.Setup(r => r.FindByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resource.Id, user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceApprover { Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, UserId = user.Id });
        var sut = CreateSut();
        var request = new AssignResourceApproverCommandRequest(resource.Id, user.Id);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));

        Assert.Equal("ResourceApprover.Conflict", exception.ErrorCode);
        _resourceApproverRepository.Verify(r => r.AddAsync(It.IsAny<ResourceApprover>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
