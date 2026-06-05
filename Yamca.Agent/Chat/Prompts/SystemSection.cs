namespace Yamca.Agent.Chat.Prompts;

/// <summary>A recognizable region within the single concatenated system message that
/// <see cref="ChatSession"/> sends. <see cref="Marker"/> is the byte-stable prefix written
/// into the message when the section is present; <see cref="Label"/> is the human name shown
/// in the "view raw context" diagnostic. Writers (<see cref="ChatSession"/>, the
/// instruction-file loader) emit the marker; the raw-context reader splits the message back
/// into labeled sections using the same markers — so both sides share one definition and
/// cannot drift.</summary>
public sealed record SystemSection(string Marker, string Label);
