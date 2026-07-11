namespace Boys.Ledger.Tests.Api;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

/// <summary>Boots the ledger API in-memory with an overridable connection string. A blank string
/// exercises the fail-fast config path; a dummy-but-present string boots the host without ever
/// opening the DB (readiness isn't hit), which is all the non-DB unit tests need.</summary>
public sealed class LedgerAppFactory : WebApplicationFactory<Program>
{
    private readonly string? _sqlConnectionString;

    public LedgerAppFactory(
        string? sqlConnectionString =
            "Server=127.0.0.1,14333;Database=boys;User Id=sa;Password=dummy;TrustServerCertificate=True;Encrypt=False")
        => _sqlConnectionString = sqlConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Added last -> wins over env/appsettings, so the test controls config deterministically.
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ledger:SqlConnectionString"] = _sqlConnectionString,
            ["Ledger:BrainGrpcAddress"] = "http://127.0.0.1:50061",
        }));
    }
}
