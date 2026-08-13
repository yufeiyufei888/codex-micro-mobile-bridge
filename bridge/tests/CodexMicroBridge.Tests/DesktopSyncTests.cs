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

    [Fact]
    public void DetectsAndReadsAssistantMessageAppendedAfterInitialSessionScan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-micro-polling-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-12T03:00:00Z",
                type = "event_msg",
                payload = new { type = "user_message", message = "快速回复测试" },
            }) + Environment.NewLine);
            var reader = new DesktopSessionReader();
            var before = reader.ReadFileStamp(path);

            File.AppendAllText(path, JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-12T03:00:00.200Z",
                type = "response_item",
                payload = new
                {
                    type = "message",
                    id = "assistant-fast",
                    role = "assistant",
                    content = new[] { new { type = "output_text", text = "收到。" } },
                    internal_chat_message_metadata_passthrough = new { turn_id = "turn-fast" },
                },
            }) + Environment.NewLine);

            var after = reader.ReadFileStamp(path);
            var messages = reader.ReadConversationMessages(path);

            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.NotEqual(before, after);
            var reply = Assert.Single(messages, message => message.Role == "assistant");
            Assert.Equal("assistant-fast", reply.MessageId);
            Assert.Equal("收到。", reply.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LivePollingWaitsForCanonicalMessageInsteadOfPersistingEventMirrorTwice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-micro-live-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-12T03:10:00Z",
                type = "event_msg",
                payload = new { type = "agent_message", message = "唯一回复" },
            }) + Environment.NewLine);
            var reader = new DesktopSessionReader();

            Assert.Empty(reader.ReadCanonicalLiveMessages(path));

            File.AppendAllText(path, JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-12T03:10:00.050Z",
                type = "response_item",
                payload = new
                {
                    type = "message",
                    id = "assistant-canonical",
                    role = "assistant",
                    content = new[] { new { type = "output_text", text = "唯一回复" } },
                    internal_chat_message_metadata_passthrough = new { turn_id = "turn-canonical" },
                },
            }) + Environment.NewLine);

            var message = Assert.Single(reader.ReadCanonicalLiveMessages(path));
            Assert.Equal("assistant-canonical", message.MessageId);
            Assert.Equal("唯一回复", message.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IncrementalReaderRecoversARecordCompletedAfterThePreviousFileStamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-micro-incremental-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "{\"timestamp\":\"2026-08-12T05:20:25Z\",\"type\":\"response_item\",\"payload\":");
            var previousLength = new FileInfo(path).Length;
            File.AppendAllText(path,
                "{\"type\":\"message\",\"id\":\"assistant-appended\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"收到。\"}],\"internal_chat_message_metadata_passthrough\":{\"turn_id\":\"turn-appended\"}}}" +
                Environment.NewLine);

            var messages = new DesktopSessionReader().ReadCanonicalMessagesSince(path, previousLength);

            var message = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<DesktopSessionMessage>>(messages));
            Assert.Equal("assistant-appended", message.MessageId);
            Assert.Equal("收到。", message.Text);
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
    public void ApprovalSummaryRemovesDesktopChromeFragments()
    {
        var summary = DesktopCodexAutomation.BuildApprovalSummary(
        [
            "确认",
            "权限",
            "确认",
            "审批",
            "权限",
            "待批准",
            "Computer Use",
            "允许 ChatGPT 使用 notepad?",
        ]);

        Assert.Equal("允许 ChatGPT 使用 notepad?", summary);
    }

    [Fact]
    public void ApprovalSummaryNormalizesWhitespaceAndDuplicates()
    {
        var summary = DesktopCodexAutomation.BuildApprovalSummary(
        [
            "  允许   ChatGPT 使用 notepad?  ",
            "允许 ChatGPT 使用 notepad?",
        ]);

        Assert.Equal("允许 ChatGPT 使用 notepad?", summary);
    }

    [Fact]
    public void DesktopSessionBindingFollowsOnlyANewerDifferentRootConversation()
    {
        var current = new DesktopSessionFileStamp(100, DateTime.Parse("2026-08-12T05:00:00Z").ToUniversalTime());
        var older = new DesktopSessionFileStamp(200, current.LastWriteTimeUtc.AddSeconds(-1));
        var newer = new DesktopSessionFileStamp(200, current.LastWriteTimeUtc.AddSeconds(1));

        Assert.False(BridgeRuntime.ShouldFollowDesktopSession("current.jsonl", current, "current.jsonl", newer));
        Assert.False(BridgeRuntime.ShouldFollowDesktopSession("current.jsonl", current, "older.jsonl", older));
        Assert.True(BridgeRuntime.ShouldFollowDesktopSession("current.jsonl", current, "newer.jsonl", newer));
        Assert.True(BridgeRuntime.ShouldFollowDesktopSession(null, null, "newer.jsonl", newer));
    }

    [Fact]
    public void ExactPhonePromptRemainsDiscoverableBehindManyNewerSubagentRollouts()
    {
        var directory = Directory.CreateTempSubdirectory("codex-micro-sessions-");
        try
        {
            var baseline = DateTime.UtcNow.AddMinutes(-1);
            var rootPath = Path.Combine(directory.FullName, "root.jsonl");
            WriteSessionMeta(rootPath, parentThreadId: null, source: "cli", baseline);
            AppendCanonicalMessage(rootPath, "phone-prompt", "user", "手机精确提示", "turn-phone", baseline.AddSeconds(1));
            File.SetLastWriteTimeUtc(rootPath, baseline.AddSeconds(1));

            for (var index = 0; index < 48; index++)
            {
                var subagentPath = Path.Combine(directory.FullName, $"subagent-{index:D2}.jsonl");
                WriteSessionMeta(subagentPath, "parent", "subagent", baseline.AddSeconds(2 + index));
                File.SetLastWriteTimeUtc(subagentPath, baseline.AddSeconds(2 + index));
            }

            var reader = new DesktopSessionReader(directory.FullName);
            var matched = reader.FindSessionContainingPrompt("手机精确提示", new DateTimeOffset(baseline));

            Assert.Equal(rootPath, matched);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ActiveRootSessionRemainsDiscoverableWhileCodexKeepsItOpenForAppend()
    {
        var directory = Directory.CreateTempSubdirectory("codex-micro-live-session-");
        try
        {
            var baseline = DateTime.UtcNow.AddSeconds(-10);
            var rootPath = Path.Combine(directory.FullName, "active-root.jsonl");
            WriteSessionMeta(rootPath, parentThreadId: null, source: "user", baseline);
            AppendCanonicalMessage(rootPath, "phone-live", "user", "正在写入的手机消息", "turn-live", baseline.AddSeconds(1));

            using var activeWriter = new FileStream(
                rootPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            activeWriter.Seek(0, SeekOrigin.End);

            var reader = new DesktopSessionReader(directory.FullName);

            Assert.Equal(
                rootPath,
                reader.FindSessionContainingPrompt("正在写入的手机消息", new DateTimeOffset(baseline)));
            Assert.Equal(
                rootPath,
                reader.FindMostRecentRootSession(new DateTimeOffset(baseline.AddMinutes(-1))));

            using var liveTextWriter = new StreamWriter(activeWriter, leaveOpen: true) { AutoFlush = true };
            liveTextWriter.WriteLine(JsonSerializer.Serialize(new
            {
                timestamp = baseline.AddSeconds(2).ToString("O"),
                type = "response_item",
                payload = new
                {
                    type = "message",
                    id = "assistant-live",
                    role = "assistant",
                    content = new[] { new { type = "output_text", text = "占用期间仍能同步回复" } },
                    internal_chat_message_metadata_passthrough = new { turn_id = "turn-live" },
                },
            }));

            Assert.Contains(
                reader.ReadCanonicalLiveMessages(rootPath),
                message => message.MessageId == "assistant-live" && message.Text == "占用期间仍能同步回复");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void EveryAssistantReplyIsVisibleOnTheSameFileChangeWithoutAnotherPhonePrompt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-micro-cycle-{Guid.NewGuid():N}.jsonl");
        try
        {
            var at = DateTime.UtcNow.AddMinutes(-1);
            WriteSessionMeta(path, parentThreadId: null, source: "cli", at);
            AppendCanonicalMessage(path, "user-1", "user", "消息一", "turn-1", at.AddSeconds(1));
            AppendCanonicalMessage(path, "assistant-1", "assistant", "回复一", "turn-1", at.AddSeconds(2));

            var reader = new DesktopSessionReader();
            var firstPoll = reader.ReadCanonicalLiveMessages(path);
            Assert.Contains(firstPoll, message => message.MessageId == "assistant-1" && message.Text == "回复一");

            AppendCanonicalMessage(path, "user-2", "user", "消息二", "turn-2", at.AddSeconds(3));
            AppendCanonicalMessage(path, "assistant-2", "assistant", "回复二", "turn-2", at.AddSeconds(4));

            var secondPoll = reader.ReadCanonicalLiveMessages(path);
            Assert.Contains(secondPoll, message => message.MessageId == "assistant-2" && message.Text == "回复二");
        }
        finally
        {
            File.Delete(path);
        }
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

    [Fact]
    public void CanReadExpectedMessageFromCurrentDesktopSessionWhenIntegrationIsRequested()
    {
        var path = Environment.GetEnvironmentVariable("CODEX_DESKTOP_SESSION_PATH");
        var offsetText = Environment.GetEnvironmentVariable("CODEX_DESKTOP_SESSION_OFFSET");
        var expectedMessageId = Environment.GetEnvironmentVariable("CODEX_DESKTOP_EXPECTED_MESSAGE_ID");
        if (string.IsNullOrWhiteSpace(path) ||
            !long.TryParse(offsetText, out var offset) ||
            string.IsNullOrWhiteSpace(expectedMessageId))
        {
            return;
        }

        var messages = new DesktopSessionReader().ReadCanonicalMessagesSince(path, offset);

        Assert.NotNull(messages);
        Assert.Contains(messages, message => string.Equals(message.MessageId, expectedMessageId, StringComparison.Ordinal));
    }

    private static void WriteSessionMeta(
        string path,
        string? parentThreadId,
        string source,
        DateTime timestamp)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "session_meta",
            payload = new
            {
                id = Path.GetFileNameWithoutExtension(path),
                parent_thread_id = parentThreadId,
                thread_source = source,
            },
        }) + Environment.NewLine);
    }

    private static void AppendCanonicalMessage(
        string path,
        string messageId,
        string role,
        string text,
        string turnId,
        DateTime timestamp)
    {
        File.AppendAllText(path, JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "response_item",
            payload = new
            {
                type = "message",
                id = messageId,
                role,
                content = new[] { new { type = role == "user" ? "input_text" : "output_text", text } },
                internal_chat_message_metadata_passthrough = new { turn_id = turnId },
            },
        }) + Environment.NewLine);
    }
}
