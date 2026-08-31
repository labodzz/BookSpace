using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Resources;

public sealed record ResourceResponse(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    ResourceStatus Status,
    string TimeZoneId);

internal static class ResourceMappingExtensions
{
    public static ResourceResponse ToResponse(this Resource resource) => new(
        resource.Id,
        resource.ResourceTypeId,
        resource.Name,
        resource.Description,
        resource.Capacity,
        resource.RequiresApproval,
        resource.Status,
        resource.TimeZoneId);
}
