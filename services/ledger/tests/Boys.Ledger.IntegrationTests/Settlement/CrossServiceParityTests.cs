namespace Boys.Ledger.IntegrationTests.Settlement;

using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Domain.Abstractions;
using Boys.Ledger.Domain.Settlement;
using FluentAssertions;
using global::Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>R4 cross-service parity: brain previews carry/take-home (Python Decimal ROUND_HALF_EVEN, floored);
/// the ledger independently recomputes them (C# banker's rounding). For the same (principal, NAV) they must
/// agree to the cent — otherwise a user would see one number and be paid another. Runs against the real
/// brain container; skips if it's not up.</summary>
public sealed class CrossServiceParityTests
{
    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    [SkippableTheory]
    [InlineData(2_000)]
    [InlineData(10_000)]
    [InlineData(33_333)]   // odd cents
    [InlineData(50_000)]
    public async Task Brain_valuation_preview_equals_ledger_settlement_to_the_cent(long principalCents)
    {
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:50061");
        var brain = new BrainClient(
            new QuantService.QuantServiceClient(channel),
            new RefereeService.RefereeServiceClient(channel),
            new RealClock(),
            Options.Create(new LedgerOptions
            { SqlConnectionString = "x", BrainGrpcAddress = "http://127.0.0.1:50061", BrainTimeoutMs = 5000 }),
            NullLogger<BrainClient>.Instance);

        Valuation preview;
        try
        {
            preview = await brain.GetValuationAsync(new GetValuationRequest
            {
                CommitmentId = "parity",
                PrincipalCents = principalCents,
                StartDate = "2021-08-13",
                AsOf = "2024-05-19",
                DriveMode = DriveMode.Auto,
            });
        }
        catch (Boys.Ledger.Domain.Errors.BrainUnavailableException)
        {
            Skip.If(true, "brain container not reachable on :50061");
            return;
        }

        // The ledger recomputes carry + take-home from principal and brain's NAV, independently.
        var ledger = SettlementCalculator.CashOut(principalCents, preview.Nav.Cents);

        ledger.Receipt.CarryCents.Should().Be(preview.CarryPreview.Cents, "carry must match brain's preview");
        ledger.Receipt.TakeHomeCents.Should().Be(preview.UserTakeHome.Cents, "take-home must match brain's preview");
    }
}
