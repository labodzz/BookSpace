using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class RemoveResourceApproverCommandHandlerTests
{
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();

    private RemoveResourceApproverCommandHandler CreateSut() => new(_resourceApproverRepository.Object);

    [Fact]
    public async Task Handle_WithMatchingApprover_RemovesItAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var approver = new ResourceApprover { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = userId };
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resourceId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(approver);
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
}
