namespace Yamca.Agent.Metrics;

/// <summary>Receives a <see cref="TurnMetric"/> for each completed model
/// round-trip. The Agent library defines the seam; the Web layer supplies an
/// implementation that persists to the metrics store (mirroring the optional
/// <c>SessionDiagnosticsLog</c> pattern). <see cref="AgentLoop"/> holds it as an
/// optional dependency — a null sink means throughput recording is disabled.
///
/// Implementations must be fire-and-forget and must never throw: a metrics
/// failure can never be allowed to break an in-flight chat turn.</summary>
public interface ITurnMetricSink
{
    void Record(TurnMetric metric);
}
