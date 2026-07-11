using Boys.Ledger.Domain;
using FluentAssertions;
using Xunit;

namespace Boys.Ledger.Tests;

// B01: proves the xUnit + FluentAssertions harness runs against the Domain project.
public class HarnessTests
{
    [Fact]
    public void Add_returns_sum()
    {
        Arithmetic.Add(2, 3).Should().Be(5);
    }
}
