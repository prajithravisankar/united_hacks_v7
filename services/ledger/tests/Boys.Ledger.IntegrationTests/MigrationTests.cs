namespace Boys.Ledger.IntegrationTests;

using Boys.Ledger.Migrations;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

/// <summary>Applies the migrations once; exposes whether SQL Server was reachable.</summary>
public sealed class SqlServerFixture : IDisposable
{
    public bool Available { get; }

    public SqlServerFixture()
    {
        try
        {
            var migrator = new Migrator();
            migrator.EnsureDatabase();
            migrator.Apply();
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    public SqlConnection Open()
    {
        var conn = new SqlConnection(DbConfig.BoysConnectionString());
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
    }
}

public sealed class MigrationTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fx;

    public MigrationTests(SqlServerFixture fx) => _fx = fx;

    private SqlConnection Conn()
    {
        Skip.IfNot(_fx.Available, "SQL Server not reachable");

        return _fx.Open();
    }

    // Insert a valid commitment (using seeded demo user + a charity) and return its id.
    private static int InsertValidCommitment(SqlConnection c, long stakeCents = 10000, string deadlineExpr = "DATEADD(DAY, 30, SYSUTCDATETIME())")
    {
        return c.ExecuteScalar<int>(
            "DECLARE @u INT = (SELECT TOP 1 user_id FROM users WHERE role='learner'); "
            + "DECLARE @ch INT = (SELECT TOP 1 charity_id FROM charities); "
            + "INSERT INTO commitments (user_id, goal_text, stake_cents, charity_id, drive_mode, state, deadline) "
            + "OUTPUT INSERTED.commitment_id "
            + $"VALUES (@u, N'Score 90% in History', @stake, @ch, 'AUTO', 'draft', {deadlineExpr});",
            new { stake = stakeCents });
    }

    [SkippableFact]
    public void Migrator_is_idempotent()
    {
        using var _ = Conn();
        new Migrator().Apply().Should().Be(0); // everything already applied
    }

    [SkippableFact]
    public void Seed_reference_data_present()
    {
        using var c = Conn();
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM charities").Should().Be(4);
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM ledger_accounts").Should().Be(6);
        c.ExecuteScalar<int>("SELECT committed_people FROM community_pool_stats WHERE id=1").Should().Be(1204);
    }

    [SkippableTheory]
    [InlineData(1999)]   // below $20.00
    [InlineData(50001)]  // above $500.00
    public void Stake_outside_range_is_rejected(long stakeCents)
    {
        using var c = Conn();
        Assert.Throws<SqlException>(() => InsertValidCommitment(c, stakeCents));
    }

    [SkippableTheory]
    [InlineData("DATEADD(DAY, 3, SYSUTCDATETIME())")]    // < 1 week
    [InlineData("DATEADD(MONTH, 7, SYSUTCDATETIME())")]  // > 6 months
    public void Deadline_outside_range_is_rejected(string deadlineExpr)
    {
        using var c = Conn();
        Assert.Throws<SqlException>(() => InsertValidCommitment(c, deadlineExpr: deadlineExpr));
    }

    [SkippableFact]
    public void Milestone_ordinal_above_five_is_rejected()
    {
        using var c = Conn();
        var commitId = InsertValidCommitment(c);
        Assert.Throws<SqlException>(() => c.Execute(
            "INSERT INTO milestones (commitment_id, ordinal, description, target_metric, due_date, state) "
            + "VALUES (@c, 6, N'x', N'y', DATEADD(DAY,10,SYSUTCDATETIME()), 'pending')",
            new { c = commitId }));
    }

    [SkippableFact]
    public void Duplicate_milestone_ordinal_per_commitment_is_rejected()
    {
        using var c = Conn();
        var commitId = InsertValidCommitment(c);
        c.Execute(
            "INSERT INTO milestones (commitment_id, ordinal, description, target_metric, due_date, state) "
            + "VALUES (@c, 1, N'a', N'b', DATEADD(DAY,10,SYSUTCDATETIME()), 'pending')",
            new { c = commitId });
        Assert.Throws<SqlException>(() => c.Execute(
            "INSERT INTO milestones (commitment_id, ordinal, description, target_metric, due_date, state) "
            + "VALUES (@c, 1, N'c', N'd', DATEADD(DAY,20,SYSUTCDATETIME()), 'pending')",
            new { c = commitId }));
    }

    [SkippableFact]
    public void Duplicate_idempotency_key_is_rejected()
    {
        using var c = Conn();
        var key = Guid.NewGuid().ToString();
        c.Execute("INSERT INTO ledger_transactions (txn_id, idempotency_key) VALUES (NEWID(), @k)", new { k = key });
        Assert.Throws<SqlException>(() =>
            c.Execute("INSERT INTO ledger_transactions (txn_id, idempotency_key) VALUES (NEWID(), @k)", new { k = key }));
    }

    [SkippableFact]
    public void Ledger_postings_are_append_only()
    {
        using var c = Conn();
        var txn = Guid.NewGuid();
        c.Execute("INSERT INTO ledger_transactions (txn_id, idempotency_key) VALUES (@t, @k)",
            new { t = txn, k = Guid.NewGuid().ToString() });
        c.Execute("INSERT INTO ledger_postings (txn_id, account, delta_cents) VALUES (@t, 'USER_ESCROW', 10000)",
            new { t = txn });
        Assert.Throws<SqlException>(() =>
            c.Execute("UPDATE ledger_postings SET delta_cents = 0 WHERE txn_id = @t", new { t = txn }));
        Assert.Throws<SqlException>(() =>
            c.Execute("DELETE FROM ledger_postings WHERE txn_id = @t", new { t = txn }));
    }

    [SkippableFact]
    public void Negative_nav_snapshot_is_rejected()
    {
        using var c = Conn();
        var commitId = InsertValidCommitment(c);
        Assert.Throws<SqlException>(() => c.Execute(
            "INSERT INTO nav_snapshots (commitment_id, as_of_date, nav_cents) VALUES (@c, '2026-01-01', -1)",
            new { c = commitId }));
    }
}
