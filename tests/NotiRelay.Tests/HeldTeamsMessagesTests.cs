using NotiRelay;
using Xunit;

public class HeldTeamsMessagesTests
{
    // Teams monitoring started at launch, after the PC was off for two days.
    private static readonly DateTimeOffset Launch = new(2026, 11, 25, 13, 5, 0, TimeSpan.Zero);

    private static MissedTeamsMessage Missed(string id, DateTimeOffset when, string? file = null) =>
        new(
            new[] { file ?? id + ".json" },
            "c1:" + id,
            new TeamsInboxMessage(id, "c1", "Ana", "s1", "hello " + id, when, "", "message"),
            when);

    private static HeldTeamsMessages Started(params MissedTeamsMessage[] foundAtStart)
    {
        var held = new HeldTeamsMessages();
        held.Start(Launch, foundAtStart);
        return held;
    }

    private static string[] Keys(IEnumerable<MissedTeamsMessage> messages) =>
        messages.Select(missed => missed.Key).ToArray();

    [Fact]
    public void IsFromBefore_OnlyMessagesOlderThanTheStartOfMonitoring()
    {
        HeldTeamsMessages held = Started();

        Assert.True(held.IsFromBefore(Launch.AddDays(-2)));
        Assert.True(held.IsFromBefore(Launch.AddSeconds(-1)));
        Assert.False(held.IsFromBefore(Launch));
        Assert.False(held.IsFromBefore(Launch.AddMinutes(3)));
    }

    [Fact]
    public void IsFromBefore_FalseWhileMonitoringIsNotRunning()
    {
        var neverStarted = new HeldTeamsMessages();
        HeldTeamsMessages stopped = Started();
        stopped.Clear();

        Assert.False(neverStarted.IsFromBefore(Launch.AddDays(-2)));
        Assert.False(stopped.IsFromBefore(Launch.AddDays(-2)));
    }

    [Fact]
    public void Add_HoldsAnOldMessageThatSyncedAfterLaunch_UntilTheUserAnswers()
    {
        HeldTeamsMessages held = Started();

        FromBeforeOutcome outcome = held.Add(Missed("m1", Launch.AddDays(-2)));

        Assert.Equal(FromBeforeOutcome.Held, outcome);
        Assert.Equal(new[] { "c1:m1" }, Keys(held.Undecided()));
        Assert.True(held.ContainsFile("M1.JSON"));
        Assert.Empty(held.Decided().ToSend);
        Assert.Empty(held.Decided().ToSkip);
    }

    [Fact]
    public void Add_MergesASecondFileCarryingTheSameMessage()
    {
        DateTimeOffset when = Launch.AddHours(-5);
        HeldTeamsMessages held = Started(Missed("m1", when, "a.json"));

        FromBeforeOutcome outcome = held.Add(Missed("m1", when, "b.json"));

        Assert.Equal(FromBeforeOutcome.AlreadyHeld, outcome);
        Assert.Single(held.Undecided());
        Assert.True(held.ContainsFile("a.json"));
        Assert.True(held.ContainsFile("b.json"));
    }

    [Fact]
    public void Undecided_IsOldestFirst_AcrossLaunchScanAndLateFiles()
    {
        HeldTeamsMessages held = Started(Missed("m2", Launch.AddHours(-1)));
        held.Add(Missed("m1", Launch.AddDays(-2)));

        Assert.Equal(new[] { "c1:m1", "c1:m2" }, Keys(held.Undecided()));
    }

    [Fact]
    public void ResolveNo_SkipsEverythingHeld_EvenFilesHeldAfterTheQuestion()
    {
        HeldTeamsMessages held = Started(Missed("m1", Launch.AddDays(-2)));
        held.Add(Missed("m2", Launch.AddDays(-1)));

        held.Resolve(send: false, askedKeys: new[] { "c1:m1" });

        Assert.Empty(held.Undecided());
        Assert.Empty(held.Decided().ToSend);
        Assert.Equal(new[] { "c1:m1", "c1:m2" }, Keys(held.Decided().ToSkip));
    }

