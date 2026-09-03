using BookSpace.Application.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// A handler's own "does this already exist" pre-check can narrow a race between two concurrent
// requests but can never close it - the database's unique constraints are the real backstop. This
// translates a unique-constraint violation surfacing from SaveChangesAsync into the same
// ConflictException a handler's pre-check throws, so a genuine race still comes back as a clean 409,
// not a raw 500 from GlobalExceptionHandler.
internal static class DbContextConcurrencyExtensions
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public static async Task SaveChangesHandlingConflictsAsync(this DbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException
        {
            Number: UniqueIndexViolation or UniqueConstraintViolation,
        } sqlException)
        {
            // The generic ConflictException message below is all the client sees, so the actual SQL
            // error (which constraint/index fired) would otherwise never reach any log.
            dbContext.GetService<ILoggerFactory>()
                .CreateLogger(typeof(DbContextConcurrencyExtensions).FullName!)
                .LogWarning(exception, "Unique constraint violation (SQL error {SqlErrorNumber}) handled as a conflict", sqlException.Number);
            throw new ConflictException("The request conflicts with existing data.");
        }
    }
}
