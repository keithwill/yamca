namespace Yamca.Agent.Metrics;

/// <summary>Retention policy for the dedicated metrics store: keep at most
/// <see cref="MaxSamples"/> records, and drop any older than <see cref="MaxAge"/>.
/// <see cref="MaxAge"/> is <c>null</c> when age-based pruning is disabled (samples are kept
/// regardless of age, subject only to the count cap). A user-tier setting, so it is read from the
/// shared user-settings blob rather than carried per session — see
/// <c>SessionSettings.ReadMetricsRetention</c>.</summary>
public sealed record MetricsRetention(int MaxSamples, TimeSpan? MaxAge);
