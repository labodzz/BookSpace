using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

public sealed class BookSpaceDbContext(DbContextOptions<BookSpaceDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ResourceType> ResourceTypes => Set<ResourceType>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<BlackoutPeriod> BlackoutPeriods => Set<BlackoutPeriod>();
    public DbSet<ResourceApprover> ResourceApprovers => Set<ResourceApprover>();
    public DbSet<RecurringSeries> RecurringSeries => Set<RecurringSeries>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ConfigureBookSpaceModel();
    }
}
