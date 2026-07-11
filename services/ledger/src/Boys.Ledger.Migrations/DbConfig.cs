namespace Boys.Ledger.Migrations;

/// <summary>Builds SQL Server connection strings from env / the repo .env (no shell eval).</summary>
public static class DbConfig
{
    public const string DatabaseName = "boys";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root (docker-compose.yml) not found");
    }

    public static string MigrationsDir() => Path.Combine(RepoRoot(), "services", "ledger", "migrations");

    private static void LoadDotEnv()
    {
        var envFile = Path.Combine(RepoRoot(), ".env");
        if (!File.Exists(envFile))
        {
            return;
        }

        foreach (var raw in File.ReadAllLines(envFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var idx = line.IndexOf('=');
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, val);
            }
        }
    }

    private static string Password()
    {
        if (Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD") is null)
        {
            LoadDotEnv();
        }

        return Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD")
            ?? throw new InvalidOperationException("MSSQL_SA_PASSWORD not set");
    }

    private static string Host() => Environment.GetEnvironmentVariable("MSSQL_HOST") ?? "127.0.0.1";

    private static string Port() => Environment.GetEnvironmentVariable("MSSQL_PORT") ?? "14333";

    private static string ConnStr(string database) =>
        $"Server={Host()},{Port()};Database={database};User Id=sa;Password={Password()};"
        + "TrustServerCertificate=True;Encrypt=False;Connect Timeout=10";

    public static string MasterConnectionString() => ConnStr("master");

    public static string BoysConnectionString() => ConnStr(DatabaseName);
}
