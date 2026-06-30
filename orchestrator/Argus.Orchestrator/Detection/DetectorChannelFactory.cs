using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Builds the GrpcChannel for the orchestrator → detector connection.
///
/// Two modes are supported based on the URI scheme of DetectorEndpoint:
///
/// - Local mode (http:// scheme or loopback host): insecure h2c channel with no cert files
///   required. Intended for the add-on deployment where detector and orchestrator share a
///   container and communicate on 127.0.0.1:50051. This intentionally overrides the v1
///   constraint T-04-03 ("no insecure path") for in-container loopback only; no LAN exposure.
///
/// - Remote mode (https:// scheme): mTLS channel using HttpClientHandler.ClientCertificates
///   with X509ChainTrustMode.CustomRootTrust CA pinning. D-18 is fully enforced: mutual TLS
///   with the server cert pinned to the Argus CA. The v1 code path is byte-for-byte unchanged.
///
/// ONE channel per process — register as singleton.
/// </summary>
public static class DetectorChannelFactory
{
    private const string Http2UnencryptedSupportSwitch =
        "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport";

    /// <summary>
    /// Creates the GrpcChannel.
    ///
    /// Local-mode endpoints (http:// or loopback host) return an insecure h2c channel; no
    /// cert files are loaded. Remote endpoints (https://) use the existing mTLS path.
    /// The optional handlerCapture is only invoked in remote (mTLS) mode.
    /// </summary>
    public static GrpcChannel Create(ConnectionSettings settings,
        Action<HttpClientHandler>? handlerCapture = null)
    {
        // Endpoint check runs first so local mode does not require cert env vars.
        if (string.IsNullOrWhiteSpace(settings.DetectorEndpoint))
            throw new ArgumentException("ARGUS_DETECTOR_ENDPOINT must be set");

        if (IsLocalMode(settings.DetectorEndpoint))
        {
            // h2c (HTTP/2 cleartext) over loopback requires this process-global switch (Pitfall 11).
            AppContext.SetSwitch(Http2UnencryptedSupportSwitch, true);
            return GrpcChannel.ForAddress(settings.DetectorEndpoint, new GrpcChannelOptions());
        }

        // Remote path: existing mTLS logic (D-18) — unchanged from v1.
        if (string.IsNullOrWhiteSpace(settings.TlsCa))
            throw new ArgumentException("ARGUS_TLS_CA must be set (path to ca.crt)");
        if (string.IsNullOrWhiteSpace(settings.TlsCert))
            throw new ArgumentException("ARGUS_TLS_CERT must be set (path to client.crt)");
        if (string.IsNullOrWhiteSpace(settings.TlsKey))
            throw new ArgumentException("ARGUS_TLS_KEY must be set (path to client.key)");

        // Load CA cert for custom trust store (T-04-01)
        // Note: X509CertificateLoader is .NET 9+; use X509Certificate2 ctor on .NET 8
        var caCert = new X509Certificate2(settings.TlsCa);

        // Load client cert + key (PEM pair)
        // .NET 8 safe pattern: export to PKCS12 so the private key is persisted into the
        // cert object and not subject to GC before TLS handshake (Linux ephemeral key issue).
        using var tempCert = X509Certificate2.CreateFromPemFile(settings.TlsCert, settings.TlsKey);
        var clientCert = new X509Certificate2(tempCert.Export(X509ContentType.Pkcs12));

        var handler = new HttpClientHandler();

        // D-18: Add client cert for mutual TLS
        handler.ClientCertificates.Add(clientCert);

        // D-18: Custom server cert validation with CustomRootTrust (pins Argus CA)
        handler.ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
        {
            chain!.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(caCert);
            return chain.Build(cert!);
        };

        handlerCapture?.Invoke(handler);

        var channel = GrpcChannel.ForAddress(
            settings.DetectorEndpoint,
            new GrpcChannelOptions { HttpHandler = handler });

        return channel;
    }

    /// <summary>
    /// Creates the GrpcChannel and logs the active mode: insecure loopback or mTLS (OBS-01).
    /// </summary>
    public static GrpcChannel Create(ConnectionSettings settings, ILogger logger)
    {
        var channel = Create(settings);
        if (IsLocalMode(settings.DetectorEndpoint!))
        {
            logger.Log(LogLevel.Information, LogEvents.ChannelEstablished,
                "Insecure loopback gRPC channel established to {Endpoint}", settings.DetectorEndpoint);
        }
        else
        {
            logger.Log(LogLevel.Information, LogEvents.ChannelEstablished,
                "mTLS gRPC channel established to {Endpoint}", settings.DetectorEndpoint);
        }
        return channel;
    }

    /// <summary>
    /// Returns true when the endpoint should use an insecure h2c channel.
    /// http:// scheme is the primary discriminator; loopback host is the safety net
    /// (config-gen always writes http://127.0.0.1:50051 for local mode).
    /// </summary>
    private static bool IsLocalMode(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp
            || uri.Host == "127.0.0.1"
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "::1");
}
