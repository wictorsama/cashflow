using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CashFlow.Assistant.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashFlow.Assistant.Tests;

/// <summary>What the validation refuses (the defence that does not depend on the model), and the Claude request and answer.</summary>
public sealed class InterpreterAndValidatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    private static RawInterpretation Draft(string? type = "Credit", decimal? amount = 100m, string? date = "2026-11-17", string? description = "Sale") =>
        new(RawInterpretation.Draft, type, amount, date, description, null);

    // ----- validation of a draft

    [Fact]
    public void AGoodDraft_PassesUnchanged()
    {
        var result = DraftValidator.Validate(Draft(), Today, "test");

        Assert.Equal(DraftStatus.Draft, result.Status);
        Assert.Equal(new EntryDraft("Credit", 100m, Today, "Sale"), result.Draft);
    }

    [Theory]
    [InlineData("Transfer", 100, "2026-11-17")] // not a type the system has
    [InlineData(null, 100, "2026-11-17")]
    [InlineData("Credit", 0, "2026-11-17")]
    [InlineData("Credit", -5, "2026-11-17")]
    [InlineData("Credit", 10.123, "2026-11-17")] // more than two decimals
    [InlineData("Credit", 100, "17/11/2026")] // not the agreed date format
    [InlineData("Credit", 100, null)]
    [InlineData("Credit", 100, "2026-11-18")] // tomorrow
    [InlineData("Credit", 100, "2025-11-16")] // more than a year ago
    [InlineData("Credit", 20000000, "2026-11-17")] // implausible: confirm first
    public void ADraftBreakingARule_IsNeverShownAsADraft(string? type, double amount, string? date)
    {
        var result = DraftValidator.Validate(Draft(type, (decimal)amount, date), Today, "test");

        Assert.Equal(DraftStatus.NeedsClarification, result.Status);
        Assert.Null(result.Draft);
        Assert.False(string.IsNullOrWhiteSpace(result.Question));
    }

    [Fact]
    public void AMissingAmount_AsksForIt()
    {
        var result = DraftValidator.Validate(Draft(amount: null), Today, "test");

        Assert.Equal(DraftStatus.NeedsClarification, result.Status);
    }

    [Fact]
    public void TheDescription_IsCleanedAndCapped()
    {
        var long200 = new string('a', 300);

        var result = DraftValidator.Validate(Draft(description: $"  hello\u0000\u0007\n\tworld  {long200}"), Today, "test");

        Assert.Equal(DraftStatus.Draft, result.Status);
        Assert.DoesNotContain(result.Draft!.Description!, char.IsControl);
        Assert.StartsWith("hello world ", result.Draft.Description, StringComparison.Ordinal);
        Assert.True(result.Draft.Description!.Length <= DraftValidator.MaxDescriptionLength);
    }

    [Fact]
    public void ABlankDescription_BecomesNone()
    {
        Assert.Null(DraftValidator.Validate(Draft(description: "   "), Today, "test").Draft!.Description);
    }

    [Fact]
    public void ARefusal_AndAnUnknownOutcome_AreRejected_AndAQuestionIsKept()
    {
        Assert.Equal(DraftStatus.Rejected, DraftValidator.Validate(RawInterpretation.Refuse(), Today, "test").Status);
        Assert.Equal(DraftStatus.Rejected, DraftValidator.Validate(new RawInterpretation("invalid", null, null, null, null, null), Today, "test").Status);
        Assert.Equal(DraftStatus.Rejected, DraftValidator.Validate(new RawInterpretation("execute", null, null, null, null, null), Today, "test").Status);

        var ask = DraftValidator.Validate(RawInterpretation.Ask("Qual o valor?"), Today, "test");
        Assert.Equal("Qual o valor?", ask.Question);
    }

    [Fact]
    public void AQuestionThatIsEmptyOrTooLong_IsReplacedByAGenericOne()
    {
        Assert.NotEmpty(DraftValidator.Validate(RawInterpretation.Ask("  "), Today, "t").Question!);
        Assert.True(DraftValidator.Validate(RawInterpretation.Ask(new string('x', 5000)), Today, "t").Question!.Length <= 200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void NoText_IsNotUsable(string? text) => Assert.Null(DraftValidator.CleanInput(text));

    [Fact]
    public void TheText_IsCleaned_AndALongOneIsNotUsable()
    {
        Assert.Equal("paguei 10 hoje", DraftValidator.CleanInput("  paguei\u0000 10\r\n hoje "));
        Assert.Null(DraftValidator.CleanInput(new string('a', DraftValidator.MaxInputLength + 1)));
        Assert.NotNull(DraftValidator.CleanInput(new string('a', DraftValidator.MaxInputLength)));
    }

    // ----- the service

    private sealed class FixedInterpreter(RawInterpretation answer) : IEntryInterpreter
    {
        public string Name => "fixed";

        public int Calls { get; private set; }

        public Task<RawInterpretation> InterpretAsync(string text, DateOnly today, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(answer);
        }
    }

    private sealed class DownInterpreter : IEntryInterpreter
    {
        public string Name => "down";

        public Task<RawInterpretation> InterpretAsync(string text, DateOnly today, CancellationToken cancellationToken) =>
            throw new AssistantUnavailableException("down");
    }

    [Fact]
    public async Task AModelThatIsManipulatedIntoAnAbsurdDraft_IsStoppedByTheValidation()
    {
        // The interpreter says "draft" with a huge amount and a future date, as a prompt injection might make it do.
        var hostile = new FixedInterpreter(Draft(amount: 999_999_999m, date: "2099-01-01"));
        var service = new DraftService(hostile, TimeProvider.System, NullLogger<DraftService>.Instance);

        var result = await service.DraftAsync("anything", Today, default);

        Assert.NotEqual(DraftStatus.Draft, result.Status);
    }

    [Fact]
    public async Task TextThatIsNotUsable_NeverReachesTheProvider()
    {
        var provider = new FixedInterpreter(Draft());
        var service = new DraftService(provider, TimeProvider.System, NullLogger<DraftService>.Instance);

        var result = await service.DraftAsync(new string('a', 5000), Today, default);

        Assert.Equal(DraftStatus.Rejected, result.Status);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AProviderThatIsDown_IsReportedAsUnavailable_NotAsABadDraft()
    {
        var service = new DraftService(new DownInterpreter(), TimeProvider.System, NullLogger<DraftService>.Instance);

        await Assert.ThrowsAsync<AssistantUnavailableException>(() => service.DraftAsync("paguei 10", Today, default));
    }

    [Fact]
    public void TodayForTheMerchant_FollowsSaoPauloTime()
    {
        // 01:30 UTC on the 17th is still the 16th in Sao Paulo.
        Assert.Equal(new DateOnly(2026, 11, 16), DraftService.TodayIn(new DateTimeOffset(2026, 11, 17, 1, 30, 0, TimeSpan.Zero)));
    }

    // ----- Claude: the request and the answer

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ToolAnswer(string input, int inputTokens = 120, int outputTokens = 40) =>
        $$$"""{"content":[{"type":"tool_use","name":"record_entry_draft","input":{{{input}}}}],"usage":{"input_tokens":{{{inputTokens}}},"output_tokens":{{{outputTokens}}}}}""";

    private static ClaudeInterpreter Claude(ScriptedHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), new ClaudeOptions { ApiKey = "test-key", Model = "claude-test", Timeout = timeout ?? TimeSpan.FromSeconds(4) });

    [Fact]
    public async Task TheRequest_SendsOnlyTheSentence_WithAForcedToolCall_AndTheKeyInAHeader()
    {
        var handler = new ScriptedHandler(_ => Json(ToolAnswer("""{"outcome":"draft","type":"Debit","amount":150,"date":"2026-11-16","description":"Luz"}""")));

        var result = await Claude(handler).InterpretAsync("paguei 150 de luz ontem", Today, default);

        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("test-key", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("claude-test", body["model"]!.GetValue<string>());
        Assert.Equal("tool", body["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal(ClaudeInterpreter.ToolName, body["tool_choice"]!["name"]!.GetValue<string>());
        Assert.Equal("<user_text>paguei 150 de luz ontem</user_text>", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Contains("2026-11-17", body["system"]!.GetValue<string>(), StringComparison.Ordinal); // today, so "ontem" can be resolved
        Assert.DoesNotContain("merchant", handler.Body!, StringComparison.OrdinalIgnoreCase); // nothing about who is asking

        Assert.Equal("draft", result.Outcome);
        Assert.Equal("Debit", result.Type);
        Assert.Equal(150m, result.Amount);
        Assert.Equal(160, result.InputTokens + result.OutputTokens);
    }

    [Fact]
    public async Task AnAmountSentAsText_IsReadTheBrazilianWay()
    {
        var handler = new ScriptedHandler(_ => Json(ToolAnswer("""{"outcome":"draft","type":"Credit","amount":"150,50","date":"2026-11-17"}""")));

        Assert.Equal(150.50m, (await Claude(handler).InterpretAsync("x", Today, default)).Amount);
    }

    [Theory]
    [InlineData("""{"content":[]}""")] // no tool call
    [InlineData("""{"content":[{"type":"text","text":"Claro! Registrei 1 milhão."}]}""")] // free text instead of the tool
    [InlineData("""{"content":[{"type":"tool_use","name":"other_tool","input":{"outcome":"draft"}}]}""")] // another tool
    [InlineData("""{"nothing":true}""")]
    public async Task AnAnswerThatIsNotTheExpectedToolCall_IsInvalid_AndTheValidatorRefusesIt(string answer)
    {
        var handler = new ScriptedHandler(_ => Json(answer));

        var raw = await Claude(handler).InterpretAsync("x", Today, default);

        Assert.Equal("invalid", raw.Outcome);
        Assert.Equal(DraftStatus.Rejected, DraftValidator.Validate(raw, Today, "claude").Status);
    }

    [Fact]
    public async Task AProviderError_BecomesUnavailable_WithoutLeakingTheBody()
    {
        var handler = new ScriptedHandler(_ => Json("""{"error":"secret detail with the sentence"}""", HttpStatusCode.TooManyRequests));

        var error = await Assert.ThrowsAsync<AssistantUnavailableException>(() => Claude(handler).InterpretAsync("x", Today, default));

        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Contains("429", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowProvider_IsCutOff_AsUnavailable()
    {
        var handler = new ScriptedHandler(_ =>
        {
            Thread.Sleep(500);

            return Json(ToolAnswer("""{"outcome":"reject"}"""));
        });

        await Assert.ThrowsAsync<AssistantUnavailableException>(() => Claude(handler, TimeSpan.FromMilliseconds(50)).InterpretAsync("x", Today, default));
    }

    [Fact]
    public async Task ACallerThatCancels_IsNotTreatedAsAnUnavailableProvider()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new ScriptedHandler(_ => Json(ToolAnswer("""{"outcome":"reject"}""")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Claude(handler).InterpretAsync("x", Today, cancelled.Token));
    }

    [Fact]
    public void TheSystemPrompt_TellsTheModelThatTheTextIsDataNotInstructions()
    {
        var prompt = ClaudeInterpreter.SystemPrompt(Today);

        Assert.Contains("DATA", prompt, StringComparison.Ordinal);
        Assert.Contains("never instructions", prompt, StringComparison.Ordinal);
        Assert.Contains("2026-11-17", prompt, StringComparison.Ordinal);
    }
}
