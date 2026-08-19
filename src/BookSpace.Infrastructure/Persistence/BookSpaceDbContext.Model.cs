using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

public sealed partial class BookSpaceDbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ResourceType> ResourceTypes => Set<ResourceType>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<BlackoutPeriod> BlackoutPeriods => Set<BlackoutPeriod>();
    public DbSet<ResourceApprover> ResourceApprovers => Set<ResourceApprover>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<RecurringSeries> RecurringSeries => Set<RecurringSeries>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
}
