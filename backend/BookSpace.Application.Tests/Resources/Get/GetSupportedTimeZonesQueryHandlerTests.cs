using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetSupportedTimeZonesQueryHandlerTests
{
    private static GetSupportedTimeZonesQueryHandler CreateSut() => new();

    [Fact]
    public async Task Handle_ReturnsANonEmptyListOfDistinctIds()
    {
        var sut = CreateSut();

        var result = await sut.Handle(new GetSupportedTimeZonesQueryRequest(), CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Equal(result.Count, result.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Sarajevo")]
    [InlineData("America/New_York")]
    [InlineData("Asia/Tokyo")]
    [InlineData("Europe/London")]
    public async Task Handle_IncludesWellKnownZones(string zoneId)
    {
        var sut = CreateSut();

        var result = await sut.Handle(new GetSupportedTimeZonesQueryRequest(), CancellationToken.None);

        Assert.Contains(zoneId, result);
    }

    // The whole point of this endpoint: never offer an id Create/UpdateResourceCommandRequest's own
    // validator (the same TimeZoneValidation.BeAValidTimeZoneId this handler filters through) would
    // then reject - every single returned entry must independently pass it.
    [Fact]
    public async Task Handle_EveryReturnedIdPassesTheSameValidatorCreateAndUpdateResourceUse()
    {
        var sut = CreateSut();

        var result = await sut.Handle(new GetSupportedTimeZonesQueryRequest(), CancellationToken.None);

        Assert.All(result, id => Assert.True(TimeZoneValidation.BeAValidTimeZoneId(id), $"'{id}' should be a valid time zone id"));
    }

    [Fact]
    public async Task Handle_ReturnsIdsInOrdinalSortOrder()
    {
        var sut = CreateSut();

        var result = await sut.Handle(new GetSupportedTimeZonesQueryRequest(), CancellationToken.None);

        Assert.Equal(result.OrderBy(id => id, StringComparer.Ordinal), result);
    }
}
