using System.Text.Json;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Tools;

internal static class ToolArguments
{
    public static bool TryGetString(JsonElement args, string name, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (args.ValueKind != JsonValueKind.Object)
        {
            error = "Arguments must be a JSON object.";
            return false;
        }

        if (!args.TryGetProperty(name, out var prop))
        {
            error = $"Missing required argument '{name}'.";
            return false;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            error = $"Argument '{name}' must be a string.";
            return false;
        }

        value = prop.GetString() ?? string.Empty;
        return true;
    }

    public static bool TryGetBool(JsonElement args, string name, bool defaultValue, out bool value, out string error)
    {
        value = defaultValue;
        error = string.Empty;

        if (args.ValueKind != JsonValueKind.Object)
        {
            error = "Arguments must be a JSON object.";
            return false;
        }

        if (!args.TryGetProperty(name, out var prop))
            return true;

        if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }

        error = $"Argument '{name}' must be a boolean.";
        return false;
    }

    /// <summary>
    /// Resolve a path argument honoring the workspace-restriction flag. When restricted,
    /// the path goes through <see cref="IWorkspace.Resolve"/> (sandboxed). When not, it is
    /// canonicalized but allowed to leave the workspace.
    /// </summary>
    public static bool TryResolvePath(ToolContext ctx, string requested, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = string.Empty;

        try
        {
            if (ctx.RestrictToWorkspace)
            {
                resolved = ctx.Workspace.Resolve(requested);
            }
            else
            {
                var combined = Path.IsPathRooted(requested)
                    ? requested
                    : Path.Combine(ctx.Workspace.RootPath, requested);
                resolved = Path.GetFullPath(combined);
            }
            return true;
        }
        catch (PathOutsideWorkspaceException ex)
        {
            error = $"Path '{requested}' is outside the workspace root '{ex.RootPath}'.";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Invalid path '{requested}': {ex.Message}";
            return false;
        }
    }
}
