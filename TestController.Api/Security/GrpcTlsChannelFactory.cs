using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestController.Api.Security;

/// <summary>
/// Factory for creating TLS-aware gRPC channels based on <see cref="TransportSecurityOptions"/>.
/// Used by controllers and services that need to connect to agents over gRPC.
/// </summary>
public sealed class GrpcTlsChannelFactory : IDisposable
{
    private readonly TransportSecurityOptions _transport;
    private readonly ILogger<GrpcTlsChannelFactory> _logger;
    private readonly ControllerTimeoutOptions _timeouts;
    private readonly X509Certificate2? _clientCert;

    public GrpcTlsChannelFactory(
        IOptions<SecurityOptions> securityOptions,
        ILogger<GrpcTlsChannelFactory> logger,
        ControllerTimeoutOptions? timeouts = null)
    {
        _transport = securityOptions.Value.Transport;
        _logger = logger;
        _timeouts = timeouts ?? new ControllerTimeoutOptions();

        // Load a client certificate for mTLS if configured
        if (_transport.RequireMutualTls && !string.IsNullOrWhiteSpace(_transport.CertThumbprint))
        {
            _clientCert = LoadCertificateByThumbprint(_transport.CertThumbprint);
        }
    }

    /// <summary>
    /// Determines whether TLS should be used to connect to an agent at the given address.
    /// </summary>
    public bool ShouldUseTls => _transport.GrpcMode != GrpcTransportMode.Plaintext;

    /// <summary>
    /// Builds the appropriate address URI for an agent, switching to HTTPS if TLS is active.
    /// </summary>
    public string BuildAgentAddress(string hostname, int plaintextPort, int tlsPort)
    {
        if (ShouldUseTls)
            return $"https://{hostname}:{tlsPort}";

        return $"http://{hostname}:{plaintextPort}";
    }

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> configured for TLS (or plaintext) gRPC connections.
    /// </summary>
    public SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(_timeouts.ChannelConnectTimeoutSeconds),
            KeepAlivePingDelay = TimeSpan.FromSeconds(_timeouts.KeepAlivePingDelaySeconds),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(_timeouts.KeepAlivePingTimeoutSeconds),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(_timeouts.PooledConnectionIdleMinutes),
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        };

        if (ShouldUseTls)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = ValidateServerCertificate,
            };

            if (_clientCert != null)
            {
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { _clientCert };
            }
        }

        return handler;
    }

    /// <summary>
    /// Creates a fully-configured <see cref="GrpcChannel"/> for the given address.
    /// </summary>
    public GrpcChannel CreateChannel(string address)
    {
        var handler = CreateHandler();
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpClient = httpClient,
            DisposeHttpClient = true,
            MaxReceiveMessageSize = _timeouts.MaxReceiveMessageSizeBytes,
            MaxSendMessageSize = _timeouts.MaxSendMessageSizeBytes,
        });
    }

    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        // If a trusted CA thumbprint is configured, validate against it
        if (!string.IsNullOrWhiteSpace(_transport.TrustedCaThumbprint) && certificate != null)
        {
            var thumbprint = certificate.GetCertHashString();
            if (string.Equals(thumbprint, _transport.TrustedCaThumbprint, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check if the issuer matches
            if (chain != null)
            {
                foreach (var element in chain.ChainElements)
                {
                    if (string.Equals(element.Certificate.Thumbprint, _transport.TrustedCaThumbprint, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        _logger.LogWarning("TLS certificate validation failed: {Errors}, Subject: {Subject}",
            sslPolicyErrors, certificate?.Subject ?? "(null)");
        return false;
    }

    private static X509Certificate2? LoadCertificateByThumbprint(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return certs.Count > 0 ? certs[0] : null;
    }

    public void Dispose()
    {
        _clientCert?.Dispose();
    }
}
