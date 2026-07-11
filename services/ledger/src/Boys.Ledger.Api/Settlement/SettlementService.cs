namespace Boys.Ledger.Api.Settlement;

using Boys.Contracts.Brain.V1;
using Boys.Ledger.Api.Commitments;
using Boys.Ledger.Api.Configuration;
using Boys.Ledger.Api.Grpc;
using Boys.Ledger.Api.Infrastructure;
using Boys.Ledger.Api.Ledger;
using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using Boys.Ledger.Domain.Ledger;
using Boys.Ledger.Domain.Settlement;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

/// <summary>Settles a commitment, all-or-nothing and exactly-once. It fetches the NAV from brain, lets the
/// pure <see cref="SettlementCalculator"/> compute the money (carry, floor, splits — the ledger is the money
/// authority), posts the balanced group through the B12 gate, moves the commitment to settled, and records
/// the receipt. All internal keys are derived from the commitment (<c>settle:{id}</c>), so concurrent or
/// retried settlements converge to a single settlement — never a double payout.</summary>
public sealed class SettlementService
{
    private const int UniqueViolation = 2627;
    private const int DuplicateKey = 2601;

    private readonly IDbConnectionFactory _factory;
    private readonly ICommitmentRepository _commitments;
    private readonly ILedgerRepository _ledger;
    private readonly LedgerService _ledgerService;
    private readonly IBrainClient _brain;
    private readonly LedgerOptions _options;
    private readonly ILogger<SettlementService> _logger;

    public SettlementService(
        IDbConnectionFactory factory,
        ICommitmentRepository commitments,
        ILedgerRepository ledger,
        LedgerService ledgerService,
        IBrainClient brain,
        IOptions<LedgerOptions> options,
        ILogger<SettlementService> logger)
    {
        _factory = factory;
        _commitments = commitments;
        _ledger = ledger;
        _ledgerService = ledgerService;
        _brain = brain;
        _options = options.Value;
        _logger = logger;
    }

    private sealed record CommitmentRow(long StakeCents, string DriveMode);

    public async Task<SettlementReceipt> SettleAsync(int commitmentId, CancellationToken cancellationToken = default)
    {
        var state = (await _commitments.GetAsync(commitmentId, cancellationToken)).State;  // applies the deadline gate
        if (state == CommitmentState.Settled)
        {
            return await GetReceiptAsync(commitmentId, cancellationToken)
                   ?? throw new InvalidOperationException($"commitment {commitmentId} is settled but has no receipt");
        }

        var type = state switch
        {
            CommitmentState.CashedOut => SettlementType.CashOut,
            CommitmentState.Succeeded => SettlementType.Success,
            CommitmentState.Failed => SettlementType.Failure,
            _ => throw new IllegalTransitionException(state.ToDb(), "settle"),  // not a settle-able state
        };

        var commitment = await GetCommitmentAsync(commitmentId, cancellationToken)
                         ?? throw new CommitmentNotFoundException(commitmentId);
        var navCents = await FetchNavAsync(commitmentId, commitment, cancellationToken);

        var key = $"settle:{commitmentId}";  // commitment-derived -> settlement is exactly-once per commitment

        // 1. Move the money (idempotent + serialized per commitment). Success recomputes a smaller bonus if
        //    the shared winners pool can't cover the target.
        var plan = await ComputeAndPostAsync(commitmentId, type, commitment.StakeCents, navCents, key, cancellationToken);
        // 2. Record the receipt BEFORE flipping to settled — a crash between the two must never leave a
        //    settled commitment with no receipt (which would brick the settle endpoint forever).
        await InsertReceiptAsync(commitmentId, plan.Receipt, key, cancellationToken);
        // 3. Flip the state to settled (idempotent by the same key).
        await _commitments.TransitionAsync(commitmentId, CommitmentCommand.Settle, isFinalLeg: false, key, cancellationToken);

        _logger.LogInformation(
            "settled commitment {CommitmentId} as {Type}: nav={Nav} take_home={TakeHome} carry={Carry} charity={Charity} bonus={Bonus}",
            commitmentId, type, navCents, plan.Receipt.TakeHomeCents, plan.Receipt.CarryCents,
            plan.Receipt.CharityCents, plan.Receipt.BonusCents);

        return plan.Receipt;
    }

