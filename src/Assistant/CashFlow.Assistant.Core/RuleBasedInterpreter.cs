using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CashFlow.Assistant.Core;

/// <summary>
/// A deterministic interpreter for Brazilian Portuguese: no network, no key, same answer every time. It is what runs in
/// development and in the tests, and what the system falls back to when no real provider is configured (RNF-18).
/// It understands the common ways of writing an entry ("paguei 150 de luz ontem", "recebi R$ 1.234,56 de venda dia 12")
/// and asks when it is not sure, instead of guessing.
/// </summary>
public sealed partial class RuleBasedInterpreter : IEntryInterpreter
{
    public string Name => "rules";

    // Text that tries to give the assistant orders instead of describing an entry.
    private static readonly string[] InjectionMarkers =
    [
        "ignore", "esqueca", "desconsidere", "instrucoes", "instrucao", "system prompt", "prompt", "voce agora", "agora voce",
        "aja como", "finja", "jailbreak", "sobrescreva", "apague", "delete", "drop table", "senha", "token", "api key",
    ];

    private static readonly string[] CreditWords =
    [
        "recebi", "recebimento", "receita", "venda", "vendi", "entrada", "ganhei", "deposito", "depositaram", "cliente pagou",
        "me pagou", "me pagaram", "pagaram", "caiu na conta", "faturei", "faturamento",
    ];

    private static readonly string[] DebitWords =
    [
        "paguei", "pagamento", "comprei", "compra", "gastei", "gasto", "despesa", "saida", "boleto", "conta de", "aluguel",
        "fornecedor", "imposto", "taxa", "tarifa", "frete", "salario", "fatura", "mensalidade", "retirada", "saque",
    ];

    private static readonly string[] Weekdays = ["domingo", "segunda", "terca", "quarta", "quinta", "sexta", "sabado"];

    public Task<RawInterpretation> InterpretAsync(string text, DateOnly today, CancellationToken cancellationToken) =>
        Task.FromResult(Interpret(text, today));

    public static RawInterpretation Interpret(string text, DateOnly today)
    {
        var plain = Fold(text);

        if (InjectionMarkers.Any(m => ContainsWord(plain, m)))
        {
            return RawInterpretation.Refuse();
        }

        var withoutDates = ExtractDate(plain, today, out var date, out var dateQuestion);
        if (dateQuestion is not null)
        {
            return RawInterpretation.Ask(dateQuestion);
        }

        if (NegativeNumber().IsMatch(withoutDates))
        {
            return RawInterpretation.Ask("Informe o valor sem sinal de menos. Para uma saída, diga que foi um pagamento ou uma compra.");
        }

        var amounts = FindAmounts(withoutDates);
        if (amounts.Count == 0)
        {
            return IsFinancial(plain) ? RawInterpretation.Ask("Qual foi o valor?") : RawInterpretation.Refuse();
        }

        if (amounts.Count > 1)
        {
            return RawInterpretation.Ask("Encontrei mais de um valor. Qual é o valor do lançamento?");
        }

        var credit = CreditWords.Any(w => plain.Contains(w, StringComparison.Ordinal));
        var debit = DebitWords.Any(w => ContainsWord(plain, w));
        if (credit && debit && !HasClearCreditPhrase(plain))
        {
            return RawInterpretation.Ask("Foi uma entrada (crédito) ou uma saída (débito)?");
        }

        if (!credit && !debit)
        {
            return RawInterpretation.Ask("Foi uma entrada (crédito) ou uma saída (débito)?");
        }

        var type = credit ? "Credit" : "Debit";

        return new RawInterpretation(RawInterpretation.Draft, type, amounts[0], date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Describe(text), null);
    }

    /// <summary>"Cliente pagou" is an income even though it contains "pagou".</summary>
    private static bool HasClearCreditPhrase(string plain) =>
        plain.Contains("cliente pagou", StringComparison.Ordinal) || plain.Contains("me pagou", StringComparison.Ordinal) || plain.Contains("me pagaram", StringComparison.Ordinal);

    private static bool IsFinancial(string plain) =>
        CreditWords.Concat(DebitWords).Any(w => plain.Contains(w, StringComparison.Ordinal));

    // ----- dates

