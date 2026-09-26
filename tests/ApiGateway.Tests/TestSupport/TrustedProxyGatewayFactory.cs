namespace ApiGateway.Tests.TestSupport;

/// <summary>
/// The gateway deployed behind one trusted proxy: X-Forwarded-For sent from
/// <see cref="ProxyIp"/> is the client IP the login rate limiter partitions by.
/// </summary>
public sealed class TrustedProxyGatewayFactory : GatewayFactory
{
    public const string ProxyIp = "10.0.0.1";

    protected override IEnumerable<KeyValuePair<string, string?>> AdditionalHostSettings =>
    [
        new("ForwardedHeaders:TrustedProxies:0", ProxyIp),
    ];
}
