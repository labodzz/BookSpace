using BookSpace.Application.Auth;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class InvitationRepository(BookSpaceDbContext dbContext) : IInvitationRepository
{
    public async Task AddAsync(Invitation invitation, CancellationToken cancellationToken) =>
        await dbContext.Invitations.AddAsync(invitation, cancellationToken);

    public Task<Invitation?> FindByTokenHashForAcceptanceAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.Invitations.IgnoreQueryFilters().FirstOrDefaultAsync(invitation => invitation.TokenHash == tokenHash, cancellationToken);

    public Task<Invitation?> FindActiveByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Invitations.FirstOrDefaultAsync(
            invitation => invitation.UserId == userId && invitation.AcceptedAtUtc == null && invitation.RevokedAtUtc == null,
            cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
