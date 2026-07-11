namespace Boys.Ledger.Api.Infrastructure;

using Microsoft.Data.SqlClient;

/// <summary>Hands out fresh SQL Server connections. One choke-point for the connection string so
/// repositories never see it and tests can point the whole app at any database.</summary>
public interface IDbConnectionFactory
{
    /// <summary>A new, unopened connection. Caller owns disposal (Dapper/`await using`).</summary>
    SqlConnection Create();
}
