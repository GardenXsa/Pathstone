using System.Net;
using System.Net.Http;
using System.Text;
using MyGame.Core.AI;

namespace MyGame.Tests.AI;

/// <summary>
/// Unit tests for <see cref="AiClient.WithModel"/> (issue #26). Verifies
/// that the derived client uses the new model id while sharing the
/// underlying <see cref="HttpClient"/> (no new socket pool).
/// </summary>
public class AiClientWithModelTests
{
    [Fact]
    public void WithModel_ChangesModelOnDerivedClient()
    {
        // The derived client's Settings.Model must match the requested
        // override, not the base client's model.
        var baseClient = new AiClient(new AiSettings
        {
            Model = "gpt-4o-mini",
            ApiKey = "sk-test",
        });

        var derived = baseClient.WithModel("gpt-4o");

        Assert.Equal("gpt-4o-mini", baseClient.Settings.Model); // unchanged
        Assert.Equal("gpt-4o",       derived.Settings.Model);   // overridden
    }

    [Fact]
    public void WithModel_NullOrWhitespace_ReturnsSameInstance()
    {
        // When the requested model is null/empty/whitespace, WithModel
        // returns the SAME client instance (no allocation). This makes
        // the pattern `ai.WithModel(roleOverride ?? ai.Settings.Model)`
        // a no-op when no override is set.
        var baseClient = new AiClient(new AiSettings
        {
            Model = "gpt-4o-mini",
            ApiKey = "sk-test",
        });

        Assert.Same(baseClient, baseClient.WithModel(null));
        Assert.Same(baseClient, baseClient.WithModel(""));
        Assert.Same(baseClient, baseClient.WithModel("   "));
    }

    [Fact]
    public void WithModel_SameModel_ReturnsSameInstance()
    {
        // When the requested model matches the base client's model,
        // WithModel returns the same instance (no allocation). This is
        // the common case when no override is set: GetModelForRole
        // returns Model, and WithModel(Model) is a no-op.
        var baseClient = new AiClient(new AiSettings
        {
            Model = "gpt-4o-mini",
            ApiKey = "sk-test",
        });

        Assert.Same(baseClient, baseClient.WithModel("gpt-4o-mini"));
    }

    [Fact]
    public void WithModel_PreservesOtherSettings()
    {
        // The derived client inherits ALL other settings from the base
        // (BaseUrl, ApiKey, Temperature, MaxTokens) — only Model changes.
        var baseClient = new AiClient(new AiSettings
        {
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "sk-test",
            Model = "deepseek-chat",
            Temperature = 0.5,
            MaxTokens = 1000,
        });

        var derived = baseClient.WithModel("deepseek-reasoner");

        Assert.Equal("https://api.deepseek.com/v1", derived.Settings.BaseUrl);
        Assert.Equal("sk-test",                     derived.Settings.ApiKey);
        Assert.Equal(0.5,                           derived.Settings.Temperature);
        Assert.Equal(1000,                          derived.Settings.MaxTokens);
        Assert.Equal("deepseek-reasoner",           derived.Settings.Model);
    }

    [Fact]
    public async Task WithModel_DerivedClientSendsOverriddenModelInRequestBody()
    {
        // End-to-end: when the derived client makes an HTTP call, the
        // request body's `model` field must be the overridden value
        // (not the base client's model). We verify by intercepting the
        // request with a stub HttpMessageHandler and inspecting the JSON.
        var handler = new CapturingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434/v1/"),
        };
        var baseClient = new AiClient(
            new AiSettings { Model = "gpt-4o-mini", ApiKey = "sk-test" },
            http);

        var derived = baseClient.WithModel("gpt-4o");
        try
        {
            await derived.ChatAsync(new[]
            {
                ChatMessage.System("test"),
                ChatMessage.User("hi"),
            }, CancellationToken.None);
        }
        catch (AiException)
        {
            // The stub returns a 200 with an empty body, which the
            // AiClient's parser will reject as a Parse error. We don't
            // care about the response — we only want to inspect the
            // request body that was sent.
        }

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"model\":\"gpt-4o\"", handler.LastRequestBody);
        Assert.DoesNotContain("\"model\":\"gpt-4o-mini\"", handler.LastRequestBody);
    }

    /// <summary>
    /// Minimal HttpMessageHandler that captures the request body and
    /// returns a 200 with an empty JSON object body. The body is irrelevant
    /// for the model-override tests — we only care about what was sent.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is HttpContent content)
            {
                LastRequestBody = content.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter().GetResult();
            }
            // Return a 200 with an empty-but-valid JSON object so the
            // AiClient's parser doesn't blow up before we get to inspect
            // the captured request body. The parser will still likely
            // raise a Parse AiException (no choices[0]) — the test
            // catches that.
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
