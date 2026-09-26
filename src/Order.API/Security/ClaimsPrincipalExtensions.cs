using System.Globalization;
using System.Security.Claims;

namespace Order.API.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The identity-api user id carried by the access token's "sub" claim (inbound claim mapping
    /// is off, so the claim keeps its JWT name). Authentication already guarantees it is present.
    /// </summary>
    public static int GetUserId(this ClaimsPrincipal user) =>
        int.Parse(
            user.FindFirstValue("sub") ?? throw new InvalidOperationException("The access token has no sub claim."),
            CultureInfo.InvariantCulture);
}
