using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Yamca.Agent.Chat;
using Yamca.Agent.Permissions;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;
using Yamca.Agent.Tools;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

/// <summary>Guards against the construction-time dependency cycle that subagents can reintroduce:
/// <c>IToolRegistry</c> is registered as a factory that enumerates every <see cref="ITool"/>,
/// one of which (<see cref="SubagentRunTool"/>) depends on <see cref="ISubagentRunner"/>, which
/// in turn needs the registry. If the runner resolves the registry eagerly, this loops forever
/// (a blank page / wedged process at runtime rather than a clean DI error). The runner must
/// resolve <c>IToolRegistry</c> lazily so the graph below resolves.</summary>
[TestFixture]
public class SubagentDiResolutionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<EndpointClientFactory>();

        services.AddScoped<SessionSettings>();
        services.AddScoped<ISessionSettings>(sp => sp.GetRequiredService<SessionSettings>());

        services.AddScoped<ISubagentRunner, SubagentRunner>();
        services.AddScoped<ITool, SubagentRunTool>();

        services.AddScoped<IToolRegistry>(sp =>
            new ToolRegistry(sp.GetServices<ITool>(), sp.GetServices<IDynamicToolSource>()));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public void ResolvingToolRegistry_DoesNotCycle()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();

        Assert.That(registry, Is.Not.Null);
        Assert.That(registry.Tools.Select(t => t.Name), Does.Contain(SubagentRunTool.ToolName));
    }

    [Test]
    public void ResolvingRunnerAndTools_DoesNotCycle()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<ISubagentRunner>();
        var tools = scope.ServiceProvider.GetServices<ITool>().ToList();

        Assert.That(runner, Is.Not.Null);
        Assert.That(tools, Has.Some.InstanceOf<SubagentRunTool>());
    }
}
