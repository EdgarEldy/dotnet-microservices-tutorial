using Identity.API.Dtos;
using Identity.API.Models;
using Identity.API.Security;
using Identity.API.Services;
using Identity.API.Tests.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Identity.API.Tests.Services;

/// <summary>
/// AuthService's login decision for an unknown account, with every dependency mocked. Rotation,
/// reuse detection and security stamp checks run conditional UPDATEs inside transactions, so they
/// are covered against PostgreSQL in AuthServiceIntegrationTests.
/// </summary>
public sealed class AuthServiceTests : IDisposable
{
    private readonly Mock<UserManager<AppUser>> userManager = IdentityMocks.UserManager();

    private readonly Mock<SignInManager<AppUser>> signInManager;

    private readonly Mock<IJwtService> jwtService = new();

    private readonly Identity.API.Data.AppDbContext dbContext = IdentityMocks.UnreachableDbContext();

    private readonly AuthService service;

    public AuthServiceTests()
    {
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
            service.LoginAsync(new LoginRequest(" ghost@example.com ", "Str0ng!Passw0rd"), TestContext.Current.CancellationToken));

        signInManager.Verify(
            m => m.CheckPasswordSignInAsync(It.IsAny<AppUser>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        jwtService.VerifyNoOtherCalls();
    }
}
