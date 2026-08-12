using System.Text.Json;
using CodexMicroBridge.App;

namespace CodexMicroBridge.Tests;

public sealed class BridgeRuntimeNotificationTests
{
    [Fact]
    public void TurnCompletedItems_RecoverCommentaryAndFinalAnswer()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            threadId = "thread-one",
            turn = new
            {
                id = "turn-one",
                status = "completed",
                items = new object[]
                {
                    new { type = "userMessage", id = "user-one", content = Array.Empty<object>() },
                    new { type = "agentMessage", id = "agent-commentary", text = "正在检查。", phase = "commentary" },
                    new { type = "agentMessage", id = "agent-final", text = "任务已经完成。", phase = "final_answer" },
                },
            },
        });

        var messages = BridgeRuntime.ExtractCompletedMessages("turn/completed", parameters);

        Assert.Collection(
            messages,
            commentary =>
            {
                Assert.Equal("agent-commentary", commentary.MessageId);
                Assert.Equal("正在检查。", commentary.Text);
            },
            final =>
            {
                Assert.Equal("agent-final", final.MessageId);
                Assert.Equal("任务已经完成。", final.Text);
                Assert.Equal("thread-one", final.ThreadId);
                Assert.Equal("turn-one", final.TurnId);
            });
    }

    [Fact]
    public void ItemCompleted_ProducesOneAuthoritativeMessage()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            threadId = "thread-one",
            turnId = "turn-one",
            completedAtMs = 1_700_000_000_000L,
            item = new
            {
                type = "agentMessage",
                id = "agent-final",
                text = "最终回复",
                phase = "final_answer",
            },
        });

        var message = Assert.Single(BridgeRuntime.ExtractCompletedMessages("item/completed", parameters));
        Assert.Equal("最终回复", message.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L), message.CompletedAt);
    }
}
