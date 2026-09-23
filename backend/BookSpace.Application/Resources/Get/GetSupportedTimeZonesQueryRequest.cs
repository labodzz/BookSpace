using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

// Read-only, tenant-agnostic reference data for the Resource Create/Edit timezone selector - the same
// canonical IANA identifiers every request on this running backend instance will accept via
// TimeZoneValidation.BeAValidTimeZoneId (Create/UpdateResourceCommandRequest's own validator), so the
// frontend never offers a value the backend would then reject.
public sealed record GetSupportedTimeZonesQueryRequest : IRequest<IReadOnlyList<string>>;

public sealed class GetSupportedTimeZonesQueryHandler : IRequestHandler<GetSupportedTimeZonesQueryRequest, IReadOnlyList<string>>
{
    // CanonicalIanaTimeZoneIds.All never changes at runtime and TimeZoneValidation.BeAValidTimeZoneId's
    // underlying TimeZoneInfo.FindSystemTimeZoneById lookups are real (if cheap) OS calls - computed once
    // per process and reused, not re-filtered on every request.
    private static readonly Lazy<IReadOnlyList<string>> SupportedIds = new(() =>
        CanonicalIanaTimeZoneIds.All
            .Where(TimeZoneValidation.BeAValidTimeZoneId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList());

    public Task<IReadOnlyList<string>> Handle(GetSupportedTimeZonesQueryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(SupportedIds.Value);
}
