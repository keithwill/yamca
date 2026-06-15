using System.Text.Json;
using NUnit.Framework;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Tools;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class SessionSettingsMaterializeTests
{
    // A read-only tool stub: no workspace support, defaults Allow / Eager.
    private static FakeTool ReadOnly(string name) =>
        new(name, PermissionLevel.Allow, Availability.Eager, supportsWorkspace: false);

    // A filesystem tool stub: supports workspace restriction, defaults Ask / Eager.
    private static FakeTool Fs(string name) =>
        new(name, PermissionLevel.Ask, Availability.Eager, supportsWorkspace: true);

    [Test]
    public void Materialize_FillsEveryFieldFromToolDefaults_OnEmptyUserTier()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}"); // not first run → User starts empty

        var tools = new ITool[] { ReadOnly("grep"), Fs("write_file") };
        var changed = settings.MaterializeUserToolDefaults(tools);

        Assert.That(changed, Is.True);

        var grep = settings.User.Get("grep")!;
        Assert.That(grep.Permission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(grep.Availability, Is.EqualTo(Availability.Eager));
        Assert.That(grep.RestrictToWorkspace, Is.Null, "tools without workspace support stay unset");

        var write = settings.User.Get("write_file")!;
        Assert.That(write.Permission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(write.RestrictToWorkspace, Is.True, "workspace-capable tools default to restricted");
    }

    [Test]
    public void Materialize_DoesNotOverwriteExplicitUserValues()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        // User has explicitly chosen Allow for a tool whose built-in default is Ask.
        settings.SetToolEntry(SettingsTier.User, "write_file",
            new ToolPermissionSettings { Permission = PermissionLevel.Allow, RestrictToWorkspace = true });

        settings.MaterializeUserToolDefaults(new ITool[] { Fs("write_file") });

        var write = settings.User.Get("write_file")!;
        Assert.That(write.Permission, Is.EqualTo(PermissionLevel.Allow), "explicit choice is preserved");
        Assert.That(write.Availability, Is.EqualTo(Availability.Eager), "missing field is backfilled");
    }

    [Test]
    public void Materialize_IsIdempotent()
    {
        var settings = new SessionSettings();
        settings.HydrateUser("{}");
        var tools = new ITool[] { ReadOnly("grep"), Fs("write_file") };

        Assert.That(settings.MaterializeUserToolDefaults(tools), Is.True);
        Assert.That(settings.MaterializeUserToolDefaults(tools), Is.False, "second pass changes nothing");
    }

    [Test]
    public void Materialize_PreservesCuratedFirstRunSeed()
    {
        var settings = new SessionSettings();
        settings.HydrateUser(null); // first run → curated seed (write_file = Allow)

        // The tool's own default is Ask; the curated seed must win.
        settings.MaterializeUserToolDefaults(new ITool[] { Fs("write_file") });

        Assert.That(settings.User.Get("write_file")!.Permission, Is.EqualTo(PermissionLevel.Allow));
    }

    [Test]
    public void Hydrate_MigratesLegacyDenyPermission_ToHiddenAvailability()
    {
        var settings = new SessionSettings();
        // A blob written by an older build, where "never run this" was a Deny permission.
        // restrictToWorkspace is carried through; the (now-removed) Deny becomes Hidden.
        settings.HydrateUser("""{"tools":{"execute_command":{"permission":"Deny","restrictToWorkspace":false}}}""");

        var entry = settings.User.Get("execute_command")!;
        Assert.That(entry.Permission, Is.Null, "the legacy Deny permission is dropped");
        Assert.That(entry.Availability, Is.EqualTo(Availability.Hidden), "and re-expressed as Hidden");
        Assert.That(entry.RestrictToWorkspace, Is.False, "other fields are preserved");
    }

    [Test]
    public void Hydrate_DenyMigration_OverridesAnyStoredAvailability()
    {
        var settings = new SessionSettings();
        // Deny + an explicit Eager is contradictory; the migration resolves it to Hidden.
        settings.HydrateProject("""{"tools":{"git_write":{"permission":"Deny","availability":"Eager"}}}""");

        var entry = settings.Project.Get("git_write")!;
        Assert.That(entry.Permission, Is.Null);
        Assert.That(entry.Availability, Is.EqualTo(Availability.Hidden));
    }

    private sealed class FakeTool : ITool
    {
        public FakeTool(string name, PermissionLevel permission, Availability availability, bool supportsWorkspace)
        {
            Name = name;
            DefaultPermission = permission;
            DefaultAvailability = availability;
            SupportsWorkspaceRestriction = supportsWorkspace;
        }

        public string Name { get; }
        public string Description => Name;
        public string ParametersSchema => "{}";
        public bool SupportsWorkspaceRestriction { get; }
        public PermissionLevel DefaultPermission { get; }
        public Availability DefaultAvailability { get; }

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
