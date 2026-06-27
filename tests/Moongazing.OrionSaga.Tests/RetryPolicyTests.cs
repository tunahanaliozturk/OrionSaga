namespace Moongazing.OrionSaga.Tests;

using Moongazing.OrionSaga.Orchestration;

using Xunit;

public sealed class RetryPolicyTests
{
    [Fact]
    public void Max_attempts_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(-3, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_negative_base_delay_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(3, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Zero_base_delay_is_allowed_and_retries_with_no_wait()
    {
        var policy = new RetryPolicy(3, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, policy.DelayBeforeAttempt(2));
        Assert.Equal(TimeSpan.Zero, policy.DelayBeforeAttempt(3));
    }

    [Fact]
    public void The_first_attempt_is_never_preceded_by_a_wait()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(2), RetryBackoff.Exponential);

        Assert.Equal(TimeSpan.Zero, policy.DelayBeforeAttempt(1));
        Assert.Equal(TimeSpan.Zero, policy.DelayBeforeAttempt(0));
    }

    [Fact]
    public void Constant_backoff_waits_the_base_delay_before_every_retry()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(50));

        Assert.Equal(TimeSpan.FromMilliseconds(50), policy.DelayBeforeAttempt(2));
        Assert.Equal(TimeSpan.FromMilliseconds(50), policy.DelayBeforeAttempt(3));
        Assert.Equal(TimeSpan.FromMilliseconds(50), policy.DelayBeforeAttempt(4));
    }

    [Fact]
    public void Exponential_backoff_doubles_the_wait_each_retry()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(10), RetryBackoff.Exponential);

        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.DelayBeforeAttempt(2)); // first retry: base
        Assert.Equal(TimeSpan.FromMilliseconds(20), policy.DelayBeforeAttempt(3)); // base * 2
        Assert.Equal(TimeSpan.FromMilliseconds(40), policy.DelayBeforeAttempt(4)); // base * 4
        Assert.Equal(TimeSpan.FromMilliseconds(80), policy.DelayBeforeAttempt(5)); // base * 8
    }

    [Fact]
    public void Exponential_backoff_clamps_rather_than_overflowing()
    {
        var policy = new RetryPolicy(int.MaxValue, TimeSpan.FromHours(1), RetryBackoff.Exponential);

        // A large retry index must not wrap into a negative or nonsensical delay.
        Assert.Equal(TimeSpan.MaxValue, policy.DelayBeforeAttempt(80));
    }

    [Fact]
    public void A_policy_remembers_its_settings()
    {
        var policy = new RetryPolicy(4, TimeSpan.FromSeconds(3), RetryBackoff.Exponential);

        Assert.Equal(4, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), policy.BaseDelay);
        Assert.Equal(RetryBackoff.Exponential, policy.Backoff);
    }
}
