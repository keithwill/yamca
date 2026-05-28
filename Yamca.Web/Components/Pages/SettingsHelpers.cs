namespace Yamca.Web.Components.Pages;

internal static class SettingsHelpers
{
    public static string ShortKey(string key)
    {
        const string prefix = "yamca.project.";
        if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length + 8)
        {
            return $"{prefix}{key.AsSpan(prefix.Length, 8)}…";
        }
        return key;
    }
}
