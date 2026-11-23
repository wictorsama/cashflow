using System.Globalization;
using System.Text.Json;
using CashFlow.Assistant.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashFlow.Assistant.Tests;

/// <summary>
/// The reference set (RNF-16): Portuguese sentences with the expected result, run through the same path a request takes
/// (cleaning, interpreter, validation). It measures accuracy and fails the build if it drops: the way to notice that a
/// change to the rules, or later to the prompt, made the assistant worse.
/// </summary>
public sealed class GoldenSetTests
{
    private sealed record Case(string Tag, string Text, string Status, string? Type, decimal? Amount, string? Date);

    private static readonly string[] Kinds = ["core", "ambiguity", "absurd", "security"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (DateOnly Today, List<Case> Cases) Load()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "golden-set.json"));
        var doc = JsonDocument.Parse(stream);
        var today = DateOnly.Parse(doc.RootElement.GetProperty("today").GetString()!, CultureInfo.InvariantCulture);
        var cases = doc.RootElement.GetProperty("cases").Deserialize<List<Case>>(Json)!;

        return (today, cases);
    }

    private static string StatusOf(DraftStatus status) => status switch
    {
        DraftStatus.Draft => "draft",
        DraftStatus.NeedsClarification => "clarify",
        _ => "rejected",
    };

    private static async Task<DraftResult> RunAsync(Case c, DateOnly today)
    {
        var service = new DraftService(new RuleBasedInterpreter(), TimeProvider.System, NullLogger<DraftService>.Instance);

        return await service.DraftAsync(c.Text, today, default);
    }

    private static bool Matches(Case c, DraftResult result)
    {
        if (StatusOf(result.Status) != c.Status)
        {
            return false;
        }

        return c.Status != "draft"
            || (result.Draft is { } d && d.Type == c.Type && d.Amount == c.Amount && d.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == c.Date);
    }

    [Fact]
    public async Task Accuracy_IsAtLeast90Percent_OverTheWholeSet()
    {
        var (today, cases) = Load();
        var failures = new List<string>();

        foreach (var c in cases)
        {
            var result = await RunAsync(c, today);
            if (!Matches(c, result))
            {
                failures.Add($"[{c.Tag}] \"{c.Text}\" expected {c.Status} {c.Type} {c.Amount} {c.Date}, got {result.Status} {result.Draft}");
            }
        }

        var accuracy = 100.0 * (cases.Count - failures.Count) / cases.Count;
        Assert.True(accuracy >= 90, $"{accuracy:F1}% ({cases.Count - failures.Count}/{cases.Count}). Misses:\n{string.Join('\n', failures)}");
    }

    [Theory]
    [InlineData("security")]
    [InlineData("absurd")]
    [InlineData("ambiguity")]
    public async Task Everything_ThatMustNotBecomeADraft_IsNeverOne(string tag)
    {
        var (today, cases) = Load();

        foreach (var c in cases.Where(c => c.Tag == tag))
        {
            var result = await RunAsync(c, today);

            // For these tags a draft is always wrong, whatever else is said about it.
            Assert.True(result.Status != DraftStatus.Draft, $"[{tag}] \"{c.Text}\" became a draft: {result.Draft}");
            Assert.Equal(c.Status, StatusOf(result.Status));
        }
    }

    [Fact]
    public async Task TheCoreCases_AreAllUnderstood()
    {
        var (today, cases) = Load();

        foreach (var c in cases.Where(c => c.Tag == "core"))
        {
            Assert.True(Matches(c, await RunAsync(c, today)), $"\"{c.Text}\"");
        }
    }

    [Fact]
    public void TheSet_HasEnoughCasesOfEveryKind()
    {
        var (_, cases) = Load();

        Assert.True(cases.Count >= 40);
        Assert.All(Kinds, tag => Assert.True(cases.Count(c => c.Tag == tag) >= 5, tag));
    }
}
