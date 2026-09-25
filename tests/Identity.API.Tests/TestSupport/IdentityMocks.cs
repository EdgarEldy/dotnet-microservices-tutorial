using Identity.API.Data;
using Identity.API.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Identity.API.Tests.TestSupport;

/// <summary>Moq doubles of ASP.NET Core Identity's managers, configured like identity-api.</summary>
public static class IdentityMocks
{
    public static IdentityOptions Options() => new()
    {
        Password = { RequiredLength = 8 },
        SignIn = { RequireConfirmedEmail = true },
    };

    public static Mock<UserManager<AppUser>> UserManager() =>
        new(
            Mock.Of<IUserStore<AppUser>>(),
            Microsoft.Extensions.Options.Options.Create(Options()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            new IPasswordValidator<AppUser>[] { new PasswordValidator<AppUser>() },
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance)
        { CallBase = false };

    public static Mock<SignInManager<AppUser>> SignInManager(UserManager<AppUser> userManager) =>
        new(
            userManager,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<AppUser>>(),
            Microsoft.Extensions.Options.Options.Create(Options()),
            NullLogger<SignInManager<AppUser>>.Instance,
            Mock.Of<IAuthenticationSchemeProvider>(),
            Mock.Of<IUserConfirmation<AppUser>>());

    /// <summary>
    /// A real AppDbContext on the Npgsql provider that never opens a connection: the unit tests
    /// only go through paths that must not touch the database, and would fail loudly if they did.
    /// </summary>
    public static AppDbContext UnreachableDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=unreachable.invalid;Database=identity-db;Timeout=1")
            .Options);
}
