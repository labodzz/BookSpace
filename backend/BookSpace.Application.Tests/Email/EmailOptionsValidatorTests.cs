using BookSpace.Application.Email;
using Xunit;

namespace BookSpace.Application.Tests.Email;

public sealed class EmailOptionsValidatorTests
{
    private readonly EmailOptionsValidator _validator = new();

    [Fact]
    public void Validate_WhenDisabled_SucceedsWithNoSmtpFieldsSet()
    {
        var options = new EmailOptions { Enabled = false };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenEnabledWithEveryRequiredField_Succeeds()
    {
        var options = new EmailOptions
        {
            Enabled = true,
            FromAddress = "bookspace.sender@gmail.com",
            Smtp = new SmtpOptions { Host = "smtp.gmail.com", Port = 587, Username = "bookspace.sender@gmail.com", Password = "app-password" },
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenEnabledWithoutFromAddress_Fails()
    {
        var options = new EmailOptions
        {
            Enabled = true,
            Smtp = new SmtpOptions { Host = "smtp.gmail.com", Username = "u", Password = "p" },
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WhenEnabledWithoutHost_Fails()
    {
        var options = new EmailOptions
        {
            Enabled = true,
            FromAddress = "bookspace.sender@gmail.com",
            Smtp = new SmtpOptions { Host = "", Username = "u", Password = "p" },
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Validate_WhenEnabledWithAnInvalidPort_Fails(int port)
    {
        var options = new EmailOptions
        {
            Enabled = true,
            FromAddress = "bookspace.sender@gmail.com",
            Smtp = new SmtpOptions { Host = "smtp.gmail.com", Port = port, Username = "u", Password = "p" },
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WhenEnabledWithoutUsername_Fails()
    {
        var options = new EmailOptions
        {
            Enabled = true,
            FromAddress = "bookspace.sender@gmail.com",
            Smtp = new SmtpOptions { Host = "smtp.gmail.com", Username = "", Password = "p" },
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WhenEnabledWithoutPassword_Fails()
    {
        var options = new EmailOptions
        {
            Enabled = true,
            FromAddress = "bookspace.sender@gmail.com",
            Smtp = new SmtpOptions { Host = "smtp.gmail.com", Username = "u", Password = "" },
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_FailureMessages_NeverIncludeTheConfiguredPasswordValue()
    {
        const string secretPassword = "super-secret-app-password-value";
        var options = new EmailOptions
        {
            Enabled = true,
            FromAddress = "",
            Smtp = new SmtpOptions { Host = "", Username = "", Password = secretPassword },
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(secretPassword, result.FailureMessage ?? string.Empty);
    }
}
