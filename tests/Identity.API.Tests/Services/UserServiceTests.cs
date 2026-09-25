using Contracts;
using FluentValidation;
using Identity.API.Dtos;
using Identity.API.Models;
using Identity.API.Security;
using Identity.API.Services;
using Identity.API.Tests.TestSupport;
using MassTransit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Identity.API.Tests.Services;

/// <summary>
/// UserService's decisions that happen before any write: every Identity manager and the publish
/// endpoint are mocked. The transactional paths are covered against PostgreSQL and Kafka in
/// OutboxAtomicityTests and AuthControllerTests.
/// </summary>
public sealed class UserServiceTests : IDisposable
{
    private const string Email = "someone@example.com";

    private readonly Mock<UserManager<AppUser>> userManager = IdentityMocks.UserManager();

    private readonly Mock<IPublishEndpoint> publishEndpoint = new();

    private readonly Identity.API.Data.AppDbContext dbContext = IdentityMocks.UnreachableDbContext();

    private readonly UserService service;

    public UserServiceTests() =>
        service = new UserService(
            dbContext,
            userManager.Object,
            Mock.Of<IRoleService>(),
            publishEndpoint.Object,
            new FakeTimeProvider(),
            NullLogger<UserService>.Instance);

    public void Dispose() => dbContext.Dispose();

    [Fact]
    public async Task RegisterAsync_ShouldNeitherCreateUserNorPublish_WhenEmailIsAlreadyRegistered()
    {
        userManager.Setup(m => m.FindByEmailAsync(Email)).ReturnsAsync(new AppUser { Id = 7, Email = Email });

        await service.RegisterAsync(new RegisterRequest($"  {Email} ", "Str0ng!Passw0rd"), TestContext.Current.CancellationToken);

        userManager.Verify(m => m.CreateAsync(It.IsAny<AppUser>(), It.IsAny<string>()), Times.Never);
        publishEndpoint.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegisterAsync_ShouldThrowValidationExceptionOnPassword_WhenPasswordBreaksPolicy()
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.RegisterAsync(new RegisterRequest(Email, "alllowercase"), TestContext.Current.CancellationToken));

        Assert.NotEmpty(exception.Errors);
        Assert.All(exception.Errors, failure => Assert.Equal(nameof(RegisterRequest.Password), failure.PropertyName));
        userManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        userManager.Verify(m => m.CreateAsync(It.IsAny<AppUser>(), It.IsAny<string>()), Times.Never);
        publishEndpoint.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegisterAsync_ShouldThrowSameValidationException_WhenWeakPasswordTargetsExistingEmail()
    {
        userManager.Setup(m => m.FindByEmailAsync(Email)).ReturnsAsync(new AppUser { Id = 7, Email = Email });

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.RegisterAsync(new RegisterRequest(Email, "alllowercase"), TestContext.Current.CancellationToken));

        publishEndpoint.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForgotPasswordAsync_ShouldNotPublish_WhenEmailIsUnknown()
    {
        userManager.Setup(m => m.FindByEmailAsync(Email)).ReturnsAsync((AppUser?)null);

        await service.ForgotPasswordAsync(new ForgotPasswordRequest(Email), TestContext.Current.CancellationToken);

        userManager.Verify(m => m.GeneratePasswordResetTokenAsync(It.IsAny<AppUser>()), Times.Never);
        publishEndpoint.Verify(p => p.Publish(It.IsAny<PasswordResetRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        publishEndpoint.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmEmailAsync_ShouldThrowValidationExceptionOnToken_WhenUserIsUnknown()
    {
        userManager.Setup(m => m.FindByIdAsync("99")).ReturnsAsync((AppUser?)null);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.ConfirmEmailAsync(new ConfirmEmailRequest(99, TokenEncoding.Encode("token")), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ConfirmEmailRequest.Token), Assert.Single(exception.Errors).PropertyName);
    }

    [Fact]
    public async Task ConfirmEmailAsync_ShouldThrowValidationExceptionWithoutCallingIdentity_WhenTokenIsNotBase64Url()
    {
        userManager.Setup(m => m.FindByIdAsync("7")).ReturnsAsync(new AppUser { Id = 7, Email = Email });

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.ConfirmEmailAsync(new ConfirmEmailRequest(7, "not base64url!"), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ConfirmEmailRequest.Token), Assert.Single(exception.Errors).PropertyName);
        userManager.Verify(m => m.ConfirmEmailAsync(It.IsAny<AppUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmEmailAsync_ShouldThrowSameValidationException_WhenIdentityRejectsToken()
    {
        var user = new AppUser { Id = 7, Email = Email };
        userManager.Setup(m => m.FindByIdAsync("7")).ReturnsAsync(user);
        userManager.Setup(m => m.ConfirmEmailAsync(user, "raw-token"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityErrorDescriber().InvalidToken()));

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.ConfirmEmailAsync(new ConfirmEmailRequest(7, TokenEncoding.Encode("raw-token")), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ConfirmEmailRequest.Token), Assert.Single(exception.Errors).PropertyName);
    }
}
