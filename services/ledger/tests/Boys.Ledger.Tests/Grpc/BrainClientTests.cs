namespace Boys.Ledger.Tests.Grpc;

using System.Net;
using System.Net.Sockets;
using Boys.Contracts.Brain.V1;
using Boys.Contracts.Common.V1;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Errors;
using FluentAssertions;
using global::Grpc.Core;
using global::Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>Proves the generated brain gRPC client round-trips through <see cref="BrainClient"/> against a
/// real (stubbed) server, and that any transport failure becomes a <see cref="BrainUnavailableException"/>
/// — never a raw RpcException leaking to callers.</summary>
public class BrainClientTests
{
    private sealed class RealTimeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;  // deadline math only; not under test
    }

    private sealed class StubQuant : QuantService.QuantServiceBase
    {
        public override Task<Valuation> GetValuation(GetValuationRequest request, ServerCallContext context)
            => Task.FromResult(new Valuation
            {
                Nav = new Money { Cents = 15500, Currency = "USD" },
                Principal = new Money { Cents = 10000, Currency = "USD" },
                Gain = new Money { Cents = 5500, Currency = "USD" },
                CarryPreview = new Money { Cents = 825, Currency = "USD" },
                UserTakeHome = new Money { Cents = 14675, Currency = "USD" },
            });
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<WebApplication> StartStubAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<KestrelServerOptions>(o =>
            o.ListenLocalhost(port, lo => lo.Protocols = HttpProtocols.Http2));  // h2c for plaintext gRPC
        builder.Services.AddGrpc();
        var app = builder.Build();
        app.MapGrpcService<StubQuant>();
        await app.StartAsync();
        return app;
    }

    private static BrainClient ClientFor(GrpcChannel channel, string address, int timeoutMs) => new(
        new QuantService.QuantServiceClient(channel),
        new RefereeService.RefereeServiceClient(channel),
        new RealTimeClock(),
        Options.Create(new LedgerOptions
        {
            SqlConnectionString = "x",
            BrainGrpcAddress = address,
            BrainTimeoutMs = timeoutMs,
        }),
        NullLogger<BrainClient>.Instance);

    [Fact]
    public async Task GetValuation_round_trips_through_the_generated_client()
    {
        var port = FreePort();
        await using var stub = await StartStubAsync(port);
        var address = $"http://127.0.0.1:{port}";
        using var channel = GrpcChannel.ForAddress(address);
        var client = ClientFor(channel, address, timeoutMs: 5000);

        var valuation = await client.GetValuationAsync(new GetValuationRequest
        {
            CommitmentId = "c1",
            PrincipalCents = 10000,
            StartDate = "2021-08-13",
            AsOf = "2024-05-19",
        });

        valuation.UserTakeHome.Cents.Should().Be(14675);  // came back over the wire, unchanged
        valuation.CarryPreview.Cents.Should().Be(825);
    }

    [Fact]
    public async Task Unreachable_brain_becomes_a_typed_BrainUnavailableException()
    {
        var deadPort = FreePort();  // nothing is listening here
        var address = $"http://127.0.0.1:{deadPort}";
        using var channel = GrpcChannel.ForAddress(address);
        var client = ClientFor(channel, address, timeoutMs: 1500);

        var act = () => client.GetValuationAsync(new GetValuationRequest
        {
            CommitmentId = "c1",
            PrincipalCents = 10000,
            StartDate = "2021-08-13",
            AsOf = "2024-05-19",
        });

        await act.Should().ThrowAsync<BrainUnavailableException>();
    }
}
