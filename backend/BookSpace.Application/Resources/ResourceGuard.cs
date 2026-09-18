using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Resources;

// Shared by every Resources-feature handler that writes a child record (availability rule, blackout
// period, approver assignment) or the resource itself - Archived is terminal (see
// docs/resource-lifecycle-and-capacity.md), and configuring/updating anything on a resource that's
// effectively deleted from the caller's point of view would silently keep it half-alive. Extracted
// after the same check was independently copy-pasted into four handlers, with the wording already
// starting to drift between copies (one said "cannot be updated", the rest "cannot be modified").
internal static class ResourceGuard
{
    public static void EnsureNotArchived(Resource resource)
    {
        if (resource.Status == ResourceStatus.Archived)
        {
            throw new ConflictException($"Resource {resource.Id} is archived and cannot be modified.");
        }
    }
}
