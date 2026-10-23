using Microsoft.Extensions.Time.Testing;
using SprintScore.Voting;

namespace SprintScore.Tests;

public class VotingSessionTests
{
    private readonly FakeTimeProvider _time = new();

    private VotingSession NewSession() => new(_time);

    /// <summary>Подключает участника с одной вкладкой и сразу отдаёт его id.</summary>
    private static string Join(VotingSession session, string name, bool isAdmin = false)
    {
        session.Connect(name, name + "-conn", isAdmin);
        return name;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-3)]
    public void Vote_OutsideScale_IsRejected(int score)
    {
        var session = NewSession();
        var alice = Join(session, "alice");

        Assert.False(session.Vote(alice, score));
        Assert.Equal(0, session.Snapshot().VotedCount);
    }

    [Fact]
    public void Vote_Twice_OverwritesWithoutDoubleCounting()
    {
        var session = NewSession();
        var alice = Join(session, "alice");

        session.Vote(alice, 3);
        session.Vote(alice, 9);
        session.Reveal();

        var state = session.Snapshot();
        Assert.Equal(1, state.VotedCount);
        Assert.Equal(9d, state.Average);
        Assert.Equal(1, state.Distribution![8]);
        Assert.Equal(0, state.Distribution[2]);
    }

    [Fact]
    public void RetractVote_ReleasesTheVote()
    {
        var session = NewSession();
        var alice = Join(session, "alice");

        session.Vote(alice, 7);
        Assert.Equal(1, session.Snapshot().VotedCount);

        Assert.True(session.RetractVote(alice));
        Assert.Equal(0, session.Snapshot().VotedCount);
    }

    [Fact]
    public void AfterReveal_VoteAndRetractAreIgnored()
    {
        var session = NewSession();
        var alice = Join(session, "alice");
        var bob = Join(session, "bob");

        session.Vote(alice, 5);
        session.Reveal();

        Assert.False(session.Vote(bob, 10));
        Assert.False(session.Vote(alice, 1));
        Assert.False(session.RetractVote(alice));

        var state = session.Snapshot();
        Assert.Equal(1, state.VotedCount);
        Assert.Equal(5d, state.Average);
    }

    [Fact]
    public void TwoTabsOfSameParticipant_CountAsOne()
    {
        var session = NewSession();
        session.Connect("alice", "tab-1", isAdmin: false);
        session.Connect("alice", "tab-2", isAdmin: false);

        Assert.Equal(1, session.Snapshot().OnlineCount);

        session.Disconnect("tab-1");
        Assert.Equal(1, session.Snapshot().OnlineCount);
    }

    [Fact]
    public void Reconnect_RestoresOwnVote()
    {
        var session = NewSession();
        session.Connect("alice", "tab-1", isAdmin: false);
        session.Vote("alice", 6);
        session.Disconnect("tab-1");

        var self = session.Connect("alice", "tab-2", isAdmin: false);

        Assert.Equal(6, self.MyVote);
        Assert.Equal(1, session.Snapshot().VotedCount);
    }

    [Fact]
    public void Admin_IsNotCountedAsParticipant()
    {
        var session = NewSession();
        Join(session, "alice");
        Join(session, "host", isAdmin: true);

        Assert.Equal(1, session.Snapshot().OnlineCount);
    }

    [Fact]
    public void ResultsAreHiddenUntilReveal()
    {
        var session = NewSession();
        var alice = Join(session, "alice");
        session.Vote(alice, 8);

        var before = session.Snapshot();
        Assert.False(before.Revealed);
        Assert.Null(before.Average);
        Assert.Null(before.Distribution);

        session.Reveal();

        var after = session.Snapshot();
        Assert.True(after.Revealed);
        Assert.Equal(8d, after.Average);
        Assert.NotNull(after.Distribution);
    }

    [Fact]
    public void Average_MatchesTheReferenceScreenshot()
    {
        // Восемь голосов со средним 8.25, как на образце: 7, 8×4, 9×3.
        var session = NewSession();
        var scores = new[] { 7, 8, 8, 8, 8, 9, 9, 9 };

        for (var i = 0; i < scores.Length; i++)
        {
            var participant = Join(session, "p" + i);
            session.Vote(participant, scores[i]);
        }

        session.Reveal();
        var state = session.Snapshot();

        Assert.Equal(8, state.VotedCount);
        Assert.Equal(8.25d, state.Average);
        Assert.Equal(1, state.Distribution![6]);  // оценка 7
        Assert.Equal(4, state.Distribution[7]);   // оценка 8
        Assert.Equal(3, state.Distribution[8]);   // оценка 9
        Assert.Equal(0, state.Distribution[9]);   // оценка 10
    }

    [Fact]
    public void Average_IsRoundedToTwoDecimals()
    {
        // 8 + 9 + 9 = 26 / 3 = 8.666… → 8.67
        var session = NewSession();
        var scores = new[] { 8, 9, 9 };

        for (var i = 0; i < scores.Length; i++)
        {
            session.Vote(Join(session, "p" + i), scores[i]);
        }

        session.Reveal();
        Assert.Equal(8.67d, session.Snapshot().Average);
    }

    [Fact]
    public void Average_IsNullWhenNobodyVoted()
    {
        var session = NewSession();
        Join(session, "alice");
        session.Reveal();

        var state = session.Snapshot();
        Assert.Null(state.Average);
        Assert.Equal(new int[10], state.Distribution);
    }

    [Fact]
    public void Restart_ClearsVotesAndOpensNewRound()
    {
        var session = NewSession();
        var alice = Join(session, "alice");
        session.Vote(alice, 4);
        session.Reveal();

        var round = session.Snapshot().Round;
        session.Restart();

        var state = session.Snapshot();
        Assert.False(state.Revealed);
        Assert.Equal(0, state.VotedCount);
        Assert.Null(state.Average);
        Assert.Equal(round + 1, state.Round);
        Assert.True(session.Vote(alice, 4));
    }

    [Fact]
    public void Departure_IsIgnoredUntilGraceExpires()
    {
        var session = NewSession();
        var alice = Join(session, "alice");
        session.Vote(alice, 5);
        session.Disconnect("alice-conn");

        // Пока идёт отсрочка, ушедший всё ещё считается присутствующим:
        // перезагрузка страницы не должна дёргать счётчики.
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(session.SweepDepartures(TimeSpan.FromSeconds(20)));

        var state = session.Snapshot();
        Assert.Equal(1, state.OnlineCount);
        Assert.Equal(1, state.VotedCount);
    }

    [Fact]
    public void Departure_RemovesParticipantAndVoteAfterGrace()
    {
        var session = NewSession();
        var alice = Join(session, "alice");
        Join(session, "bob");
        session.Vote(alice, 5);
        session.Disconnect("alice-conn");

        _time.Advance(TimeSpan.FromSeconds(21));
        Assert.True(session.SweepDepartures(TimeSpan.FromSeconds(20)));

        var state = session.Snapshot();
        Assert.Equal(1, state.OnlineCount);
        Assert.Equal(0, state.VotedCount);
    }

    [Fact]
    public void Reconnect_WithinGrace_KeepsParticipant()
    {
        var session = NewSession();
        var alice = Join(session, "alice");
        session.Vote(alice, 5);
        session.Disconnect("alice-conn");

        _time.Advance(TimeSpan.FromSeconds(5));
        session.Connect(alice, "alice-conn-2", isAdmin: false);
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.False(session.SweepDepartures(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, session.Snapshot().VotedCount);
    }

    [Fact]
    public void Sweep_DoesNotTouchRevealedResults()
    {
        var session = NewSession();
        var alice = Join(session, "alice");
        session.Vote(alice, 9);
        session.Reveal();
        session.Disconnect("alice-conn");

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(session.SweepDepartures(TimeSpan.FromSeconds(20)));

        var state = session.Snapshot();
        Assert.Equal(1, state.VotedCount);
        Assert.Equal(9d, state.Average);
    }
}
