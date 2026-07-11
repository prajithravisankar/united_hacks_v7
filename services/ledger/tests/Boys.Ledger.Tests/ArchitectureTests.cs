namespace Boys.Ledger.Tests;

using Boys.Ledger.Domain.Abstractions;
using FluentAssertions;
using Xunit;

/// <summary>The load-bearing architecture rule: the Domain project is pure. It must not reference any
/// I/O framework, so settlement and the state machine stay unit-testable without a database or gRPC.
/// If someone adds Dapper to Domain "just for a second", this goes red.</summary>
public class ArchitectureTests
{
    [Theory]
    [InlineData("Dapper")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("Grpc")]
    [InlineData("Google.Protobuf")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.Extensions.Http")]
    public void Domain_assembly_references_no_io_framework(string forbiddenPrefix)
    {
        var referenced = typeof(IClock).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);

        referenced.Should().NotContain(
            name => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase),
            because: $"Domain must not depend on {forbiddenPrefix} (it stays pure)");
    }
}
