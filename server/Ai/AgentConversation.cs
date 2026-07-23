using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Slopterm.Server.Vault;

namespace Slopterm.Server.Ai;

/// <summary>
/// Per-SSH-session AI conversation state and the agentic loop. Constructed with (and owned by)
/// the <see cref="TerminalSession"/>; dies with it. Holds NO reference to the vault - the vault
/// is passed per-turn so a locked/unlocked transition is always observed fresh. One
/// <c>_stateLock</c> guards ALL of <c>_history</c>, <c>_transcript</c>, <c>_busy</c>,
/// <c>_generation</c> and <c>_currentCts</c>.
/// </summary>
public sealed class AgentConversation : IDisposable
{
    private readonly TerminalSession _session;
    private readonly object _stateLock = new();
    private readonly List<MessageParam> _history = [];   // model turns (always well-formed, ends with an assistant msg or empty)
    private readonly List<ChatMessage> _transcript = []; // display turns
    private bool _busy;
    private int _generation;                             // bumped by Clear() so an in-flight turn skips its commit
    private CancellationTokenSource? _currentCts;        // per-turn, standalone (not linked to the connection)

    public AgentConversation(TerminalSession session) => _session = session;

    /// <summary>
    /// Shallow copy is safe: an assistant message is only added to <c>_transcript</c> AFTER it
    /// stops mutating (in RunTurnAsync's finally), and user messages are immutable once created.
    /// </summary>
    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_stateLock)
        {
            return _transcript.ToList();
        }
    }

    public bool TryBeginTurn(out CancellationToken token)
    {
        lock (_stateLock)
        {
            if (_busy)
            {
                token = default;
                return false;
            }

            _currentCts = new CancellationTokenSource();
            token = _currentCts.Token;
            _busy = true;
            return true;
        }
    }

    public void CancelCurrent()
    {
        lock (_stateLock)
        {
            try
            {
                _currentCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void EndTurn()
    {
        lock (_stateLock)
        {
            _busy = false;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    /// <summary>
    /// Wipes both transcript and model history and cancels any in-flight turn. Bumping the
    /// generation first makes the running turn skip its commit and emit no turn_done (the empty
    /// history frame the caller sends already reset the client).
    /// </summary>
    public void Clear()
    {
        lock (_stateLock)
        {
            _generation++;
            _transcript.Clear();
            _history.Clear();
            try
            {
                _currentCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose() => CancelCurrent();

    public async Task RunTurnAsync(VaultService vault, string mode, string userText, Func<object, Task> emit, CancellationToken ct)
    {
        var assistantId = Guid.NewGuid().ToString("N");
        var assistant = new ChatMessage { Id = assistantId, Role = "assistant", Mode = mode };

        var userParam = new MessageParam
        {
            Role = Role.User,
            Content = new List<ContentBlockParam> { new TextBlockParam { Text = userText } },
        };

        int gen;
        List<MessageParam> baseHistory;
        lock (_stateLock)
        {
            gen = _generation;
            _transcript.Add(new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = "user",
                Text = userText,
                Mode = mode,
            });
            baseHistory = [.. _history];
        }

        var localHistory = new List<MessageParam>(baseHistory) { userParam };

        // BEFORE any credential resolution / client build, so the missing-credentials path still
        // produces a turn_start.
        await emit(new { type = "turn_start", id = assistantId, mode });

        var stopReason = "end_turn";
        string? error = null;
        try
        {
            // Credential gate + client build live INSIDE the try, so a missing key or a build
            // throw maps to stopReason "error" and still hits the finally (turn_done).
            var (_, ready) = AnthropicCredentials.ProbeSource(vault);
            if (!ready)
            {
                throw new InvalidOperationException(
                    "No Claude credentials. Add an API key in Settings or run \"ant auth login\".");
            }

            var client = AnthropicCredentials.BuildClient(vault);
            var tools = mode == "agent" ? AgentTools : ChatTools;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var createParams = new MessageCreateParams
                {
                    Model = Model.ClaudeOpus4_8,
                    MaxTokens = 16000,
                    Thinking = new ThinkingConfigAdaptive(),
                    OutputConfig = new OutputConfig { Effort = Effort.High },
                    System = SystemPrompt(),
                    Messages = localHistory,
                    Tools = tools,
                };

                var blocks = new List<StreamBlock>();
                var byIndex = new Dictionary<long, StreamBlock>();
                var streamStopRaw = "end_turn";

                await foreach (var ev in client.Messages.CreateStreaming(createParams, ct))
                {
                    if (ev.TryPickContentBlockStart(out var startEv))
                    {
                        blocks.Add(byIndex[startEv.Index] = StartBlock(startEv.ContentBlock));
                    }
                    else if (ev.TryPickContentBlockDelta(out var deltaEv))
                    {
                        byIndex.TryGetValue(deltaEv.Index, out var block);
                        var delta = deltaEv.Delta;
                        if (delta.TryPickText(out var textDelta))
                        {
                            assistant.Text += textDelta.Text;
                            await emit(new { type = "text_delta", id = assistantId, text = textDelta.Text });
                            if (block is not null)
                            {
                                block.Text.Append(textDelta.Text);
                            }
                        }
                        else if (delta.TryPickInputJson(out var jsonDelta))
                        {
                            block?.Json.Append(jsonDelta.PartialJson);
                        }
                        else if (delta.TryPickThinking(out var thinkingDelta))
                        {
                            block?.Text.Append(thinkingDelta.Thinking);
                        }
                        else if (delta.TryPickSignature(out var signatureDelta))
                        {
                            if (block is not null)
                            {
                                block.Signature = signatureDelta.Signature ?? block.Signature;
                            }
                        }
                    }
                    else if (ev.TryPickDelta(out var messageDelta))
                    {
                        var stopReasonEnum = messageDelta.Delta.StopReason;
                        if (stopReasonEnum is not null)
                        {
                            string? raw = stopReasonEnum; // ApiEnum -> raw wire string
                            if (!string.IsNullOrEmpty(raw))
                            {
                                streamStopRaw = raw;
                            }
                        }
                    }
                }

                // Rebuild this streamed turn as param blocks and append it to the model history.
                // Thinking blocks are kept here because the API requires them when a tool_use turn
                // is echoed back with thinking enabled; they are stripped again before the turn is
                // committed as future context (see the finally).
                var assistantContent = new List<ContentBlockParam>();
                var toolCalls = new List<ToolCall>();
                foreach (var block in blocks)
                {
                    switch (block.Kind)
                    {
                        case StreamBlockKind.Thinking:
                            if (!string.IsNullOrEmpty(block.Signature))
                            {
                                assistantContent.Add(new ThinkingBlockParam
                                {
                                    Thinking = block.Text.ToString(),
                                    Signature = block.Signature,
                                });
                            }

                            break;
                        case StreamBlockKind.RedactedThinking:
                            assistantContent.Add(new RedactedThinkingBlockParam { Data = block.Data });
                            break;
                        case StreamBlockKind.Text:
                            if (block.Text.Length > 0)
                            {
                                assistantContent.Add(new TextBlockParam { Text = block.Text.ToString() });
                            }

                            break;
                        case StreamBlockKind.ToolUse:
                            var input = ParseInput(block.Json.ToString());
                            toolCalls.Add(new ToolCall(block.ToolId, block.ToolName, input));
                            assistantContent.Add(new ToolUseBlockParam
                            {
                                ID = block.ToolId,
                                Name = block.ToolName,
                                Input = input,
                            });
                            break;
                    }
                }

                if (assistantContent.Count > 0)
                {
                    localHistory.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
                }

                if (streamStopRaw == "tool_use" && toolCalls.Count > 0)
                {
                    var results = new List<ContentBlockParam>();
                    foreach (var call in toolCalls)
                    {
                        var (summary, result) = await ExecuteToolAsync(mode, call, ct);
                        assistant.Activities.Add(new ChatActivity { Tool = call.Name, Summary = summary });
                        await emit(new { type = "tool_activity", id = assistantId, tool = call.Name, summary });
                        results.Add(new ToolResultBlockParam { ToolUseID = call.Id, Content = result });
                    }

                    localHistory.Add(new MessageParam { Role = Role.User, Content = results }); // all results, one message
                    continue;
                }

                stopReason = streamStopRaw == "refusal" ? "refusal" : "end_turn";
                break;
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = "stopped";
        }
        catch (Exception ex)
        {
            stopReason = "error";
            error = ex.Message;
        }
        finally
        {
            bool cleared;
            lock (_stateLock)
            {
                cleared = gen != _generation;
                if (!cleared)
                {
                    List<MessageParam> commit;
                    if (stopReason is "end_turn" or "refusal")
                    {
                        // Clean, well-formed conversation (every tool_use has its result).
                        commit = StripThinking(localHistory);
                    }
                    else
                    {
                        // stopped / error: keep the user turn + any streamed assistant TEXT only,
                        // never a dangling tool_use (which would 400 the next request).
                        commit = [.. baseHistory, userParam];
                        if (!string.IsNullOrEmpty(assistant.Text))
                        {
                            commit.Add(new MessageParam
                            {
                                Role = Role.Assistant,
                                Content = new List<ContentBlockParam> { new TextBlockParam { Text = assistant.Text } },
                            });
                        }
                    }

                    // Model history must always end with an assistant message (the API rejects a
                    // trailing user turn / consecutive user turns). If this turn produced no
                    // assistant content at all, drop it and keep the prior clean history.
                    if (EndsWithUser(commit))
                    {
                        commit = [.. baseHistory];
                    }

                    _history.Clear();
                    _history.AddRange(commit);
                    _transcript.Add(assistant); // show the (possibly partial) answer
                }
            }

            if (!cleared)
            {
                await emit(new { type = "turn_done", id = assistantId, stopReason, error });
            }
        }
    }

    private static StreamBlock StartBlock(RawContentBlockStartEventContentBlock contentBlock)
    {
        if (contentBlock.TryPickToolUse(out var toolUse))
        {
            return new StreamBlock { Kind = StreamBlockKind.ToolUse, ToolId = toolUse.ID ?? "", ToolName = toolUse.Name ?? "" };
        }

        if (contentBlock.TryPickThinking(out _))
        {
            return new StreamBlock { Kind = StreamBlockKind.Thinking };
        }

        if (contentBlock.TryPickRedactedThinking(out var redacted))
        {
            return new StreamBlock { Kind = StreamBlockKind.RedactedThinking, Data = redacted.Data ?? "" };
        }

        if (contentBlock.TryPickText(out _))
        {
            return new StreamBlock { Kind = StreamBlockKind.Text };
        }

        return new StreamBlock { Kind = StreamBlockKind.Other };
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    /// <summary>
    /// Removes thinking / redacted-thinking blocks from a to-be-committed history. They are only
    /// required WITHIN a turn's tool-use loop (kept in localHistory there); as future context they
    /// are unnecessary and dropping them avoids any cross-turn positioning constraints plus bloat.
    /// </summary>
    private static List<MessageParam> StripThinking(List<MessageParam> history)
    {
        var result = new List<MessageParam>(history.Count);
        foreach (var message in history)
        {
            if (!message.Content.TryPickContentBlockParams(out var contentBlocks))
            {
                result.Add(message);
                continue;
            }

            var kept = new List<ContentBlockParam>();
            foreach (var block in contentBlocks)
            {
                if (block.TryPickThinking(out _) || block.TryPickRedactedThinking(out _))
                {
                    continue;
                }

                kept.Add(block);
            }

            result.Add(new MessageParam { Role = message.Role, Content = kept });
        }

        return result;
    }

    private static bool EndsWithUser(List<MessageParam> history)
    {
        if (history.Count == 0)
        {
            return false;
        }

        Role role = history[^1].Role; // ApiEnum -> Role
        return role == Role.User;
    }

    // --- Tools ---------------------------------------------------------------------------------

    private async Task<(string Summary, string Result)> ExecuteToolAsync(string mode, ToolCall call, CancellationToken ct)
    {
        switch (call.Name)
        {
            case "read_terminal":
            {
                var maxLines = 120;
                if (call.Input.TryGetValue("maxLines", out var value) && value.ValueKind == JsonValueKind.Number)
                {
                    maxLines = value.GetInt32();
                }

                var text = AnsiText.Strip(_session.Scrollback.SnapshotTail(16 * 1024));
                return ("read recent output", LastLines(text, maxLines));
            }

            case "run_command":
            {
                if (mode != "agent")
                {
                    return ("blocked run_command (chat mode)", "Error: run_command is only available in agent mode.");
                }

                var command = GetString(call.Input, "command") ?? "";
                var before = _session.Scrollback.TotalWritten;
                _session.WriteToShell(command + "\r");
                await ReadUntilIdleAsync(ct);
                var output = AnsiText.Strip(_session.Scrollback.SnapshotSince(before));
                return ($"ran: {OneLine(command, 80)}", output);
            }

            case "type_text":
            {
                if (mode != "agent")
                {
                    return ("blocked type_text (chat mode)", "Error: type_text is only available in agent mode.");
                }

                var text = GetString(call.Input, "text") ?? "";
                var before = _session.Scrollback.TotalWritten;
                _session.WriteToShell(text); // no newline - for prompts / pagers
                await Task.Delay(400, ct);
                var output = AnsiText.Strip(_session.Scrollback.SnapshotSince(before));
                return ($"typed: {OneLine(text, 80)}", output);
            }

            case "wait":
            {
                if (mode != "agent")
                {
                    return ("blocked wait (chat mode)", "Error: wait is only available in agent mode.");
                }

                var seconds = 3;
                if (call.Input.TryGetValue("seconds", out var value) && value.ValueKind == JsonValueKind.Number)
                {
                    seconds = value.GetInt32();
                }

                seconds = Math.Clamp(seconds, 1, 60);
                var before = _session.Scrollback.TotalWritten;
                await Task.Delay(seconds * 1000, ct);
                var output = AnsiText.Strip(_session.Scrollback.SnapshotSince(before));
                return ($"waited {seconds}s", output);
            }

            default:
                return ($"unknown tool: {call.Name}", $"Error: unknown tool '{call.Name}'.");
        }
    }

    /// <summary>
    /// Best-effort "command finished" signal against a raw PTY with no exit-code channel: poll the
    /// byte counter every 250ms, stop once it has been quiet for ~750ms, hard-capped at ~15s (and
    /// honoring <paramref name="ct"/>). Long-running/interactive commands return partial output;
    /// the model can call read_terminal / wait again.
    /// </summary>
    private async Task ReadUntilIdleAsync(CancellationToken ct)
    {
        const int pollMs = 250;
        const int idleThresholdMs = 750;
        const int hardCapMs = 15_000;

        var start = Environment.TickCount64;
        var last = _session.Scrollback.TotalWritten;
        var lastChange = start;

        while (true)
        {
            await Task.Delay(pollMs, ct);
            var now = Environment.TickCount64;
            var current = _session.Scrollback.TotalWritten;

            if (current != last)
            {
                last = current;
                lastChange = now;
            }
            else if (now - lastChange >= idleThresholdMs)
            {
                break;
            }

            if (now - start >= hardCapMs)
            {
                break;
            }
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> input, string key)
        => input.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string OneLine(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : string.Concat(flat.AsSpan(0, max), "…");
    }

    private static string LastLines(string text, int maxLines)
    {
        if (maxLines <= 0)
        {
            return "";
        }

        var lines = text.Split('\n');
        return lines.Length <= maxLines ? text : string.Join('\n', lines[^maxLines..]);
    }

    private string SystemPrompt() =>
        $"""
        You are an AI assistant embedded in a live SSH terminal session connected to {_session.Username}@{_session.Host}:{_session.Port}.
        You can read the recent terminal output with the read_terminal tool.
        In agent mode you can also run commands (run_command), type text without a trailing newline (type_text, for prompts and pagers), and pause for output to settle (wait). Everything you send appears in the user's real terminal, which they are watching live - they see every keystroke land.
        Be concise. Prefer reading recent output or waiting to observe results before continuing. Confirm the user's intent before running destructive or irreversible commands. Never invent command output - use read_terminal to see what actually happened.
        """;

    private static readonly IReadOnlyList<ToolUnion> ChatTools = BuildTools(agent: false);
    private static readonly IReadOnlyList<ToolUnion> AgentTools = BuildTools(agent: true);

    private static IReadOnlyList<ToolUnion> BuildTools(bool agent)
    {
        var readTerminal = new Tool
        {
            Name = "read_terminal",
            Description = "Read the most recent output from the SSH terminal session (ANSI escapes stripped).",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["maxLines"] = JsonSerializer.SerializeToElement(
                        new { type = "integer", description = "Maximum number of trailing lines to return (default 120)." }),
                },
                Required = [],
            },
        };

        if (!agent)
        {
            return new List<ToolUnion> { readTerminal };
        }

        var runCommand = new Tool
        {
            Name = "run_command",
            Description = "Type a command into the terminal, run it (appends a newline), and return the output it produced.",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["command"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "The shell command to run. No trailing newline needed." }),
                },
                Required = ["command"],
            },
        };

        var typeText = new Tool
        {
            Name = "type_text",
            Description = "Type raw text into the terminal WITHOUT a trailing newline - for answering prompts or driving pagers.",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["text"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "The exact text to type (e.g. \"y\", or a control sequence)." }),
                },
                Required = ["text"],
            },
        };

        var wait = new Tool
        {
            Name = "wait",
            Description = "Pause for a number of seconds to let a long-running command make progress, then return any new output.",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["seconds"] = JsonSerializer.SerializeToElement(
                        new { type = "integer", description = "How many seconds to wait (1-60)." }),
                },
                Required = ["seconds"],
            },
        };

        return new List<ToolUnion> { readTerminal, runCommand, typeText, wait };
    }

    private enum StreamBlockKind
    {
        Text,
        ToolUse,
        Thinking,
        RedactedThinking,
        Other,
    }

    private sealed class StreamBlock
    {
        public StreamBlockKind Kind { get; init; }
        public StringBuilder Text { get; } = new();
        public StringBuilder Json { get; } = new();
        public string ToolId { get; init; } = "";
        public string ToolName { get; init; } = "";
        public string Signature { get; set; } = "";
        public string Data { get; init; } = "";
    }

    private sealed record ToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input);
}
