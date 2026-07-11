namespace Boys.Ledger.Migrations;

using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

/// <summary>Applies ordered .sql migration files to the boys database, recording each so
/// re-runs are no-ops. This is how the SQL Server schema is created (the image has no init dir).</summary>
public sealed class Migrator
{
    private readonly string _migrationsDir;

    public Migrator(string? migrationsDir = null) => _migrationsDir = migrationsDir ?? DbConfig.MigrationsDir();

    /// <summary>Create the boys database if it does not exist.</summary>
    public void EnsureDatabase()
    {
        using var conn = new SqlConnection(DbConfig.MasterConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{DbConfig.DatabaseName}') IS NULL CREATE DATABASE [{DbConfig.DatabaseName}]";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Apply every not-yet-applied migration in filename order. Returns how many ran.</summary>
    public int Apply()
    {
        using var conn = new SqlConnection(DbConfig.BoysConnectionString());
        conn.Open();
        EnsureMigrationsTable(conn);
        var applied = GetApplied(conn);

        var count = 0;
        foreach (var file in Directory.GetFiles(_migrationsDir, "*.sql").OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(file);
            if (applied.Contains(name))
            {
                continue;
            }

            ApplyFile(conn, file, name);
            count++;
        }

        return count;
    }

    private static void EnsureMigrationsTable(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "IF OBJECT_ID('schema_migrations') IS NULL "
            + "CREATE TABLE schema_migrations (filename VARCHAR(256) PRIMARY KEY, applied_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());";
        cmd.ExecuteNonQuery();
    }

    private static HashSet<string> GetApplied(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT filename FROM schema_migrations";
        using var reader = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private static void ApplyFile(SqlConnection conn, string file, string name)
    {
        var batches = Regex.Split(File.ReadAllText(file), @"(?im)^\s*GO\s*$");
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch))
                {
                    continue;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = batch;
                cmd.ExecuteNonQuery();
            }

            using (var rec = conn.CreateCommand())
            {
                rec.Transaction = tx;
                rec.CommandText = "INSERT INTO schema_migrations (filename) VALUES (@f)";
                rec.Parameters.AddWithValue("@f", name);
                rec.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
