using BookSpace.Application.Tenants;
using Xunit;

namespace BookSpace.Application.Tests.Tenants.Create;

public sealed class CreateTenantCommandRequestValidatorTests
{
    private readonly CreateTenantCommandRequestValidator _validator = new();

    private static CreateTenantCommandRequest ValidRequest() => new("Acme Coworking", "Europe/Zagreb", "Ana", "Anic", "ana@acme.test");

    [Fact]
    public void Validate_WithAValidRequest_Passes()
    {
        var result = _validator.Validate(ValidRequest());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_WithAnEmptyName_Fails(string name)
    {
        var result = _validator.Validate(ValidRequest() with { Name = name });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithANameOverTwoHundredCharacters_Fails()
    {
        var result = _validator.Validate(ValidRequest() with { Name = new string('a', 201) });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not/A/Real/Zone")]
    [InlineData("Europe/Nowhere")]
    public void Validate_WithAnInvalidDefaultTimeZoneId_Fails(string timeZoneId)
    {
        var result = _validator.Validate(ValidRequest() with { DefaultTimeZoneId = timeZoneId });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.test")]
    public void Validate_WithAnInvalidAdminEmail_Fails(string email)
    {
        var result = _validator.Validate(ValidRequest() with { AdminEmail = email });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_WithAnEmptyAdminFirstName_Fails(string firstName)
    {
        var result = _validator.Validate(ValidRequest() with { AdminFirstName = firstName });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_WithAnEmptyAdminLastName_Fails(string lastName)
    {
        var result = _validator.Validate(ValidRequest() with { AdminLastName = lastName });

        Assert.False(result.IsValid);
    }
}
