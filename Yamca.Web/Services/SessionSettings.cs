using System.Text.Json;
using System.Text.Json.Serialization;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Web.Services;

/// <summary>
/// Concrete <see cref="ISessionSettings"/> implementation backing one Blazor circuit.
/// Mutations raise <see cref="Changed"/> so the host can persist the affected tier to
/// disk (user tier via <c>UserSettingsStore</c>, project tier via <c>ProjectSettingsStore</c>).
/// </summary>
public sealed class SessionSettings : ISessionSettings
{
    private const string DefaultSystemPrompt =
        "You are a coding assistant.";

    // Defaults and clamp ranges for the scalar user-tier settings. Hoisted here so the
    // property initializers, the setters' clamps, ApplyBlob (hydrate/import), and
    // ResetUserToDefaults all agree on one source of truth.
    private const bool DefaultMarkdownEnabled = true;
    private const ReasoningDisplay DefaultReasoningDisplay = ReasoningDisplay.Collapsed;
    private const PromptDockPosition DefaultPromptDockPosition = PromptDockPosition.Top;
    private const bool DefaultAutoCompactionEnabled = false;
    private const int DefaultAutoCompactionThresholdPercent = 75;
    private const int MinAutoCompactionThresholdPercent = 1;
    private const int MaxAutoCompactionThresholdPercent = 95;
    private const int DefaultAutoCompactionKeepRecentTurns = 4;
    private const int MinAutoCompactionKeepRecentTurns = 1;
    private const int MaxAutoCompactionKeepRecentTurns = 50;
    private const int MinMaxToolIterations = 1;
    private const int MaxMaxToolIterations = 100;
    private const DeferredToolsHint DefaultDeferredToolsHint = DeferredToolsHint.Names;
    private const ShellPreference DefaultShellPreference = ShellPreference.Auto;

    // Clamp ranges for the orchestrator's numeric settings, applied both when the UI
    // mutates them (SetOrchestrator) and when a blob is hydrated off disk.
    private const int MinOrchestratorConcurrency = 1;
    private const int MaxOrchestratorConcurrency = 16;
    private const int MinOrchestratorTurns = 1;
    private const int MaxOrchestratorTurns = 20;
    private const int MinOrchestratorTimeoutSeconds = 10;
    private const int MaxOrchestratorTimeoutSeconds = 7200;
    private const int MinOrchestratorRetryAttempts = 0;
    private const int MaxOrchestratorRetryAttempts = 10;
    private const int MinOrchestratorRetryDelaySeconds = 1;
    private const int MaxOrchestratorRetryDelaySeconds = 7200;
    private const int MinOrchestratorPollSeconds = 2;
    private const int MaxOrchestratorPollSeconds = 300;

    public static readonly IReadOnlyList<string> DefaultUserInstructionFiles =
        new[] { "AGENTS.md", "CLAUDE.md", "GEMINI.md" };

    public ToolSettingsMap Project { get; private set; } = ToolSettingsMap.Empty;
    public ToolSettingsMap User { get; private set; } = ToolSettingsMap.Empty;
    public EndpointsSettings Endpoints { get; private set; } = EndpointsSettings.CreateDefault();
    public string SystemPrompt { get; private set; } = DefaultSystemPrompt;
    public bool MarkdownEnabled { get; private set; } = DefaultMarkdownEnabled;
    public ReasoningDisplay ReasoningDisplay { get; private set; } = DefaultReasoningDisplay;
    public PromptDockPosition PromptDockPosition { get; private set; } = DefaultPromptDockPosition;

    public bool AutoCompactionEnabled { get; private set; } = DefaultAutoCompactionEnabled;
    public int AutoCompactionThresholdPercent { get; private set; } = DefaultAutoCompactionThresholdPercent;
    public int AutoCompactionKeepRecentTurns { get; private set; } = DefaultAutoCompactionKeepRecentTurns;

    public int MaxToolIterations { get; private set; } = AgentLoopOptions.Default.MaxIterations;

    public DeferredToolsHint DeferredToolsHint { get; private set; } = DefaultDeferredToolsHint;

    public ShellPreference ShellPreference { get; private set; } = DefaultShellPreference;

    public IReadOnlyList<string> UserInstructionFiles { get; private set; } = DefaultUserInstructionFiles;
    public IReadOnlyList<string> ProjectInstructionFiles { get; private set; } = Array.Empty<string>();
    public bool ProjectInheritsUserInstructions { get; private set; } = true;

    public ScriptRegistry UserScripts { get; private set; } = ScriptRegistry.Empty;
    public ScriptRegistry ProjectScripts { get; private set; } = ScriptRegistry.Empty;

    public SubagentRegistry UserSubagents { get; private set; } = SubagentRegistry.Empty;
    public SubagentRegistry ProjectSubagents { get; private set; } = SubagentRegistry.Empty;

    public OrchestratorSettings Orchestrator { get; private set; } = OrchestratorSettings.Default;

    /// <summary>Fired when the named tier has been mutated. The handler is expected
    /// to serialize that tier and write it to disk.</summary>
    public event Action<SettingsTier>? Changed;

    // --- mutation API (used by Settings page + IPermissionStore adapter) -----------

