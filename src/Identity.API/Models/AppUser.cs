using Microsoft.AspNetCore.Identity;

namespace Identity.API.Models;

/// <summary>
/// An account of the system (AspNetUsers). The UserName is the e-mail address.
/// </summary>
public class AppUser : IdentityUser<int>
{
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
