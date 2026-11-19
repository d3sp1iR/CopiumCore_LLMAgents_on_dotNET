using CopiumCore.Domain;
using Xunit;

namespace CopiumCore.Domain.Tests;

public sealed class RunTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.TimedOut)]
    public void QueuedRunAllowsOnlyDefinedTransitions(RunStatus next)
    {
        var run = CreateRun();
        run.TransitionTo(next);
        Assert.Equal(next, run.Status);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.TimedOut)]
    public void RunningRunCanReachTerminalState(RunStatus next)
    {
        var run = CreateRun();
        run.TransitionTo(RunStatus.Running);
        run.TransitionTo(next);
        Assert.Equal(next, run.Status);
        Assert.Throws<InvalidOperationException>(() => run.TransitionTo(RunStatus.Running));
    }

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    public void QueuedRunRejectsInvalidTransitions(RunStatus next)
    {
        var run = CreateRun();
        Assert.Throws<InvalidOperationException>(() => run.TransitionTo(next));
        Assert.Equal(RunStatus.Queued, run.Status);
    }

    [Fact]
    public void RejectsMissingIdentityAndExpiredDeadline()
    {
        Assert.Throws<ArgumentException>(() => new Run(" ", "a", "1", "u", "s", "t", Now, Now.AddSeconds(90)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Run("r", "a", "1", "u", "s", "t", Now, Now));
    }

    private static Run CreateRun() => new("r", "a", "1", "u", "s", "t", Now, Now.AddSeconds(90));
}
