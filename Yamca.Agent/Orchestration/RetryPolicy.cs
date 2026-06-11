namespace Yamca.Agent.Orchestration;

/// <summary>Exponential-backoff math for failed orchestrator runs (Symphony-style:
/// <c>delay = min(base · 2^(attempt−1), max)</c>, then park after the attempt cap).</summary>
public static class RetryPolicy
{
    /// <summary>Delay before retry number <paramref name="attempt"/> (1-based: the delay
    /// scheduled after the attempt-th failure).</summary>
    public static TimeSpan DelayFor(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        if (attempt < 1) attempt = 1;
        // Cap the exponent so the multiplication can't overflow; 2^30 · any sane base
        // already exceeds any realistic maxDelay.
        var factor = Math.Pow(2, Math.Min(attempt - 1, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(baseDelay.Ticks * factor, maxDelay.Ticks));
        return delay > maxDelay ? maxDelay : delay;
    }

    /// <summary>True when a card that has failed <paramref name="attempts"/> times should be
    /// parked instead of retried. <paramref name="maxAttempts"/> of 0 parks on first failure.</summary>
    public static bool ShouldPark(int attempts, int maxAttempts) => attempts >= maxAttempts;
}
