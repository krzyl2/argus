using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Builds the single mTLS GrpcChannel for the orchestrator → detector connection.
///
/// D-18: Uses HttpClientHandler.ClientCertificates + ServerCertificateCustomValidationCallback
/// with X509ChainTrustMode.CustomRootTrust. The legacy Grpc.Core credential API is not used.
/// T-04-01: CustomRootTrust pins the Argus CA; rejects certs not chaining to deploy/certs/ca.crt.
/// T-04-03: No insecure GrpcChannel path; all connections enforce mTLS via HttpClientHandler.
///
/// ONE channel per process — register as singleton.
/// </summary>
public static class DetectorChannelFactory
{
    /// <summary>
    /// Creates the mTLS GrpcChannel. Optionally exposes the HttpClientHandler for testing.
    /// </summary>
    public static GrpcChannel Create(ConnectionSettings settings,
        Action<HttpClientHandler>? handlerCapture = null)
    {
        if (string.IsNullOrWhiteSpace(settings.TlsCa))
            throw new ArgumentException("ARGUS_TLS_CA must be set (path to ca.crt)");
        if (string.IsNullOrWhiteSpace(settings.TlsCert))
            throw new ArgumentException("ARGUS_TLS_CERT must be set (path to client.crt)");
        if (string.IsNullOrWhiteSpace(settings.TlsKey))
            throw new ArgumentException("ARGUS_TLS_KEY must be set (path to client.key)");
        if (string.IsNullOrWhiteSpace(settings.DetectorEndpoint))
            throw new ArgumentException("ARGUS_DETECTOR_ENDPOINT must be set");

        // Load CA cert for custom trust store (T-04-01)
        // Note: X509CertificateLoader is .NET 9+; use X509Certificate2 ctor on .NET 8
        var caCert = new X509Certificate2(settings.TlsCa);

        // Load client cert + key (PEM pair)
        var clientCert = X509Certificate2.CreateFromPemFile(settings.TlsCert, settings.TlsKey);

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
    /// Creates the mTLS GrpcChannel and logs the established connection (OBS-01).
    /// </summary>
    public static GrpcChannel Create(ConnectionSettings settings, ILogger logger)
    {
        var channel = Create(settings);
        logger.Log(LogLevel.Information, LogEvents.ChannelEstablished,
            "mTLS gRPC channel established to {Endpoint}", settings.DetectorEndpoint);
        return channel;
    }
}
