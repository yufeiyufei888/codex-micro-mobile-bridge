using System.Text.Json;
using CodexMicroBridge.App;

namespace CodexMicroBridge.Tests;

public sealed class DesktopSyncTests
{
    [Fact]
    public void ReadsLatestCompletedAssistantMessageFromDesktopRollout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-micro-desktop-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-08-11T04:00:00Z",
                    type = "response_item",
                    payload = new
                    {
                        type = "message",
                        id = "message-old",
                        role = "assistant",
                        content = new[] { new { type = "output_text", text = "old" } },
                        internal_chat_message_metadata_passthrough = new { turn_id = "turn-old" },
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-08-11T04:00:01Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "new desktop response" },
                }),
            ]);

            var message = new DesktopSessionReader().ReadLatestAssistantMessage(path);

            Assert.NotNull(message);
            Assert.Equal("assistant", message.Role);
            Assert.Equal("new desktop response", message.Text);
            Assert.Equal(DateTimeOffset.Parse("2026-08-11T04:00:01Z"), message.Timestamp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsUserAndAssistantHistoryWithoutDuplicatingEventMirrors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-micro-history-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-08-12T01:00:00Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "测试问题" },
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-08-12T01:00:01Z",
                    type = "response_item",
                    payload = new
                    {
                        type = "message", id = "user-1", role = "user",
                        content = new[] { new { type = "input_text", text = "测试问题" } },
                        internal_chat_message_metadata_passthrough = new { turn_id = "turn-1" },
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-08-12T01:00:02Z",
                    type = "event_msg",
                    payload = new { type = "agent_message", message = "测试回答" },
                }),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-08-12T01:00:03Z",
                    type = "response_item",
                    payload = new
                    {
                        type = "message", id = "assistant-1", role = "assistant",
                        content = new[] { new { type = "output_text", text = "测试回答" } },
                        internal_chat_message_metadata_passthrough = new { turn_id = "turn-1" },
                    },
                }),
            ]);

            var messages = new DesktopSessionReader().ReadConversationMessages(path);

            Assert.Collection(messages,
                message =>
                {
                    Assert.Equal("user-1", message.MessageId);
                    Assert.Equal("user", message.Role);
                    Assert.Equal("测试问题", message.Text);
                },
                message =>
                {
                    Assert.Equal("assistant-1", message.MessageId);
                    Assert.Equal("assistant", message.Role);
                    Assert.Equal("测试回答", message.Text);
                });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("允许此对话 ↵", "AllowConversation")]
    [InlineData("Allow for this conversation Enter", "AllowConversation")]
    [InlineData("始终允许", "AlwaysAllow")]
    [InlineData("Always allow", "AlwaysAllow")]
    [InlineData("拒绝 Escape", "Decline")]
    [InlineData("Deny Escape", "Decline")]
    [InlineData("发送", "None")]
    public void ClassifiesCurrentComputerUseApprovalButtons(string name, string expected)
    {
        Assert.Equal(expected, DesktopCodexAutomation.ClassifyApprovalButtonName(name).ToString());
    }

    [Fact]
    public void CollapsesOnlyNearDuplicateHistoryRows()
    {
        var at = DateTimeOffset.Parse("2026-08-12T01:00:00Z");
        var messages = BridgeRuntime.CollapseDuplicateMessages(
        [
            new("event-1", "desktop-current", "turn-1", "event-1", "assistant", "相同回答", at),
            new("canonical-1", "desktop-current", "turn-1", "canonical-1", "assistant", "相同回答", at.AddSeconds(2)),
            new("canonical-2", "desktop-current", "turn-2", "canonical-2", "assistant", "相同回答", at.AddMinutes(1)),
        ]);

        Assert.Equal(2, messages.Count);
        Assert.Equal("event-1", messages[0].MessageId);
        Assert.Equal("canonical-2", messages[1].MessageId);
    }

    [Fact]
    public void DesktopProtocolIdentityIsStable()
    {
        Assert.Equal("desktop-current", BridgeRuntime.DesktopThreadId);
    }

    [Fact]
    public void AcceptsCurrentPackagedCodexExecutableAndWindowIdentities()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var executable = Path.Combine(
            programFiles,
            "WindowsApps",
            "OpenAI.Codex_26.803.8161.0_x64__2p2nqsd0c76g0",
            "app",
            "ChatGPT.exe");

        Assert.True(DesktopCodexAutomation.IsTrustedCodexExecutablePath(executable));
        Assert.True(DesktopCodexAutomation.IsTrustedCodexWindowIdentity("Codex", "Chrome_WidgetWin_1"));
        Assert.True(DesktopCodexAutomation.IsTrustedCodexWindowIdentity("ChatGPT", "Chrome_WidgetWin_1"));
    }

    [Fact]
    public void RejectsSpoofedCodexExecutableAndWindow()
    {
        var spoofed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OpenAI.Codex_fake",
            "app",
            "ChatGPT.exe");

        Assert.False(DesktopCodexAutomation.IsTrustedCodexExecutablePath(spoofed));
        Assert.False(DesktopCodexAutomation.IsTrustedCodexWindowIdentity("Codex Micro 桌面桥接", "Chrome_WidgetWin_1"));
        Assert.False(DesktopCodexAutomation.IsTrustedCodexWindowIdentity("Codex", "OtherWindowClass"));
    }

    [Theory]
    [InlineData("发送")]
    [InlineData("发送消息")]
    [InlineData("Send")]
    [InlineData("Send message")]
    public void AcceptsOnlyExplicitDesktopSendButtonNames(string name)
    {
        Assert.True(DesktopCodexAutomation.IsSendButtonName(name));
        Assert.False(DesktopCodexAutomation.IsSendButtonName("继续"));
        Assert.False(DesktopCodexAutomation.IsSendButtonName("停止"));
    }

    [Theory]
    [InlineData("第一行\r\n第二行", "第一行\n第二行")]
    [InlineData("桌面同步 V0.2.2", "桌面同步 V0.2.2")]
    public void ComposerVerificationAcceptsOnlyEquivalentNewlineNormalization(string actual, string expected)
    {
        Assert.True(DesktopCodexAutomation.ComposerTextEquals(actual, expected));
        Assert.False(DesktopCodexAutomation.ComposerTextEquals(actual + "x", expected));
    }

    [Fact]
    public void CanInspectCurrentDesktopWhenIntegrationIsRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CODEX_DESKTOP_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var inspection = new DesktopCodexAutomation().Inspect();

        Assert.True(inspection.Available, inspection.Error);
        Assert.False(string.IsNullOrWhiteSpace(inspection.ConversationTitle));
    }
}