    /// <summary>Finds the day the person means, removes the words that said it, and defaults to today.</summary>
    private static string ExtractDate(string plain, DateOnly today, out DateOnly date, out string? question)
    {
        question = null;
        date = today;
        var rest = plain;

        var full = FullDate().Match(rest);
        if (full.Success)
        {
            var day = int.Parse(full.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(full.Groups[2].Value, CultureInfo.InvariantCulture);
            var yearText = full.Groups[3].Value;
            var year = yearText.Length == 0 ? today.Year : int.Parse(yearText, CultureInfo.InvariantCulture);
            year = year < 100 ? 2000 + year : year;

            if (!TryDate(year, month, day, out var parsed))
            {
                question = "Não entendi a data. Informe o dia, por exemplo 12/11.";

                return rest;
            }

            // "12/11" without a year, in the future, means last year.
            date = yearText.Length == 0 && parsed > today ? parsed.AddYears(-1) : parsed;

            return rest.Remove(full.Index, full.Length);
        }

        var dayOfMonth = DayOfMonth().Match(rest);
        if (dayOfMonth.Success)
        {
            var day = int.Parse(dayOfMonth.Groups[1].Value, CultureInfo.InvariantCulture);
            var candidate = today;
            if (day > today.Day)
            {
                candidate = today.AddMonths(-1); // "dia 28" said on the 10th is the previous month
            }

            if (!TryDate(candidate.Year, candidate.Month, day, out var parsed))
            {
                question = "Não entendi a data. Informe o dia, por exemplo 12/11.";

                return rest;
            }

            date = parsed;

            return rest.Remove(dayOfMonth.Index, dayOfMonth.Length);
        }

        if (rest.Contains("amanha", StringComparison.Ordinal))
        {
            question = "A data não pode ser no futuro. Em que dia foi?";

            return rest;
        }

        foreach (var (word, back) in new[] { ("anteontem", 2), ("ontem", 1), ("hoje", 0) })
        {
            var at = rest.IndexOf(word, StringComparison.Ordinal);
            if (at >= 0)
            {
                date = today.AddDays(-back);

                return rest.Remove(at, word.Length);
            }
        }

        for (var i = 0; i < Weekdays.Length; i++)
        {
            var match = Regex.Match(rest, $@"\b{Weekdays[i]}(-feira)?\b", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (match.Success)
            {
                // The last time that weekday happened, today included.
                var back = ((int)today.DayOfWeek - i + 7) % 7;
                date = today.AddDays(-back);

                return rest.Remove(match.Index, match.Length);
            }
        }

        return rest;
    }

    private static bool TryDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1900 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);

        return true;
    }

    // ----- amounts

    private static List<decimal> FindAmounts(string plain)
    {
        var found = new List<decimal>();
        foreach (Match match in Amount().Matches(plain))
        {
            if (TryAmount(match.Groups["number"].Value, match.Groups["mil"].Success, out var value))
            {
                found.Add(value);
            }
        }

        return found;
    }

    private static bool TryAmount(string text, bool thousand, out decimal value)
    {
        value = 0;
        // Brazilian form "1.234,56"; also "1234.56" and "1234,5".
        var normalized = text.Contains(',', StringComparison.Ordinal)
            ? text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
            : DotAsThousands().IsMatch(text) ? text.Replace(".", string.Empty, StringComparison.Ordinal) : text;

        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = thousand ? parsed * 1000m : parsed;
        value = decimal.Round(value, 2);

        return value > 0;
    }

    private static string Describe(string original)
    {
        var text = Regex.Replace(original.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100));

        return text.Length <= DraftValidator.MaxDescriptionLength ? text : text[..DraftValidator.MaxDescriptionLength];
    }

    // ----- text helpers

    /// <summary>Lower case without accents: "Pagamento de água" and "pagamento de agua" read the same.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool ContainsWord(string plain, string word) =>
        Regex.IsMatch(plain, $@"(?<![a-z]){Regex.Escape(word)}s?(?![a-z])", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    [GeneratedRegex(@"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b", RegexOptions.None, 100)]
    private static partial Regex FullDate();

    [GeneratedRegex(@"\bdia\s+(\d{1,2})\b", RegexOptions.None, 100)]
    private static partial Regex DayOfMonth();

    // An amount: optional "r$", digits with Brazilian or plain separators, optional "mil" / "reais" / "conto(s)".
    [GeneratedRegex(@"(?:r\$\s*)?(?<![\d.,])(?<number>\d{1,3}(?:\.\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?![\d])(?:\s*(?<mil>mil)\b)?", RegexOptions.None, 100)]
    private static partial Regex Amount();

    [GeneratedRegex(@"(?<![\w.,])-\s?\d", RegexOptions.None, 100)]
    private static partial Regex NegativeNumber();

    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+$", RegexOptions.None, 100)]
    private static partial Regex DotAsThousands();
}
