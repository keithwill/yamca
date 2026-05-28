namespace Yamca.Agent.Settings;

/// <summary>
/// Collection of configured OpenAI-compatible endpoints with one marked Default.
/// New chats start out targeting <see cref="Default"/> unless the user picks
/// another endpoint before sending the first message.
/// </summary>
public sealed record EndpointsSettings(
    IReadOnlyList<EndpointSettings> Items,
    Guid DefaultId)
{
    /// <summary>Seed used on a fresh install: one local endpoint, marked default.</summary>
    public static EndpointsSettings CreateDefault()
    {
        var seed = EndpointSettings.CreateDefault();
        return new EndpointsSettings(new[] { seed }, seed.Id);
    }

    public EndpointSettings? FindById(Guid id) =>
        Items.FirstOrDefault(e => e.Id == id);

    /// <summary>Endpoint identified by <see cref="DefaultId"/>; if that id no longer
    /// resolves (e.g. the default was deleted), falls back to the first item.</summary>
    public EndpointSettings Default =>
        FindById(DefaultId) ?? Items[0];
}
