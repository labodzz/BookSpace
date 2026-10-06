using BookSpace.Domain.Enums;

namespace BookSpace.Application.Tenants;

// Shared response shape for every tenant-lifecycle action (rename, suspend, reactivate, soft-delete) -
// each one only ever changes Name and/or Status, so there is no reason for four near-identical response
// records. Never carries user aggregates or anything else GetTenantQueryRequest's richer
// TenantDetailResponse returns - a lifecycle action's caller already knows which tenant it acted on and
// only needs to see the result of that specific action.
public sealed record TenantStatusResponse(Guid Id, string Name, TenantStatus Status);
