using System.Runtime.CompilerServices;
using MyGame.Core.AI;

namespace MyGame.Tests.AI;

/// <summary>
/// Test double for <see cref="IAiClient"/>. Returns canned
/// <see cref="ChatResponse"/> objects in FIFO order, counts calls, and
/// never makes a real HTTP request. Used by the multiplayer integration
/// tests (issue #57) to drive the GameMaster through a deterministic
/// turn without contacting an LLM provider.
///
/// <para>
/// <b>Construction:</b> pass either a fixed list of canned responses
/// (each call dequeues the next; throws when the queue is empty) or a
/// single response that's returned on every call. The
/// <see cref="Settings"/> property defaults to a minimal
/// <see cref="AiSettings"/>; pass your own if the code under test reads
/// <see cref="IAiClient.Settings"/> (e.g. for role-specific model
/// derivation).
/// </para>
///
/// <para>
/// <b>Streaming behaviour:</b>
/// <see cref="StreamChatWithToolsAsync"/> yields the canned
/// <see cref="ChatResponse.Content"/> as a single delta and then writes
/// the full <see cref="ChatResponse"/> into the supplied
/// <see cref="StreamChatResult"/>. This terminates the GameMaster's
/// tool-call loop on iteration 1 (no tool calls → loop exits), so the
/// stub can be used to drive a complete GM turn with one canned
/// response.
/// </para>
///
/// <para>
/// <see cref="WithModel"/> returns <c>this</c> — model overrides are
/// meaningless for a stub returning canned responses, and returning the
/// same instance matches the production <see cref="AiClient.WithModel"/>
/// no-op-when-same-model contract.
/// </para>
/// </summary>
public sealed class StubAiClient : IAiClient
{
    private readonly Queue<ChatResponse> _responses;
    private readonly AiSettings _settings;
    private int _chatWithToolsCallCount;
    private int _chatCallCount;
    private int _streamChatWithToolsCallCount;
    private int _streamChatCallCount;

    /// <summary>
    /// Create a stub that returns the given responses in order, one per
    /// call to <see cref="ChatAsync"/> /
    /// <see cref="ChatWithToolsAsync"/> /
    /// <see cref="StreamChatWithToolsAsync"/> /
    /// <see cref="StreamChatAsync"/>. The queue is shared across all
    /// four methods (each call dequeues the next response regardless of
    /// which method was called). Throws <see cref="InvalidOperationException"/>
    /// if a call is made after the queue is empty.
    /// </summary>
    /// <param name="responses">Canned responses, returned in FIFO order.</param>
    /// <param name="settings">Optional settings exposed via
    /// <see cref="Settings"/>. Defaults to a minimal
    /// <see cref="AiSettings"/> with a placeholder model id.</param>
    public StubAiClient(IEnumerable<ChatResponse> responses, AiSettings? settings = null)
    {
        _responses = new Queue<ChatResponse>(responses ?? throw new ArgumentNullException(nameof(responses)));
        _settings = settings ?? new AiSettings { Model = "stub-model", ApiKey = "stub-key" };
    }

    /// <summary>
    /// Convenience: create a stub that always returns the same response.
    /// Equivalent to passing a single-element queue, but the response is
    /// re-enqueued after each call so the stub never runs out.
    /// </summary>
    public StubAiClient(ChatResponse response, AiSettings? settings = null)
        : this(Enumerable.Repeat(response, 1), settings)
    {
        _repeatForever = response;
    }

    private readonly ChatResponse? _repeatForever;

    /// <summary>
    /// The settings exposed via <see cref="IAiClient.Settings"/>. Tests
    /// can read this to assert on what the code under test passed in,
    /// and the code under test can read it for role-specific model
    /// derivation (returns the stub's settings, which don't affect the
    /// canned responses).
    /// </summary>
    public AiSettings Settings => _settings;

    /// <summary>Number of times <see cref="ChatWithToolsAsync"/> was called.</summary>
    public int ChatWithToolsCallCount => Volatile.Read(ref _chatWithToolsCallCount);

    /// <summary>Number of times <see cref="ChatAsync"/> was called.</summary>
    public int ChatCallCount => Volatile.Read(ref _chatCallCount);

    /// <summary>Number of times <see cref="StreamChatWithToolsAsync"/> was called.</summary>
    public int StreamChatWithToolsCallCount => Volatile.Read(ref _streamChatWithToolsCallCount);

    /// <summary>Number of times <see cref="StreamChatAsync"/> was called.</summary>
    public int StreamChatCallCount => Volatile.Read(ref _streamChatCallCount);

    /// <summary>
    /// Total calls across all four methods. Useful for asserting "the
    /// GM made exactly one AI call this turn" without caring which
    /// overload was used.
    /// </summary>
    public int TotalCallCount => ChatWithToolsCallCount + ChatCallCount
        + StreamChatWithToolsCallCount + StreamChatCallCount;

    /// <summary>
    /// Returns <c>this</c>. Model overrides are meaningless for a stub
    /// returning canned responses, and returning the same instance
    /// matches the production AiClient.WithModel no-op-when-same-model
    /// contract.
    /// </summary>
    public IAiClient WithModel(string? model) => this;

    /// <summary>
    /// Dequeue the next canned response. Throws if the queue is empty
    /// (unless the stub was constructed via the single-response
    /// convenience ctor, in which case the response is re-enqueued
    /// forever).
    /// </summary>
    public Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _chatCallCount);
        return Task.FromResult(NextResponse());
    }

    /// <summary>
    /// Dequeue the next canned response. The <paramref name="tools"/>
    /// argument is ignored — the stub doesn't generate tool calls unless
    /// the canned response carries them.
    /// </summary>
    public Task<ChatResponse> ChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _chatWithToolsCallCount);
        return Task.FromResult(NextResponse());
    }

    /// <summary>
    /// Yield the canned response's content (if any) as a single delta.
    /// Token counts and tool calls are NOT surfaced via this streaming
    /// overload — use <see cref="StreamChatWithToolsAsync"/> for those.
    /// </summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Interlocked.Increment(ref _streamChatCallCount);
        var response = NextResponse();
        if (!string.IsNullOrEmpty(response.Content))
            yield return response.Content;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Yield the canned response's content (if any) as a single delta
    /// and write the full <see cref="ChatResponse"/> (content + tool
    /// calls + token counts + finish reason) into <paramref name="result"/>.
    /// The GameMaster's tool-call loop sees the response, processes any
    /// tool calls, and either continues (if tool calls were present) or
    /// terminates (if not). A canned response with no tool calls
    /// terminates the loop on iteration 1.
    /// </summary>
    public async IAsyncEnumerable<string> StreamChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        StreamChatResult result,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        Interlocked.Increment(ref _streamChatWithToolsCallCount);
        var response = NextResponse();
        result.Response = response;
        if (!string.IsNullOrEmpty(response.Content))
            yield return response.Content;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Dequeue the next response. If the stub was constructed via the
    /// single-response convenience ctor, the response is re-enqueued
    /// forever (so the stub never runs out). Otherwise throws when the
    /// queue is empty — that's a test bug (the code under test made
    /// more AI calls than the test set up canned responses for).
    /// </summary>
    private ChatResponse NextResponse()
    {
        if (_repeatForever is not null)
            return _repeatForever;
        lock (_responses)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException(
                    "StubAiClient response queue is empty. The code under test made " +
                    "more AI calls than the test supplied canned responses for.");
            return _responses.Dequeue();
        }
    }
}