    public Task<SettlementReceipt?> GetReceiptAsync(int commitmentId, CancellationToken cancellationToken = default)
        => ReadReceiptAsync(commitmentId, cancellationToken);

    /// <summary>Builds the plan and posts it (idempotent by <paramref name="key"/>). For Success, the bonus
    /// is drawn from the shared winners pool; if a concurrent settlement drained it below the target, the
    /// draw is rejected and we recompute a smaller bonus from the live balance and retry. The final attempt
    /// falls back to no bonus, guaranteeing termination and that the pool is never over-drawn.</summary>
    private async Task<SettlementPlan> ComputeAndPostAsync(
        int commitmentId, SettlementType type, long principalCents, long navCents, string key, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 0; ; attempt++)
        {
            var plan = type switch
            {
                SettlementType.CashOut => SettlementCalculator.CashOut(principalCents, navCents),
                SettlementType.Success => SettlementCalculator.Success(principalCents, navCents,
                    attempt >= maxAttempts - 1
                        ? 0  // last resort: settle with no bonus rather than loop forever
                        : await _ledger.GetAccountBalanceAsync(LedgerAccount.WinnersBonusPool, cancellationToken)),
                _ => SettlementCalculator.Failure(principalCents, navCents),
            };

            try
            {
                await _ledger.PostAsync(_ledgerService.BuildTransfer(commitmentId, plan.Postings, key), cancellationToken);
                return plan;
            }
            catch (InsufficientPoolException) when (type == SettlementType.Success && attempt < maxAttempts - 1)
            {
                _logger.LogWarning("winners pool shrank during settlement of {CommitmentId}; recomputing the bonus", commitmentId);
            }
        }
    }

    private async Task<long> FetchNavAsync(int commitmentId, CommitmentRow commitment, CancellationToken cancellationToken)
    {
        var driveMode = commitment.DriveMode == "USER" ? DriveMode.User : DriveMode.Auto;
        var valuation = await _brain.GetValuationAsync(new GetValuationRequest
        {
            CommitmentId = commitmentId.ToString(),
            PrincipalCents = commitment.StakeCents,
            StartDate = _options.FundStartDate,
            AsOf = _options.FundAsOfDate,
            DriveMode = driveMode,
        }, cancellationToken);
        return valuation.Nav.Cents;
    }

    private async Task<CommitmentRow?> GetCommitmentAsync(int commitmentId, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        return await conn.QuerySingleOrDefaultAsync<CommitmentRow>(new CommandDefinition(
            "SELECT stake_cents AS StakeCents, drive_mode AS DriveMode FROM commitments WHERE commitment_id = @id",
            new { id = commitmentId }, cancellationToken: cancellationToken));
    }

    private async Task InsertReceiptAsync(
        int commitmentId, SettlementReceipt receipt, string key, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO settlements (commitment_id, settlement_type, principal_cents, nav_cents, gain_cents, "
                + "carry_cents, charity_cents, bonus_cents, take_home_cents, idempotency_key) "
                + "VALUES (@commitmentId, @type, @principal, @nav, @gain, @carry, @charity, @bonus, @takeHome, @key)",
                new
                {
                    commitmentId,
                    type = receipt.Type.ToString(),
                    principal = receipt.PrincipalCents,
                    nav = receipt.NavCents,
                    gain = receipt.GainCents,
                    carry = receipt.CarryCents,
                    charity = receipt.CharityCents,
                    bonus = receipt.BonusCents,
                    takeHome = receipt.TakeHomeCents,
                    key,
                }, cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or DuplicateKey)
        {
            // Receipt already recorded (a retry) — nothing to do.
        }
    }

    private async Task<SettlementReceipt?> ReadReceiptAsync(int commitmentId, CancellationToken cancellationToken)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<(string Type, long Principal, long Nav, long Gain, long Carry, long Charity, long Bonus, long TakeHome)?>(
            new CommandDefinition(
                "SELECT settlement_type, principal_cents, nav_cents, gain_cents, carry_cents, charity_cents, "
                + "bonus_cents, take_home_cents FROM settlements WHERE commitment_id = @id",
                new { id = commitmentId }, cancellationToken: cancellationToken));

        if (row is null)
        {
            return null;
        }

        var r = row.Value;
        return new SettlementReceipt(
            Enum.Parse<SettlementType>(r.Type), r.Principal, r.Nav, r.Gain, r.Carry, r.Charity, r.Bonus, r.TakeHome);
    }
}