    /// <summary>Append a new endpoint. The first endpoint added to an empty list
    /// becomes the default automatically.</summary>
    public void AddEndpoint(EndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var items = Endpoints.Items.Append(endpoint).ToList();
        var defaultId = Endpoints.Items.Count == 0 ? endpoint.Id : Endpoints.DefaultId;
        Endpoints = new EndpointsSettings(items, defaultId);
        Changed?.Invoke(SettingsTier.User);
    }

    /// <summary>Replace the endpoint with the matching <see cref="EndpointSettings.Id"/>.
    /// Does nothing if no entry matches.</summary>
    public void UpdateEndpoint(EndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var idx = -1;
        for (var i = 0; i < Endpoints.Items.Count; i++)
        {
            if (Endpoints.Items[i].Id == endpoint.Id) { idx = i; break; }
        }
        if (idx < 0) return;
        var items = Endpoints.Items.ToList();
        items[idx] = endpoint;
        Endpoints = Endpoints with { Items = items };
        Changed?.Invoke(SettingsTier.User);
    }

    /// <summary>Remove the endpoint with the given id. Refuses to remove the last
    /// remaining endpoint. If the default is removed, the first remaining entry
    /// becomes the new default.</summary>
    public void RemoveEndpoint(Guid id)
    {
        if (Endpoints.Items.Count <= 1) return;
        var items = Endpoints.Items.Where(e => e.Id != id).ToList();
        if (items.Count == Endpoints.Items.Count) return; // nothing matched
        var defaultId = Endpoints.DefaultId == id ? items[0].Id : Endpoints.DefaultId;
        Endpoints = new EndpointsSettings(items, defaultId);
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetDefaultEndpoint(Guid id)
    {
        if (Endpoints.DefaultId == id) return;
        if (Endpoints.FindById(id) is null) return;
        Endpoints = Endpoints with { DefaultId = id };
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetSystemPrompt(string systemPrompt)
    {
        SystemPrompt = systemPrompt ?? string.Empty;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetMarkdownEnabled(bool enabled)
    {
        if (MarkdownEnabled == enabled) return;
        MarkdownEnabled = enabled;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetReasoningDisplay(ReasoningDisplay display)
    {
        if (ReasoningDisplay == display) return;
        ReasoningDisplay = display;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetPromptDockPosition(PromptDockPosition position)
    {
        if (PromptDockPosition == position) return;
        PromptDockPosition = position;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetAutoCompactionEnabled(bool enabled)
    {
        if (AutoCompactionEnabled == enabled) return;
        AutoCompactionEnabled = enabled;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetAutoCompactionThresholdPercent(int percent)
    {
        var clamped = Math.Clamp(percent, MinAutoCompactionThresholdPercent, MaxAutoCompactionThresholdPercent);
        if (AutoCompactionThresholdPercent == clamped) return;
        AutoCompactionThresholdPercent = clamped;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetAutoCompactionKeepRecentTurns(int turns)
    {
        var clamped = Math.Clamp(turns, MinAutoCompactionKeepRecentTurns, MaxAutoCompactionKeepRecentTurns);
        if (AutoCompactionKeepRecentTurns == clamped) return;
        AutoCompactionKeepRecentTurns = clamped;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetMaxToolIterations(int iterations)
    {
        var clamped = Math.Clamp(iterations, MinMaxToolIterations, MaxMaxToolIterations);
        if (MaxToolIterations == clamped) return;
        MaxToolIterations = clamped;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetDeferredToolsHint(DeferredToolsHint hint)
    {
        if (DeferredToolsHint == hint) return;
        DeferredToolsHint = hint;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetShellPreference(ShellPreference preference)
    {
        if (ShellPreference == preference) return;
        ShellPreference = preference;
        Changed?.Invoke(SettingsTier.User);
    }

    public void SetInstructionFiles(SettingsTier tier, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths.Select(p => p?.Trim() ?? string.Empty).ToArray();

        if (tier == SettingsTier.Project) ProjectInstructionFiles = normalized;
        else UserInstructionFiles = normalized;

        Changed?.Invoke(tier);
    }

    public void SetProjectInheritsUserInstructions(bool inherits)
    {
        if (ProjectInheritsUserInstructions == inherits) return;
        ProjectInheritsUserInstructions = inherits;
        Changed?.Invoke(SettingsTier.Project);
    }

    public void SetScripts(SettingsTier tier, ScriptRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (tier == SettingsTier.Project) ProjectScripts = registry;
        else UserScripts = registry;
        Changed?.Invoke(tier);
    }

    /// <summary>Append a single script entry to the named tier. Existing entries with
    /// the same path are replaced. Used by the approval prompt's "Allow and register"
    /// action.</summary>
    public void AddRegisteredScript(SettingsTier tier, RegisteredScript entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var current = tier == SettingsTier.Project ? ProjectScripts : UserScripts;
        var registered = current.Registered
            .Where(e => !string.Equals(e.Path, entry.Path, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        SetScripts(tier, new ScriptRegistry(registered, current.Directories, current.Inline));
    }

    public void SetSubagents(SettingsTier tier, SubagentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (tier == SettingsTier.Project) ProjectSubagents = registry;
        else UserSubagents = registry;
        Changed?.Invoke(tier);
    }

    /// <summary>Replace the orchestrator configuration (project tier). Numeric fields are
    /// clamped to the same ranges hydration applies, so the live value never depends on
    /// whether it arrived from the UI or from disk.</summary>
    public void SetOrchestrator(OrchestratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Orchestrator = ClampOrchestrator(settings);
        Changed?.Invoke(SettingsTier.Project);
    }

    private static OrchestratorSettings ClampOrchestrator(OrchestratorSettings s) => s with
    {
        EnabledColumns = s.EnabledColumns
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        MaxConcurrentRuns = Math.Clamp(s.MaxConcurrentRuns, MinOrchestratorConcurrency, MaxOrchestratorConcurrency),
        MaxConcurrentRunsPerColumn = s.MaxConcurrentRunsPerColumn is int pc
            ? Math.Clamp(pc, MinOrchestratorConcurrency, MaxOrchestratorConcurrency)
            : null,
        MaxTurnsPerRun = Math.Clamp(s.MaxTurnsPerRun, MinOrchestratorTurns, MaxOrchestratorTurns),
        MaxToolIterationsPerTurn = s.MaxToolIterationsPerTurn is int ti
            ? Math.Clamp(ti, MinMaxToolIterations, MaxMaxToolIterations)
            : null,
        StallTimeoutSeconds = Math.Clamp(s.StallTimeoutSeconds, MinOrchestratorTimeoutSeconds, MaxOrchestratorTimeoutSeconds),
        TurnTimeoutSeconds = Math.Clamp(s.TurnTimeoutSeconds, MinOrchestratorTimeoutSeconds, MaxOrchestratorTimeoutSeconds),
        RetryMaxAttempts = Math.Clamp(s.RetryMaxAttempts, MinOrchestratorRetryAttempts, MaxOrchestratorRetryAttempts),
        RetryBaseDelaySeconds = Math.Clamp(s.RetryBaseDelaySeconds, MinOrchestratorRetryDelaySeconds, MaxOrchestratorRetryDelaySeconds),
        RetryMaxDelaySeconds = Math.Clamp(s.RetryMaxDelaySeconds, MinOrchestratorRetryDelaySeconds, MaxOrchestratorRetryDelaySeconds),
        PollIntervalSeconds = Math.Clamp(s.PollIntervalSeconds, MinOrchestratorPollSeconds, MaxOrchestratorPollSeconds),
        AllowedTools = s.AllowedTools
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList(),
    };

    /// <summary>Reset every user-tier setting to the values it would be seeded with on
    /// first run, while leaving the configured endpoints untouched. Mirrors the
    /// <c>firstRun</c> branch of <see cref="ApplyBlob"/>. The curated seed is then
    /// expanded to a full, explicit map via <see cref="MaterializeUserToolDefaults"/> so
    /// the User tier never carries an "inherit" (unset) per-tool value.</summary>
    public void ResetUserToDefaults(IEnumerable<ITool> settingsTools)
    {
        ArgumentNullException.ThrowIfNull(settingsTools);

        SystemPrompt = DefaultSystemPrompt;
        MarkdownEnabled = DefaultMarkdownEnabled;
        ReasoningDisplay = DefaultReasoningDisplay;
        PromptDockPosition = DefaultPromptDockPosition;
        AutoCompactionEnabled = DefaultAutoCompactionEnabled;
        AutoCompactionThresholdPercent = DefaultAutoCompactionThresholdPercent;
        AutoCompactionKeepRecentTurns = DefaultAutoCompactionKeepRecentTurns;
        MaxToolIterations = AgentLoopOptions.Default.MaxIterations;
        DeferredToolsHint = DefaultDeferredToolsHint;
        ShellPreference = DefaultShellPreference;
        User = DefaultUserToolSettings();
        MaterializeUserToolDefaults(settingsTools);
        UserInstructionFiles = DefaultUserInstructionFiles;
        UserScripts = ScriptRegistry.Empty;
        UserSubagents = DefaultUserSubagents();
        Changed?.Invoke(SettingsTier.User);
    }

    /// <summary>Fill in any unset User-tier per-tool field with the owning tool's built-in
    /// default, so the User tier carries an explicit value for every settings tool. With this
    /// done there is no "inherit" left at the User tier — the permission/availability resolvers
    /// resolve as Project-over-User, and the tool default is only ever a defensive last resort.
    /// Returns true if the map changed, so the caller can persist the normalized result.</summary>
    /// <remarks>Materializing makes a User-tier value "sticky": once written it no longer tracks
    /// future changes to a tool's shipped default. The Backup page's "Reset Defaults" re-seeds.
    /// Existing explicit values (including the curated first-run seed) are never overwritten.</remarks>
    public bool MaterializeUserToolDefaults(IEnumerable<ITool> settingsTools)
    {
        ArgumentNullException.ThrowIfNull(settingsTools);

        var next = new Dictionary<string, ToolPermissionSettings>(User.Entries, StringComparer.Ordinal);
        var changed = false;

        foreach (var tool in settingsTools)
        {
            next.TryGetValue(tool.Name, out var current);

            var filled = new ToolPermissionSettings
            {
                Permission = current?.Permission ?? tool.DefaultPermission,
                Availability = current?.Availability ?? tool.DefaultAvailability,
                // Only tools that support workspace restriction expose the control; the rest
                // render as "n/a", so leave their flag unset rather than inventing a value.
                RestrictToWorkspace = tool.SupportsWorkspaceRestriction
                    ? current?.RestrictToWorkspace ?? true
                    : current?.RestrictToWorkspace,
            };

            if (current is null || current != filled)
            {
                next[tool.Name] = filled;
                changed = true;
            }
        }

        if (!changed) return false;
        User = new ToolSettingsMap(next);
        return true;
    }

    /// <summary>Replace a single tool entry in the given tier, or pass <c>null</c> to remove.</summary>
    public void SetToolEntry(SettingsTier tier, string toolName, ToolPermissionSettings? entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);

        var source = tier == SettingsTier.Project ? Project : User;
        var next = new Dictionary<string, ToolPermissionSettings>(source.Entries, StringComparer.Ordinal);

        if (entry is null)
            next.Remove(toolName);
        else
            next[toolName] = entry;

        var map = new ToolSettingsMap(next);
        if (tier == SettingsTier.Project) Project = map;
        else User = map;

        Changed?.Invoke(tier);
    }

    // --- (de)serialization for the persisted settings blob --------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Hydrate the user tier from a JSON blob read off disk.
    /// Missing fields fall back to defaults. Silently ignores malformed input.</summary>
    public void HydrateUser(string? json)
    {
        var firstRun = string.IsNullOrWhiteSpace(json);
        var blob = TryDeserialize<UserBlob>(json) ?? new UserBlob();
        ApplyBlob(blob, firstRun);
    }

    /// <summary>Apply a deserialized user blob to every user-tier field. Shared by
    /// <see cref="HydrateUser"/> (load from disk) and <see cref="ImportUser"/> (manual import).
    /// On <paramref name="firstRun"/> — i.e. no blob has ever been written — the tools,
    /// instruction files, and subagents are seeded with the curated defaults instead of the
    /// (empty) blob values; an import is never a first run.</summary>
    private void ApplyBlob(UserBlob blob, bool firstRun)
    {
        Endpoints = EndpointsFromBlob(blob);

        SystemPrompt = blob.SystemPrompt ?? DefaultSystemPrompt;
        MarkdownEnabled = blob.MarkdownEnabled ?? DefaultMarkdownEnabled;
        ReasoningDisplay = blob.ReasoningDisplay ?? DefaultReasoningDisplay;
        PromptDockPosition = blob.PromptDockPosition ?? DefaultPromptDockPosition;
        AutoCompactionEnabled = blob.AutoCompactionEnabled ?? DefaultAutoCompactionEnabled;
        AutoCompactionThresholdPercent = blob.AutoCompactionThresholdPercent is int p
            ? Math.Clamp(p, MinAutoCompactionThresholdPercent, MaxAutoCompactionThresholdPercent)
            : DefaultAutoCompactionThresholdPercent;
        AutoCompactionKeepRecentTurns = blob.AutoCompactionKeepRecentTurns is int k
            ? Math.Clamp(k, MinAutoCompactionKeepRecentTurns, MaxAutoCompactionKeepRecentTurns)
            : DefaultAutoCompactionKeepRecentTurns;
        MaxToolIterations = blob.MaxToolIterations is int mi
            ? Math.Clamp(mi, MinMaxToolIterations, MaxMaxToolIterations)
            : AgentLoopOptions.Default.MaxIterations;
        DeferredToolsHint = blob.DeferredToolsHint ?? DefaultDeferredToolsHint;
        ShellPreference = blob.ShellPreference ?? DefaultShellPreference;
        User = firstRun ? DefaultUserToolSettings() : MapFromDto(blob.Tools);
        UserInstructionFiles = firstRun
            ? DefaultUserInstructionFiles
            : (blob.InstructionFiles?.ToArray() ?? Array.Empty<string>());
        UserScripts = ScriptsFromDto(blob.Scripts);
        UserSubagents = firstRun ? DefaultUserSubagents() : SubagentsFromDto(blob.Subagents);
    }

    // Seeded only on first run, alongside DefaultUserToolSettings.
    //
    // "explorer" — a read-only investigator that answers broad questions about the repo.
    // Deliberately excludes the code_* tools so it leans on plain read/list/find/grep.
    // Mutating and execute tools are omitted entirely.
    //
    // "code" — a mutating implementer that takes a ready-to-go plan, applies the edits
    // (leaning on the code_* symbol tools), updates unit tests, builds/tests via any
    // registered script it has, and returns a summary. Its instructions are deliberately
    // tool-agnostic about how to build/test: it reasons from whatever tools it's been given,
    // so a user can later grant execute_command without editing the prompt.
    private static SubagentRegistry DefaultUserSubagents()
    {
        var explorer = new SubagentDefinition(
            Id: Guid.NewGuid(),
            Name: "explorer",
            Description: "Read-only repository explorer. Delegate broad discovery questions to it: " +
                         "which files implement X, what conventions the project uses, how something is wired. " +
                         "Returns a concise written answer, not file dumps.",
            // The shared subagent preamble (see SubagentRunner.BuildSession) already covers the
            // headless context, the subagent_result handoff, and "don't ask, just assume" — so
            // these instructions only carry the explorer-specific role and tool guidance.
            Instructions:
                "You are a read-only repository explorer. Investigate the workspace with the read_file, " +
                "list_directory, find_files, and grep tools to answer the caller's question, then deliver " +
                "a concise, concrete answer that cites file paths. Never attempt to modify files.",
            AllowedTools: new[] { "read_file", "list_directory", "find_files", "grep" },
            RestrictToWorkspace: true);

        var code = new SubagentDefinition(
            Id: Guid.NewGuid(),
            Name: "code",
            Description: "Implements a ready-to-go plan. Delegate to it once a change has been analyzed " +
                         "and is ready to write: it applies the code edits, updates unit tests, builds and " +
                         "tests if able, and returns a summary of what changed plus any deviations from the " +
                         "plan. Give it a concrete, self-contained implementation brief.",
            // The shared preamble already covers the headless context, the subagent_result handoff,
            // and "don't ask, just assume". These instructions carry only the role and behavioral
            // contract — and deliberately do NOT name build/test tools, so the agent reasons from
            // whatever tools it has and the prompt stays stable if the user grants more later.
            Instructions:
                "You implement a change that has already been analyzed and described. Apply the code edits " +
                "the plan calls for and add or update " +
                "unit tests to cover the change.\n\n" +
                "After making your changes, use whatever tools you have to build the project and run the " +
                "relevant tests, and iterate until they pass. If you have no tool available to build or test, " +
                "say so explicitly in your result so the caller can take over.\n\n" +
                "If you cannot implement the request in a way that matches how it was described, do not " +
                "improvise a different solution: return a result stating that you did not implement it and " +
                "explaining why.\n\n" +
                "Your result must summarize what changed (the files and symbols you touched), report the " +
                "build/test outcome with the actual evidence, and call out any deviations from the plan or " +
                "caveats implied by the choices you made.",
            AllowedTools: new[]
            {
                "read_file", "write_file", "edit_file", "delete_file", "list_directory", "find_files", "grep",
                "execute_registered_script",
            },
            RestrictToWorkspace: true);

        return new SubagentRegistry(new[] { explorer, code });
    }

    // Seeded only when no User blob has ever been written to disk.
    // Once the user has stored anything, their choices — including explicit "inherit"
    // (a removed entry) — are respected verbatim.
    private static ToolSettingsMap DefaultUserToolSettings()
    {
        static ToolPermissionSettings Workspace(PermissionLevel p) =>
            new() { Permission = p, RestrictToWorkspace = true };

        return new ToolSettingsMap(new Dictionary<string, ToolPermissionSettings>(StringComparer.Ordinal)
        {
            ["read_file"]                = Workspace(PermissionLevel.Allow),
            ["write_file"]               = Workspace(PermissionLevel.Allow),
            ["delete_file"]              = Workspace(PermissionLevel.Allow),
            ["list_directory"]           = Workspace(PermissionLevel.Allow),
            ["execute_command"]          = new ToolPermissionSettings { Permission = PermissionLevel.Ask },
            ["execute_registered_script"] = Workspace(PermissionLevel.Allow),
            ["execute_discovered_script"] = Workspace(PermissionLevel.Ask),
        });
    }

    public void HydrateProject(string? json)
    {
        var blob = TryDeserialize<ProjectBlob>(json) ?? new ProjectBlob();
        Project = MapFromDto(blob.Tools);
        ProjectInstructionFiles = blob.InstructionFiles?.ToArray() ?? Array.Empty<string>();
        ProjectInheritsUserInstructions = blob.InheritsUserInstructions ?? true;
        ProjectScripts = ScriptsFromDto(blob.Scripts);
        ProjectSubagents = SubagentsFromDto(blob.Subagents);
        Orchestrator = OrchestratorFromDto(blob.Orchestrator);
    }

    public string SerializeUser() =>
        JsonSerializer.Serialize(ToUserBlob(includeApiKeys: true), JsonOptions);

    /// <summary>Snapshot the live user-tier state into a serializable blob. Shared by
    /// <see cref="SerializeUser"/> (persist to disk, always with keys) and
    /// <see cref="ExportUser"/> (manual export, keys optional).</summary>
    private UserBlob ToUserBlob(bool includeApiKeys) =>
        new()
        {
            Endpoints = EndpointsToDto(Endpoints, includeApiKeys),
            DefaultEndpointId = Endpoints.DefaultId,
            SystemPrompt = SystemPrompt,
            MarkdownEnabled = MarkdownEnabled,
            ReasoningDisplay = ReasoningDisplay,
            PromptDockPosition = PromptDockPosition,
            AutoCompactionEnabled = AutoCompactionEnabled,
            AutoCompactionThresholdPercent = AutoCompactionThresholdPercent,
            AutoCompactionKeepRecentTurns = AutoCompactionKeepRecentTurns,
            MaxToolIterations = MaxToolIterations,
            DeferredToolsHint = DeferredToolsHint,
            ShellPreference = ShellPreference,
            Tools = MapToDto(User),
            InstructionFiles = NonEmpty(UserInstructionFiles),
            Scripts = ScriptsToDto(UserScripts),
            Subagents = SubagentsToDto(UserSubagents),
        };

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string ExportFormat = "yamca.settings";
    private const int ExportVersion = 1;

    public string ExportUser(bool includeApiKey)
    {
        var envelope = new SettingsExportEnvelope
        {
            Format = ExportFormat,
            Version = ExportVersion,
            Tier = "user",
            ExportedAt = DateTimeOffset.UtcNow,
            User = ToUserBlob(includeApiKeys: includeApiKey),
        };
        return JsonSerializer.Serialize(envelope, ExportJsonOptions);
    }

    public sealed record ImportResult(bool Success, string? Error)
    {
        public static ImportResult Ok() => new(true, null);
        public static ImportResult Fail(string error) => new(false, error);
    }

    public ImportResult ImportUser(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ImportResult.Fail("File is empty.");

        SettingsExportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SettingsExportEnvelope>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ImportResult.Fail($"Not valid JSON: {ex.Message}");
        }

        if (envelope is null)
            return ImportResult.Fail("File is empty or not a JSON object.");
        if (!string.Equals(envelope.Format, ExportFormat, StringComparison.Ordinal))
            return ImportResult.Fail($"Unrecognized file format (expected \"{ExportFormat}\").");
        if (envelope.Version != ExportVersion)
            return ImportResult.Fail($"Unsupported export version {envelope.Version} (this build expects {ExportVersion}).");
        if (envelope.User is null)
            return ImportResult.Fail("File is missing the \"user\" section.");

        ApplyBlob(envelope.User, firstRun: false);
        Changed?.Invoke(SettingsTier.User);
        return ImportResult.Ok();
    }

    public string SerializeProject()
    {
        var blob = new ProjectBlob
        {
            Tools = MapToDto(Project),
            InstructionFiles = NonEmpty(ProjectInstructionFiles),
            InheritsUserInstructions = ProjectInheritsUserInstructions ? null : false,
            Scripts = ScriptsToDto(ProjectScripts),
            Subagents = SubagentsToDto(ProjectSubagents),
            Orchestrator = OrchestratorToDto(Orchestrator),
        };
        return JsonSerializer.Serialize(blob, JsonOptions);
    }

    private static List<string>? NonEmpty(IReadOnlyList<string> source)
    {
        var trimmed = source.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return trimmed.Count == 0 ? null : trimmed;
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static ToolSettingsMap MapFromDto(Dictionary<string, ToolEntryDto>? dto)
    {
        if (dto is null) return ToolSettingsMap.Empty;
        var dict = new Dictionary<string, ToolPermissionSettings>(StringComparer.Ordinal);
        foreach (var (name, entry) in dto)
        {
            var permission = entry.Permission;
            var availability = entry.Availability;

            // Migration: Deny is no longer a user-selectable permission. A persisted Deny meant
            // "this tool must never run", which is now expressed as Hidden availability — strictly
            // better, since the model never even sees a hidden tool. Convert on load so old blobs
            // keep working and a stored Deny can never resurface.
            if (permission == PermissionLevel.Deny)
            {
                permission = null;
                availability = Availability.Hidden;
            }

            dict[name] = new ToolPermissionSettings
            {
                Permission = permission,
                RestrictToWorkspace = entry.RestrictToWorkspace,
                Availability = availability,
            };
        }
        return new ToolSettingsMap(dict);
    }

    private static Dictionary<string, ToolEntryDto> MapToDto(ToolSettingsMap map)
    {
        var dto = new Dictionary<string, ToolEntryDto>(StringComparer.Ordinal);
        foreach (var (name, entry) in map.Entries)
        {
            dto[name] = new ToolEntryDto
            {
                Permission = entry.Permission,
                RestrictToWorkspace = entry.RestrictToWorkspace,
                Availability = entry.Availability,
            };
        }
        return dto;
    }

    // --- DTOs --------------------------------------------------------------------

    private sealed class UserBlob
    {
        public List<EndpointDto>? Endpoints { get; set; }
        public Guid? DefaultEndpointId { get; set; }
        public string? SystemPrompt { get; set; }
        public bool? MarkdownEnabled { get; set; }
        public ReasoningDisplay? ReasoningDisplay { get; set; }
        public PromptDockPosition? PromptDockPosition { get; set; }
        public bool? AutoCompactionEnabled { get; set; }
        public int? AutoCompactionThresholdPercent { get; set; }
        public int? AutoCompactionKeepRecentTurns { get; set; }
        public int? MaxToolIterations { get; set; }
        public DeferredToolsHint? DeferredToolsHint { get; set; }
        public ShellPreference? ShellPreference { get; set; }
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
        public List<string>? InstructionFiles { get; set; }
        public ScriptsDto? Scripts { get; set; }
        public List<SubagentDto>? Subagents { get; set; }
    }

    private sealed class ProjectBlob
    {
        public Dictionary<string, ToolEntryDto>? Tools { get; set; }
        public List<string>? InstructionFiles { get; set; }
        public bool? InheritsUserInstructions { get; set; }
        public ScriptsDto? Scripts { get; set; }
        public List<SubagentDto>? Subagents { get; set; }
        public OrchestratorDto? Orchestrator { get; set; }
    }

    private sealed class EndpointDto
    {
        public Guid? Id { get; set; }
        public string? Name { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Model { get; set; }
    }

    private static EndpointsSettings EndpointsFromBlob(UserBlob blob)
    {
        if (blob.Endpoints is not { Count: > 0 } list)
            return EndpointsSettings.CreateDefault();

        var items = new List<EndpointSettings>(list.Count);
        foreach (var dto in list)
        {
            items.Add(new EndpointSettings(
                Id: dto.Id ?? Guid.NewGuid(),
                Name: string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim(),
                BaseUrl: dto.BaseUrl ?? "",
                ApiKey: dto.ApiKey ?? "",
                Model: dto.Model ?? ""));
        }

        var defaultId = blob.DefaultEndpointId is Guid id && items.Any(e => e.Id == id)
            ? id
            : items[0].Id;
        return new EndpointsSettings(items, defaultId);
    }

    private static List<EndpointDto> EndpointsToDto(EndpointsSettings endpoints, bool includeApiKeys) =>
        endpoints.Items.Select(e => new EndpointDto
        {
            Id = e.Id,
            Name = string.IsNullOrWhiteSpace(e.Name) ? null : e.Name,
            BaseUrl = e.BaseUrl,
            ApiKey = includeApiKeys ? e.ApiKey : string.Empty,
            Model = e.Model,
        }).ToList();

    private sealed class SettingsExportEnvelope
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Tier { get; set; }
        public DateTimeOffset? ExportedAt { get; set; }
        public UserBlob? User { get; set; }
    }

    private sealed class ToolEntryDto
    {
        public Yamca.Agent.Permissions.PermissionLevel? Permission { get; set; }
        public bool? RestrictToWorkspace { get; set; }
        public Availability? Availability { get; set; }
    }

    private sealed class ScriptsDto
    {
        public List<ScriptEntryDto>? Registered { get; set; }
        public List<ScriptEntryDto>? Directories { get; set; }
        public List<InlineScriptDto>? Inline { get; set; }
    }

    private sealed class ScriptEntryDto
    {
        public string? Path { get; set; }
        public string? Description { get; set; }
        public bool? SuppressOutputOnSuccess { get; set; }
    }

    private sealed class InlineScriptDto
    {
        public string? Command { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool? SuppressOutputOnSuccess { get; set; }
        public bool? Background { get; set; }
    }

    private static ScriptRegistry ScriptsFromDto(ScriptsDto? dto)
    {
        if (dto is null) return ScriptRegistry.Empty;

        var files = (dto.Registered ?? new List<ScriptEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Select(e => new RegisteredScript(NormalizePath(e.Path!), Trim(e.Description), e.SuppressOutputOnSuccess ?? false))
            .ToList();
        var dirs = (dto.Directories ?? new List<ScriptEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Select(e => new RegisteredScriptDirectory(NormalizePath(e.Path!), Trim(e.Description), e.SuppressOutputOnSuccess ?? false))
            .ToList();
        var inline = (dto.Inline ?? new List<InlineScriptDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Command))
            .Select(e => new RegisteredInlineScript(e.Command!.Trim(), Trim(e.Description), e.SuppressOutputOnSuccess ?? false, Trim(e.Name), e.Background ?? false))
            .ToList();

        if (files.Count == 0 && dirs.Count == 0 && inline.Count == 0) return ScriptRegistry.Empty;
        return new ScriptRegistry(files, dirs, inline);
    }

    private static ScriptsDto? ScriptsToDto(ScriptRegistry registry)
    {
        if (registry.IsEmpty) return null;
        // Emit the flag only when true (WhenWritingNull omits nulls), keeping JSON clean.
        return new ScriptsDto
        {
            Registered = registry.Registered.Count == 0
                ? null
                : registry.Registered.Select(e => new ScriptEntryDto { Path = e.Path, Description = e.Description, SuppressOutputOnSuccess = e.SuppressOutputOnSuccess ? true : null }).ToList(),
            Directories = registry.Directories.Count == 0
                ? null
                : registry.Directories.Select(d => new ScriptEntryDto { Path = d.Path, Description = d.Description, SuppressOutputOnSuccess = d.SuppressOutputOnSuccess ? true : null }).ToList(),
            Inline = registry.Inline.Count == 0
                ? null
                : registry.Inline.Select(i => new InlineScriptDto { Command = i.Command, Name = i.Name, Description = i.Description, SuppressOutputOnSuccess = i.SuppressOutputOnSuccess ? true : null, Background = i.Background ? true : null }).ToList(),
        };
    }

    private static string NormalizePath(string raw) => raw.Trim().Replace('\\', '/');
    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class SubagentDto
    {
        public Guid? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Instructions { get; set; }
        public List<string>? AllowedTools { get; set; }
        public bool? RestrictToWorkspace { get; set; }
        public bool? RequireApproval { get; set; }
        public Guid? EndpointId { get; set; }
        public int? MaxIterations { get; set; }
    }

    private static SubagentRegistry SubagentsFromDto(List<SubagentDto>? dto)
    {
        if (dto is null || dto.Count == 0) return SubagentRegistry.Empty;

        var agents = new List<SubagentDefinition>(dto.Count);
        foreach (var d in dto)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) continue;
            agents.Add(new SubagentDefinition(
                Id: d.Id ?? Guid.NewGuid(),
                Name: d.Name.Trim(),
                Description: d.Description?.Trim() ?? string.Empty,
                Instructions: d.Instructions ?? string.Empty,
                AllowedTools: (d.AllowedTools ?? new List<string>())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .ToList(),
                RestrictToWorkspace: d.RestrictToWorkspace ?? true,
                RequireApproval: d.RequireApproval ?? false,
                EndpointId: d.EndpointId,
                MaxIterations: d.MaxIterations));
        }

        return agents.Count == 0 ? SubagentRegistry.Empty : new SubagentRegistry(agents);
    }

    private static List<SubagentDto>? SubagentsToDto(SubagentRegistry registry)
    {
        if (registry.IsEmpty) return null;
        return registry.Agents.Select(a => new SubagentDto
        {
            Id = a.Id,
            Name = a.Name,
            Description = string.IsNullOrWhiteSpace(a.Description) ? null : a.Description,
            Instructions = string.IsNullOrWhiteSpace(a.Instructions) ? null : a.Instructions,
            AllowedTools = a.AllowedTools.Count == 0 ? null : a.AllowedTools.ToList(),
            RestrictToWorkspace = a.RestrictToWorkspace,
            RequireApproval = a.RequireApproval ? true : null,
            EndpointId = a.EndpointId,
            MaxIterations = a.MaxIterations,
        }).ToList();
    }

    private sealed class OrchestratorDto
    {
        public List<string>? EnabledColumns { get; set; }
        public Guid? EndpointId { get; set; }
        public int? MaxConcurrentRuns { get; set; }
        public int? MaxConcurrentRunsPerColumn { get; set; }
        public int? MaxTurnsPerRun { get; set; }
        public int? MaxToolIterationsPerTurn { get; set; }
        public int? StallTimeoutSeconds { get; set; }
        public int? TurnTimeoutSeconds { get; set; }
        public int? RetryMaxAttempts { get; set; }
        public int? RetryBaseDelaySeconds { get; set; }
        public int? RetryMaxDelaySeconds { get; set; }
        public int? PollIntervalSeconds { get; set; }
        public List<string>? AllowedTools { get; set; }
        public bool? RestrictToWorkspace { get; set; }
    }

    // Missing fields fall back to OrchestratorSettings.Default per field, then everything is
    // clamped — so a hand-edited project.json can never hydrate out-of-range live values.
    private static OrchestratorSettings OrchestratorFromDto(OrchestratorDto? dto)
    {
        if (dto is null) return OrchestratorSettings.Default;
        var d = OrchestratorSettings.Default;
        return ClampOrchestrator(new OrchestratorSettings(
            EnabledColumns: dto.EnabledColumns ?? d.EnabledColumns,
            EndpointId: dto.EndpointId,
            MaxConcurrentRuns: dto.MaxConcurrentRuns ?? d.MaxConcurrentRuns,
            MaxConcurrentRunsPerColumn: dto.MaxConcurrentRunsPerColumn,
            MaxTurnsPerRun: dto.MaxTurnsPerRun ?? d.MaxTurnsPerRun,
            MaxToolIterationsPerTurn: dto.MaxToolIterationsPerTurn,
            StallTimeoutSeconds: dto.StallTimeoutSeconds ?? d.StallTimeoutSeconds,
            TurnTimeoutSeconds: dto.TurnTimeoutSeconds ?? d.TurnTimeoutSeconds,
            RetryMaxAttempts: dto.RetryMaxAttempts ?? d.RetryMaxAttempts,
            RetryBaseDelaySeconds: dto.RetryBaseDelaySeconds ?? d.RetryBaseDelaySeconds,
            RetryMaxDelaySeconds: dto.RetryMaxDelaySeconds ?? d.RetryMaxDelaySeconds,
            PollIntervalSeconds: dto.PollIntervalSeconds ?? d.PollIntervalSeconds,
            AllowedTools: dto.AllowedTools ?? d.AllowedTools,
            RestrictToWorkspace: dto.RestrictToWorkspace ?? d.RestrictToWorkspace));
    }

    // Emit null when the whole record still matches the defaults so a project that never
    // touched the orchestrator keeps a clean project.json (same idea as ScriptsToDto).
    private static OrchestratorDto? OrchestratorToDto(OrchestratorSettings s)
    {
        var d = OrchestratorSettings.Default;
        var isDefault = s.EnabledColumns.Count == 0
            && s.EndpointId is null
            && s.MaxConcurrentRuns == d.MaxConcurrentRuns
            && s.MaxConcurrentRunsPerColumn is null
            && s.MaxTurnsPerRun == d.MaxTurnsPerRun
            && s.MaxToolIterationsPerTurn is null
            && s.StallTimeoutSeconds == d.StallTimeoutSeconds
            && s.TurnTimeoutSeconds == d.TurnTimeoutSeconds
            && s.RetryMaxAttempts == d.RetryMaxAttempts
            && s.RetryBaseDelaySeconds == d.RetryBaseDelaySeconds
            && s.RetryMaxDelaySeconds == d.RetryMaxDelaySeconds
            && s.PollIntervalSeconds == d.PollIntervalSeconds
            && s.AllowedTools.SequenceEqual(d.AllowedTools, StringComparer.Ordinal)
            && s.RestrictToWorkspace == d.RestrictToWorkspace;
        if (isDefault) return null;

        return new OrchestratorDto
        {
            EnabledColumns = s.EnabledColumns.Count == 0 ? null : s.EnabledColumns.ToList(),
            EndpointId = s.EndpointId,
            MaxConcurrentRuns = s.MaxConcurrentRuns,
            MaxConcurrentRunsPerColumn = s.MaxConcurrentRunsPerColumn,
            MaxTurnsPerRun = s.MaxTurnsPerRun,
            MaxToolIterationsPerTurn = s.MaxToolIterationsPerTurn,
            StallTimeoutSeconds = s.StallTimeoutSeconds,
            TurnTimeoutSeconds = s.TurnTimeoutSeconds,
            RetryMaxAttempts = s.RetryMaxAttempts,
            RetryBaseDelaySeconds = s.RetryBaseDelaySeconds,
            RetryMaxDelaySeconds = s.RetryMaxDelaySeconds,
            PollIntervalSeconds = s.PollIntervalSeconds,
            AllowedTools = s.AllowedTools.ToList(),
            RestrictToWorkspace = s.RestrictToWorkspace,
        };
    }
}
