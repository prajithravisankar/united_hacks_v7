namespace Boys.Ledger.IntegrationTests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

/// <summary>Boots the real ledger API against a chosen connection string so <c>/health/ready</c> can be
/// exercised against a live SQL Server (or a deliberately dead one).</summary>
public sealed class LedgerAppFactory : WebApplicationFactory<Program>
{
    private readonly string _sqlConnectionString;

    public LedgerAppFactory(string sqlConnectionString) => _sqlConnectionString = sqlConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ledger:SqlConnectionString"] = _sqlConnectionString,
            ["Ledger:BrainGrpcAddress"] = "http://127.0.0.1:50061",
        }));
    }
}
