using System.Text.Json.Serialization;
using Yamca.Agent.Metrics;

namespace Yamca.Agent.Storage;

/// <summary>The source-generated <see cref="JsonSerializerContext"/> for the dedicated metrics
/// store (<c>.yamca/metrics.db</c>). Kept separate from <see cref="YamcaStoreJsonContext"/> because
/// the metrics file is a distinct VestPocket store with its own, disposable lifecycle — see
/// <see cref="MetricsStore"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TurnMetric))]
internal partial class MetricsStoreJsonContext : JsonSerializerContext;
