namespace Yamca.Web.Services.Orchestration;

/// <summary>Process-start options for the orchestrator. <see cref="StartEnabled"/> is the seam
/// for a future <c>--orchestrate</c> CLI flag: Program.cs registers <c>new(StartEnabled: false)</c>
/// today, and the flag only has to change that literal. The orchestrator's enabled state is
/// otherwise runtime-only — it is never persisted, so yamca always starts with orchestration
/// off unless explicitly asked.</summary>
public sealed record OrchestratorStartupOptions(bool StartEnabled);
