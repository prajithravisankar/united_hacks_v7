namespace Boys.Ledger.Tests.Commitments;

using Boys.Ledger.Domain.Commitments;
using Boys.Ledger.Domain.Errors;
using FluentAssertions;
using Xunit;
using static Boys.Ledger.Domain.Commitments.CommitmentCommand;
using static Boys.Ledger.Domain.Commitments.CommitmentState;

/// <summary>B13 machine: the ENTIRE transition matrix is specified. Every legal (state, command, finalLeg)
/// triple maps to its target; every other triple throws. Nothing is left unspecified.</summary>
public class CommitmentMachineTests
{
    // The complete set of legal transitions, keyed on (from, command, isFinalLeg).
    private static readonly Dictionary<(CommitmentState, CommitmentCommand, bool), CommitmentState> Legal = Build();

    private static Dictionary<(CommitmentState, CommitmentCommand, bool), CommitmentState> Build()
    {
        var map = new Dictionary<(CommitmentState, CommitmentCommand, bool), CommitmentState>();
        foreach (var final in new[] { true, false })
        {
            map[(Draft, Activate, final)] = Active;
            map[(Active, SubmitProof, final)] = PendingVerification;
            map[(Riding, SubmitProof, final)] = PendingVerification;
            map[(PendingVerification, ClearMilestone, final)] = MilestoneCleared;
            map[(PendingVerification, RejectMilestone, final)] = Failed;   // hard gate
            map[(CashedOut, Settle, final)] = Settled;
            map[(Succeeded, Settle, final)] = Settled;
            map[(Failed, Settle, final)] = Settled;
        }

        // Conditional on which leg we just cleared:
        map[(MilestoneCleared, CashOut, false)] = CashedOut;   // bow out on a non-final leg
        map[(MilestoneCleared, Ride, false)] = Riding;         // compound to the next leg
        map[(MilestoneCleared, Complete, true)] = Succeeded;   // final leg -> success only
        return map;
    }

    public static IEnumerable<object[]> EveryTriple()
    {
        foreach (CommitmentState state in Enum.GetValues(typeof(CommitmentState)))
        {
            foreach (CommitmentCommand command in Enum.GetValues(typeof(CommitmentCommand)))
            {
                foreach (var final in new[] { true, false })
                {
                    yield return new object[] { state, command, final };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(EveryTriple))]
    public void Every_state_command_pair_is_either_legal_or_throws(
        CommitmentState state, CommitmentCommand command, bool isFinalLeg)
    {
        if (Legal.TryGetValue((state, command, isFinalLeg), out var expected))
        {
            CommitmentMachine.Next(state, command, isFinalLeg).Should().Be(expected);
        }
        else
        {
            var act = () => CommitmentMachine.Next(state, command, isFinalLeg);
            act.Should().Throw<IllegalTransitionException>();
        }
    }

    [Fact]
    public void Cash_out_and_ride_are_illegal_on_the_final_leg()
    {
        var cashOut = () => CommitmentMachine.Next(MilestoneCleared, CashOut, isFinalLeg: true);
        var ride = () => CommitmentMachine.Next(MilestoneCleared, Ride, isFinalLeg: true);
        var completeEarly = () => CommitmentMachine.Next(MilestoneCleared, Complete, isFinalLeg: false);

        cashOut.Should().Throw<IllegalTransitionException>();
        ride.Should().Throw<IllegalTransitionException>();
        completeEarly.Should().Throw<IllegalTransitionException>();  // can't "succeed" before the final leg
    }

    [Theory]
    [InlineData(Active)]
    [InlineData(PendingVerification)]
    [InlineData(MilestoneCleared)]
    [InlineData(Riding)]
    public void Passed_deadline_fails_a_live_leg(CommitmentState live)
    {
        CommitmentMachine.ApplyDeadline(live, deadlinePassed: true).Should().Be(Failed);
        CommitmentMachine.IsDeadlineTrippable(live).Should().BeTrue();
    }

    [Theory]
    [InlineData(Draft)]      // pre-commitment: no stake, nothing to fail
    [InlineData(CashedOut)]
    [InlineData(Succeeded)]
    [InlineData(Failed)]
    [InlineData(Settled)]
    public void Passed_deadline_leaves_non_live_states_untouched(CommitmentState state)
    {
        CommitmentMachine.ApplyDeadline(state, deadlinePassed: true).Should().Be(state);
        CommitmentMachine.IsDeadlineTrippable(state).Should().BeFalse();
    }

    [Fact]
    public void Deadline_not_yet_passed_changes_nothing()
    {
        CommitmentMachine.ApplyDeadline(Active, deadlinePassed: false).Should().Be(Active);
    }
}
