using BookSpace.Application.Mediator;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Mediator;

public sealed class ValidationBehaviorTests
{
    public sealed record TestRequest(string Value) : IRequest<string>;

    [Fact]
    public async Task Handle_WithNoValidatorsRegistered_InvokesNext()
    {
        var sut = new ValidationBehavior<TestRequest, string>([]);
        var nextCalled = false;

        var result = await sut.Handle(new TestRequest("ok"), NextReturningHandled, CancellationToken.None);

        Assert.True(nextCalled);
        Assert.Equal("handled", result);

        Task<string> NextReturningHandled()
        {
            nextCalled = true;
            return Task.FromResult("handled");
        }
    }

    [Fact]
    public async Task Handle_WhenAllValidatorsPass_InvokesNext()
    {
        var validator = new Mock<IValidator<TestRequest>>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<IValidationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var sut = new ValidationBehavior<TestRequest, string>([validator.Object]);

        var result = await sut.Handle(new TestRequest("ok"), () => Task.FromResult("handled"), CancellationToken.None);

        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task Handle_WhenAValidatorFails_ThrowsValidationExceptionWithoutInvokingNext()
    {
        var failure = new ValidationFailure(nameof(TestRequest.Value), "Value is required.");
        var validator = new Mock<IValidator<TestRequest>>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<IValidationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([failure]));
        var sut = new ValidationBehavior<TestRequest, string>([validator.Object]);
        var nextCalled = false;

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            sut.Handle(new TestRequest(""), NextReturningHandled, CancellationToken.None));

        Assert.False(nextCalled);
        Assert.Contains(exception.Errors, e => e.PropertyName == nameof(TestRequest.Value));

        Task<string> NextReturningHandled()
        {
            nextCalled = true;
            return Task.FromResult("handled");
        }
    }

    [Fact]
    public async Task Handle_WhenMultipleValidatorsFail_AggregatesAllFailures()
    {
        var firstValidator = new Mock<IValidator<TestRequest>>();
        firstValidator.Setup(v => v.ValidateAsync(It.IsAny<IValidationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Value", "Value is required.")]));
        var secondValidator = new Mock<IValidator<TestRequest>>();
        secondValidator.Setup(v => v.ValidateAsync(It.IsAny<IValidationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Value", "Value is too short.")]));
        var sut = new ValidationBehavior<TestRequest, string>([firstValidator.Object, secondValidator.Object]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            sut.Handle(new TestRequest(""), () => Task.FromResult("handled"), CancellationToken.None));

        Assert.Equal(2, exception.Errors.Count());
    }
}
