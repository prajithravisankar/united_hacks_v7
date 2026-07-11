namespace Boys.Ledger.Api.Infrastructure;

using Boys.Ledger.Api.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

/// <summary>The one place the SQL connection string is read. Everything DB-touching resolves a
/// connection through here.</summary>
public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IOptions<LedgerOptions> options)
        => _connectionString = options.Value.SqlConnectionString;

    public SqlConnection Create() => new(_connectionString);
}
