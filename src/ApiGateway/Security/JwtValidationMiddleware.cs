using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Model;

namespace ApiGateway.Security;

/// <summary>
/// Rejects with a 401 ProblemDetails every proxied request that does not carry a valid access
/// token, before it reaches a downstream service. The token is validated with the shared JwtBearer
/// scheme from ServiceDefaults (signature, expiration, issuer, audience); permissions are not
/// checked here, the downstream service re-validates the token and applies them.
/// Non-proxied endpoints (health checks) and the public identity endpoints pass through.
/// </summary>
public sealed class JwtValidationMiddleware(RequestDelegate next)
{
    // Called without a valid access token by design: Register/Login/ConfirmEmail before any
    // token exists, Refresh precisely when the access token has expired, ForgotPassword and
    // ResetPassword when the user cannot log in. PathString comparison ignores case.
    private static readonly PathString[] PublicPaths =
    [
        "/api/v1/Auth/Register",
        "/api/v1/Auth/Login",
        "/api/v1/Auth/ConfirmEmail",
        "/api/v1/Auth/Refresh",
        "/api/v1/Auth/ForgotPassword",
        "/api/v1/Auth/ResetPassword",
    ];

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        if (!IsProxied(context) || IsPublic(context.Request.Path))
        {
            await next(context);
            return;
        }

        var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!result.Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Unauthorized",
                    Detail = "A valid access token is required.",
                },
            });
            return;
        }

        context.User = result.Principal;
        await next(context);
    }

    // YARP adds its RouteModel to the metadata of every proxied endpoint.
    private static bool IsProxied(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is not null;

    private static bool IsPublic(PathString path) =>
        Array.Exists(PublicPaths, publicPath => path.Equals(publicPath));
}
