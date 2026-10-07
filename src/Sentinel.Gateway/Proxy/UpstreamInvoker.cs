using System.Net;

namespace Sentinel.Gateway.Proxy;

/// <summary>The HTTP client the forwarder sends upstream requests with. Replaceable so tests can talk to an in-memory upstream.</summary>
public interface IUpstreamInvoker
{
    HttpMessageInvoker Invoker { get; }
}

internal sealed class SocketsUpstreamInvoker : IUpstreamInvoker, IDisposable
{
    private readonly SocketsHttpHandler _handler = new()
    {
        UseProxy = false,
        AllowAutoRedirect = false, // an upstream must not be able to redirect the gateway (and its API key) elsewhere
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    public SocketsUpstreamInvoker() => Invoker = new HttpMessageInvoker(_handler, disposeHandler: false);

    public HttpMessageInvoker Invoker { get; }

    public void Dispose()
    {
        Invoker.Dispose();
        _handler.Dispose();
    }
}