    [Fact]
    public void ResolveNo_SkipsOldMessagesThatArriveLater_WithoutAskingAgain()
    {
        HeldTeamsMessages held = Started();
        held.Resolve(send: false, askedKeys: Array.Empty<string>());

        FromBeforeOutcome outcome = held.Add(Missed("m3", Launch.AddDays(-1)));

        Assert.Equal(FromBeforeOutcome.Skip, outcome);
        Assert.Empty(held.Undecided());
    }

    [Fact]
    public void ResolveYes_SendsOnlyTheMessagesTheUserWasToldAbout()
    {
        HeldTeamsMessages held = Started(Missed("m1", Launch.AddDays(-2)));
        held.Add(Missed("m2", Launch.AddDays(-1)));

        held.Resolve(send: true, askedKeys: new[] { "c1:m1" });

        Assert.Equal(new[] { "c1:m1" }, Keys(held.Decided().ToSend));
        Assert.Equal(new[] { "c1:m2" }, Keys(held.Undecided()));
        Assert.Equal(FromBeforeOutcome.Held, held.Add(Missed("m3", Launch.AddHours(-2))));
    }

    [Fact]
    public void Decided_SendsOldestFirst()
    {
        HeldTeamsMessages held = Started(
            Missed("m2", Launch.AddHours(-1)),
            Missed("m1", Launch.AddDays(-2)));

        held.Resolve(send: true, askedKeys: new[] { "c1:m2", "c1:m1" });

        Assert.Equal(new[] { "c1:m1", "c1:m2" }, Keys(held.Decided().ToSend));
    }

    [Fact]
    public void Remove_ForgetsAppliedMessagesAndReleasesTheirFiles()
    {
        HeldTeamsMessages held = Started(Missed("m1", Launch.AddDays(-2)));
        held.Resolve(send: false, askedKeys: new[] { "c1:m1" });

        held.Remove(new[] { "c1:m1" });

        Assert.Empty(held.Decided().ToSkip);
        Assert.False(held.ContainsFile("m1.json"));
    }

    [Fact]
    public void Clear_DropsUnansweredMessages_SoTheyAreOfferedAgainNextStart()
    {
        HeldTeamsMessages held = Started(Missed("m1", Launch.AddDays(-2)));

        held.Clear();

        Assert.Empty(held.Undecided());
        Assert.False(held.ContainsFile("m1.json"));
    }

    [Fact]
    public void Start_ForgetsAnEarlierNo()
    {
        HeldTeamsMessages held = Started();
        held.Resolve(send: false, askedKeys: Array.Empty<string>());

        held.Start(Launch.AddHours(1), Array.Empty<MissedTeamsMessage>());

        Assert.Equal(FromBeforeOutcome.Held, held.Add(Missed("m1", Launch)));
    }

    [Fact]
    public void MoveCutoff_AfterSleep_HoldsMessagesFromTheSleep_AndAsksAgain()
    {
        HeldTeamsMessages held = Started();
        held.Resolve(send: false, askedKeys: Array.Empty<string>());
        DateTimeOffset woke = Launch.AddHours(8);

        held.MoveCutoff(woke);

        Assert.True(held.IsFromBefore(woke.AddMinutes(-1)));
        Assert.Equal(FromBeforeOutcome.Held, held.Add(Missed("m1", Launch.AddHours(3))));
    }

    [Fact]
    public void MoveCutoff_NeverMovesBack()
    {
        HeldTeamsMessages held = Started();

        held.MoveCutoff(Launch.AddHours(-1));

        Assert.True(held.IsFromBefore(Launch.AddMinutes(-1)));
    }

    [Fact]
    public void MoveCutoff_IgnoredWhileMonitoringIsNotRunning()
    {
        var stopped = new HeldTeamsMessages();

        stopped.MoveCutoff(Launch);

        Assert.False(stopped.IsFromBefore(Launch.AddDays(-1)));
    }
}
