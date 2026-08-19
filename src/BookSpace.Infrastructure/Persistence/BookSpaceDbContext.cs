using BookSpace.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

public sealed partial class BookSpaceDbContext(DbContextOptions<BookSpaceDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.TenantId).IsRequired();
            entity.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(user => user.LastName).HasMaxLength(100).IsRequired();
            entity.Property(user => user.IsSuspended).HasDefaultValue(false);
            entity.Property(user => user.CreatedAtUtc).IsRequired();
            entity.HasIndex(user => user.TenantId);
        });

        builder.ConfigureBookSpaceModel();
        builder.ConfigureBookSpaceHardening();
        builder.Entity<ApplicationRole>().HasData(
            RoleSeed.Create(RoleSeed.SysAdminId, "SysAdmin"),
            RoleSeed.Create(RoleSeed.TenantAdminId, "TenantAdmin"),
            RoleSeed.Create(RoleSeed.ApproverId, "Approver"),
            RoleSeed.Create(RoleSeed.MemberId, "Member"));
    }
}

internal static class RoleSeed
{
    internal static readonly Guid SysAdminId = Guid.Parse("a20f52d5-6af2-4657-b3df-3e66d2854688");
    internal static readonly Guid TenantAdminId = Guid.Parse("a8b50aeb-26c8-496e-a1f9-529e05cb2862");
    internal static readonly Guid ApproverId = Guid.Parse("97b5ce71-3f64-488a-982f-2f6734a7af7d");
    internal static readonly Guid MemberId = Guid.Parse("f589746d-b495-4aea-8a3d-11f5791449c4");

    internal static ApplicationRole Create(Guid id, string name) => new()
    {
        Id = id, Name = name, NormalizedName = name.ToUpperInvariant(), ConcurrencyStamp = id.ToString()
    };
}
