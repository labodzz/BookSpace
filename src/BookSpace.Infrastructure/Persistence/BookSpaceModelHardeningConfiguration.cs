using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal static class BookSpaceModelHardeningConfiguration
{
    internal static void ConfigureBookSpaceHardening(this ModelBuilder builder)
    {
        builder.Entity<ResourceType>(entity =>
            entity.HasOne<Tenant>().WithMany().HasForeignKey(type => type.TenantId).OnDelete(DeleteBehavior.Restrict));

        builder.Entity<Resource>(entity => entity.ToTable(table =>
            table.HasCheckConstraint("CK_Resources_Capacity", "[Capacity] > 0")));

        builder.Entity<AvailabilityRule>(entity => entity.ToTable(table =>
            table.HasCheckConstraint("CK_AvailabilityRules_TimeRange", "[EndTime] > [StartTime]")));

        builder.Entity<BlackoutPeriod>(entity => entity.ToTable(table =>
            table.HasCheckConstraint("CK_BlackoutPeriods_TimeRange", "[EndUtc] > [StartUtc]")));

        builder.Entity<Booking>(entity => entity.ToTable(table =>
            table.HasCheckConstraint("CK_Bookings_TimeRange", "[EndUtc] > [StartUtc]")));

        builder.Entity<RecurringSeries>(entity => entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_RecurringSeries_Interval", "[Interval] > 0");
            table.HasCheckConstraint(
                "CK_RecurringSeries_EndCondition",
                "([UntilDate] IS NOT NULL OR [OccurrenceCount] IS NOT NULL) AND ([OccurrenceCount] IS NULL OR [OccurrenceCount] > 0)");
        }));
    }
}
