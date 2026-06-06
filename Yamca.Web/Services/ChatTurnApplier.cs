using Yamca.Agent.Chat;

namespace Yamca.Web.Services;

/// <summary>Applies a single <see cref="ChatStreamEvent"/> to a <see cref="ChatTurn"/>,
/// building up its item list (assistant text, reasoning, tool-call cards) and activity
/// state. Shared by <see cref="ChatViewModel"/> (the live chat) and the subagent session
/// registry, so a subagent's transcript renders identically to a real chat turn.
///
/// Only the turn-building cases live here. Caller-specific concerns (e.g. usage-token
/// bookkeeping on the chat view model) stay with the caller.</summary>
public static class ChatTurnApplier
{
    public static void Apply(ChatTurn turn, ChatStreamEvent ev)
    {
        switch (ev)
        {
            case ModelRequestStartedEvent:
                // A round-trip just began — we're processing the prompt until the first token.
                turn.Activity = TurnActivity.ProcessingPrompt;
                break;

            case ToolCallGenerationStartedEvent:
                // The model has started streaming tool calls — switch to the wrench now, while
                // the (often slow) argument generation is still in flight, rather than waiting
                // for the brief execution window.
                turn.Activity = TurnActivity.RunningTools;
                break;

            case AssistantTokenEvent token:
                // Tokens are streaming; the content itself is the indicator now.
                turn.Activity = TurnActivity.Idle;
                var text = CurrentOrNewText(turn);
                text.Append(token.Delta);
                break;

            case ReasoningTokenEvent rtoken:
                turn.Activity = TurnActivity.Idle;
                var rItem = CurrentOrNewReasoning(turn);
                rItem.Append(rtoken.Delta);
                break;

            case ReasoningCompleteEvent:
                var openR = CurrentReasoning(turn);
                if (openR is not null) openR.IsComplete = true;
                break;

            case AssistantMessageEvent msg:
                // Generation finished. When tool calls follow, keep the wrench up through
                // execution; otherwise the turn is ending, so drop the indicator.
                if (msg.ToolCalls.Count == 0)
                    turn.Activity = TurnActivity.Idle;
                // The streaming buffer already holds the same content; just mark complete.
                var current = CurrentText(turn);
                if (current is null && !string.IsNullOrEmpty(msg.Content))
                {
                    var t = new AssistantTextItem();
                    t.Append(msg.Content);
                    turn.AddItem(t);
                    current = t;
                }
                if (current is not null) current.IsComplete = true;
                break;

            case ToolCallStartedEvent started:
                turn.Activity = TurnActivity.RunningTools;
                turn.AddItem(new ToolCallItem
                {
                    CallId = started.CallId,
                    ToolName = started.ToolName,
                    ArgumentsJson = started.ArgumentsJson,
                    State = ToolCallState.Pending,
                });
                break;

            case ToolCallResultEvent done:
                if (TryFind(turn, done.CallId, out var doneItem))
                {
                    doneItem.State = done.IsError ? ToolCallState.Failed : ToolCallState.Succeeded;
                    doneItem.Result = done.Content;
                }
                break;

            case ToolDeniedEvent denied:
                if (TryFind(turn, denied.CallId, out var dItem))
                {
                    dItem.State = ToolCallState.Denied;
                    dItem.Result = denied.Reason;
                }
                else
                {
                    // Some denial paths (unknown tool, malformed JSON) skip the
                    // ToolCallStartedEvent and only emit the denied/result event.
                    turn.AddItem(new ToolCallItem
                    {
                        CallId = denied.CallId,
                        ToolName = denied.ToolName,
                        ArgumentsJson = "",
                        State = ToolCallState.Denied,
                        Result = denied.Reason,
                    });
                }
                break;

            case TurnCompleteEvent complete:
                turn.Activity = TurnActivity.Idle;
                turn.MaxIterationsReached = complete.Reason == TurnCompletionReason.MaxIterationsReached;
                break;
        }
    }

    private static AssistantTextItem CurrentOrNewText(ChatTurn turn)
    {
        if (turn.LastItem is AssistantTextItem t && !t.IsComplete) return t;
        var fresh = new AssistantTextItem();
        turn.AddItem(fresh);
        return fresh;
    }

    private static AssistantTextItem? CurrentText(ChatTurn turn) =>
        turn.Items.OfType<AssistantTextItem>().LastOrDefault(t => !t.IsComplete);

    private static ReasoningItem CurrentOrNewReasoning(ChatTurn turn)
    {
        if (turn.LastItem is ReasoningItem r && !r.IsComplete) return r;
        var fresh = new ReasoningItem();
        turn.AddItem(fresh);
        return fresh;
    }

    private static ReasoningItem? CurrentReasoning(ChatTurn turn) =>
        turn.Items.OfType<ReasoningItem>().LastOrDefault(r => !r.IsComplete);

    private static bool TryFind(ChatTurn turn, string callId, out ToolCallItem item)
    {
        item = turn.FindToolCall(callId)!;
        return item is not null;
    }
}
