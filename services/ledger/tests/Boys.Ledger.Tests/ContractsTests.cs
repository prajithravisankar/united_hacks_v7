using Boys.Contracts.Common.V1;
using FluentAssertions;
using Xunit;

namespace Boys.Ledger.Tests;

// B03: proves the generated C# gRPC types compile and construct.
public class ContractsTests
{
    [Fact]
    public void Money_constructs_with_cents()
    {
        var m = new Money { Cents = 100, Currency = "USD" };
        m.Cents.Should().Be(100);
    }
}
