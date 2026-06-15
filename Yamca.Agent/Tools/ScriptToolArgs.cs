using System.Text.Json;

namespace Yamca.Agent.Tools;

/// <summary>Shared argument parsing for the two script tools.</summary>
internal static class ScriptToolArgs
{
    public static bool TryParse(
        JsonElement arguments,
        out string scriptPath,
        out IReadOnlyList<string> args,
        out int timeoutSeconds,
        out int? maxOutputLines,
        out string error,
        string targetProperty = "script_path")
    {
        scriptPath = string.Empty;
        args = Array.Empty<string>();
        timeoutSeconds = 60;
        maxOutputLines = null;
        error = string.Empty;

        if (!ToolArguments.TryGetString(arguments, targetProperty, out scriptPath, out error))
            return false;

        if (arguments.TryGetProperty("arguments", out var argsProp))
        {
            if (argsProp.ValueKind != JsonValueKind.Array)
            {
                error = "Argument 'arguments' must be an array of strings.";
                return false;
            }
            var list = new List<string>(argsProp.GetArrayLength());
            foreach (var element in argsProp.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = "Every entry in 'arguments' must be a string.";
                    return false;
                }
                list.Add(element.GetString() ?? string.Empty);
            }
            args = list;
        }

        if (arguments.TryGetProperty("timeout_seconds", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
            timeoutSeconds = Math.Clamp(tProp.GetInt32(), 1, 600);

        if (arguments.TryGetProperty("max_output_lines", out var mProp) && mProp.ValueKind == JsonValueKind.Number)
            maxOutputLines = Math.Clamp(mProp.GetInt32(), 1, 10_000);

        return true;
    }
}
