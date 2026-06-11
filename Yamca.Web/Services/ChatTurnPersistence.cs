using Yamca.Agent.Chat.Persistence;

namespace Yamca.Web.Services;

/// <summary>Maps a live <see cref="ChatTurn"/> to its persisted form. Shared by
/// <see cref="ChatViewModel"/> (chat history saves) and the orchestrator's run
/// persistence so both write identical turn documents.</summary>
public static class ChatTurnPersistence
{
    public static PersistedTurn ToPersistedTurn(ChatTurn turn)
    {
        var pt = new PersistedTurn
        {
            UserMessage = turn.UserMessage,
            Error = turn.Error,
            Images = turn.Images.Count > 0 ? turn.Images.ToList() : null,
        };
        foreach (var item in turn.Items)
        {
            pt.Items.Add(item switch
            {
                AssistantTextItem a => new PersistedTurnItem { Kind = "text", Text = a.Text, IsComplete = a.IsComplete },
                ReasoningItem r => new PersistedTurnItem { Kind = "reasoning", Text = r.Text, IsComplete = r.IsComplete },
                ToolCallItem c => new PersistedTurnItem
                {
                    Kind = "tool",
                    CallId = c.CallId,
                    ToolName = c.ToolName,
                    ArgumentsJson = c.ArgumentsJson,
                    State = c.State.ToString(),
                    Result = c.Result,
                },
                _ => new PersistedTurnItem { Kind = "text", Text = "" },
            });
        }
        return pt;
    }
}
