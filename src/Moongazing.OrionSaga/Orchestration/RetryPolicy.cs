namespace Moongazing.OrionSaga.Orchestration;

/// <summary>
/// How the delay between retry attempts grows. <see cref="Constant"/> waits the same base delay before
/// every retry; <see cref="Exponential"/> doubles the wait each attempt (base, base*2, base*4, ...).
/// </summary>
public enum RetryBackoff
{
    /// <summary>The same base delay before every retry.</summary>
    Constant = 0,

    /// <summary>The base delay doubled each successive retry: base, base*2, base*4, and so on.</summary>
    Exponential = 1,
}

/// <summary>
/// An opt-in retry-with-backoff policy applied around an action that may transiently fault. It bounds
/// how many total attempts are made and how long to wait between them. A policy is a small immutable
/// value with no behaviour of its own: the executor reads <see cref="MaxAttempts"/> and
/// <see cref="DelayBeforeAttempt"/> to decide whether and when to retry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxAttempts"/> counts the first try plus retries, so a policy with three attempts runs the
/// action up to three times: the initial call and two retries. A retry is only made after a transient
/// fault; a successful attempt stops immediately. Cancellation (the caller's token or a step timeout) is
/// never retried, since it is not a transient fault.
/// </para>
/// <para>
/// The delay before a retry is computed from <see cref="BaseDelay"/> and <see cref="Backoff"/> and is
/// honoured cooperatively against the active <see cref="CancellationToken"/>, so a cancelled token cuts
/// the wait short rather than blocking for the full backoff.
/// </para>
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>Create a retry policy.</summary>
    /// <param name="maxAttempts">
    /// The maximum number of attempts, counting the first try plus retries. Must be at least 1; 1 means
    /// no retry (a single attempt), which is equivalent to declaring no policy at all.
    /// </param>
    /// <param name="baseDelay">
    /// The delay before the first retry, and the unit the backoff grows from. Must be zero or positive;
    /// zero retries immediately with no wait.
    /// </param>
    /// <param name="backoff">How the delay grows between successive retries. Defaults to constant.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> is less than 1, or <paramref name="baseDelay"/> is negative.
    /// </exception>
    public RetryPolicy(int maxAttempts, TimeSpan baseDelay, RetryBackoff backoff = RetryBackoff.Constant)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), maxAttempts, "Max attempts must be at least 1.");
        }

        if (baseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseDelay), baseDelay, "Base delay must be zero or positive.");
        }

        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        Backoff = backoff;
    }

    /// <summary>The maximum number of attempts, counting the first try plus retries (at least 1).</summary>
    public int MaxAttempts { get; }

    /// <summary>The delay before the first retry and the unit the backoff grows from.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>How the delay grows between successive retries.</summary>
    public RetryBackoff Backoff { get; }

    /// <summary>
    /// The delay to wait before the given attempt. <paramref name="attempt"/> is one-based: attempt 1 is
    /// the first try and is never waited for (returns <see cref="TimeSpan.Zero"/>); attempt 2 is the
    /// first retry and waits <see cref="BaseDelay"/>; later attempts grow per <see cref="Backoff"/>.
    /// </summary>
    /// <param name="attempt">The one-based attempt number the delay precedes.</param>
    /// <returns>The delay to wait before that attempt; zero for the first attempt.</returns>
    public TimeSpan DelayBeforeAttempt(int attempt)
    {
        // The first attempt is the initial call: it is never preceded by a backoff wait. Retries start
        // at attempt 2, whose preceding wait is one BaseDelay; attempt 3 is two retries in, and so on.
        if (attempt <= 1)
        {
            return TimeSpan.Zero;
        }

        var retryIndex = attempt - 2; // 0 for the first retry, 1 for the second, ...
        if (Backoff == RetryBackoff.Constant || retryIndex == 0)
        {
            return BaseDelay;
        }

        // Exponential: BaseDelay * 2^retryIndex. Computed on ticks to avoid floating point, and clamped
        // to TimeSpan.MaxValue so a large retryIndex cannot overflow into a negative or wrapped delay.
        var baseTicks = BaseDelay.Ticks;
        if (baseTicks == 0)
        {
            return TimeSpan.Zero;
        }

        var maxShift = TimeSpan.MaxValue.Ticks / baseTicks;
        if (retryIndex >= 62 || (1L << retryIndex) > maxShift)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks(baseTicks << retryIndex);
    }
}
