namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// One symbol extracted from a parsed source file. <paramref name="Depth"/> drives the
/// indent level in the rendered output; <paramref name="Line"/> is 1-indexed for display.
/// </summary>
public sealed record Symbol(string Kind, string Display, int Line, int Depth);
