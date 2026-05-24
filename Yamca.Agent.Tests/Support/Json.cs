using System.Text.Json;

namespace Yamca.Agent.Tests.Support;

internal static class Json
{
    public static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
