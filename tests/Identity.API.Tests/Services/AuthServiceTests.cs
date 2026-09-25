using Identity.API.Data;
using Identity.API.Dtos;
using Identity.API.Models;
using Identity.API.Security;
using Identity.API.Services;
using Identity.API.Tests.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Identity.API.Tests.Services;

/// <summary>
/// AuthService's login decisions, with every dependency mocked. Rotation, reuse detection and
/// security stamp checks run conditional UPDATEs inside transactions, so they are covered
/// against PostgreSQL in AuthServiceIntegrationTests.
/// </summary>
public sealed class AuthServiceTests : IDisposable
{
    private const string Password = "Str0ng!Passw0rd";

    private readonly Mock<UserManager<AppUser>> userManager = IdentityMocks.UserManager();

    private readonly Mock<IPasswordHasher<AppUser>> passwordHasher = new();

    private readonly Mock<SignInManager<AppUser>> signInManager;

    private readonly Mock<IJwtService> jwtService = new();

    private readonly UnsavedAppDbContext dbContext = new();

    private readonly AuthService service;

    public AuthServiceTests()
    {
        userManager.Object.PasswordHasher = passwordHasher.Object;
        signInManager = IdentityMocks.SignInManager(userManager.Object);
        service = new AuthService(
            dbContext,
            userManager.Object,
            signInManager.Object,
            Mock.Of<IUserClaimsPrincipalFactory<AppUser>>(),
            jwtService.Object,
            new FakeTimeProvider(),
            NullLogger<AuthService>.Instance);
    }

    public void Dispose() => dbContext.Dispose();

    [Fact]
    public async Task LoginAsync_ShouldThrowAuthenticationFailedWithoutIssuingTokens_WhenEmailIsUnknown()
    {
        userManager.Setup(m => m.FindByEmailAsync("ghost@example.com")).ReturnsAsync((AppUser?)null);

        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            service.LoginAsync(new LoginRequest(" ghost@example.com ", Password), TestContext.Current.CancellationToken));

        signInManager.Verify(
            m => m.CheckPasswordSignInAsync(It.IsAny<AppUser>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        jwtService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("NotAllowed")]
    [InlineData("LockedOut")]
    public async Task LoginAsync_ShouldVerifyPasswordHashAndThrowAuthenticationFailed_WhenIdentityRefusesBeforeHashing(string refusal)
    {
        // Identity answers NotAllowed (unconfirmed e-mail) and LockedOut without hashing the
        // password: the service must hash anyway, so the response time hides the account state.
        var user = new AppUser { Id = 7, Email = "someone@example.com" };
        userManager.Setup(m => m.FindByEmailAsync(user.Email)).ReturnsAsync(user);
        signInManager.Setup(m => m.CheckPasswordSignInAsync(user, Password, true))
            .ReturnsAsync(refusal == "LockedOut" ? SignInResult.LockedOut : SignInResult.NotAllowed);

        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            service.LoginAsync(new LoginRequest(user.Email, Password), TestContext.Current.CancellationToken));

        passwordHasher.Verify(h => h.VerifyHashedPassword(It.IsAny<AppUser>(), It.IsAny<string>(), Password), Times.Once);
        jwtService.VerifyNoOtherCalls();

        var audit = Assert.Single(dbContext.ChangeTracker.Entries<AuditLog>()).Entity;
        Assert.Equal(AuditActions.LoginFailed, audit.Action);
        Assert.Equal(refusal, audit.Details);
    }

    /// <summary>
    /// A real AppDbContext on the Npgsql provider whose SaveChangesAsync is a no-op: the failed
    /// login's audit row stays in the change tracker, where the test can inspect it, and no
    /// connection is ever opened.
    /// </summary>
    private sealed class UnsavedAppDbContext() : AppDbContext(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=unreachable.invalid;Database=identity-db;Timeout=1")
            .Options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
