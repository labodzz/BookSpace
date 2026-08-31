namespace BookSpace.Application.Common;

// Response type for mediator commands that have nothing meaningful to return (deletes, removals) -
// IRequestHandler<TRequest, TResponse> always needs a TResponse, and "nothing" isn't a type.
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}
