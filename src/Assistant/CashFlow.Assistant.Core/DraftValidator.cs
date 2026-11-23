using System.Globalization;
using System.Text;

namespace CashFlow.Assistant.Core;

/// <summary>
/// Everything an interpreter returns is checked here against the rules the entry itself enforces (and a few stricter
/// ones for a suggestion). A draft that does not pass is never shown as a draft: it becomes a question or a refusal.
/// This is the defence that does not depend on the model behaving, so it also covers a manipulated model.
/// </summary>
public static class DraftValidator
{
    /// <summary>Same limits as the Entries domain.</summary>
    public const int MaxDescriptionLength = 200;

    public const decimal MaxAmount = 999_999_999_999.99m;

    /// <summary>Above this, the assistant asks the person to confirm the amount instead of proposing it.</summary>
    public const decimal MaxPlausibleAmount = 10_000_000m;

    /// <summary>An entry older than this is probably a mistake in the sentence.</summary>
    public const int MaxDaysInThePast = 365;

    public const int MaxInputLength = 500;

    /// <summary>The text the person typed, cleaned: control characters removed, spaces collapsed. Null if nothing usable.</summary>
    public static string? CleanInput(string? text)
    {
        var cleaned = Clean(text);

        return cleaned.Length is 0 or > MaxInputLength ? null : cleaned;
    }

    public static DraftResult Validate(RawInterpretation raw, DateOnly today, string provider)
    {
        switch (raw.Outcome)
        {
            case RawInterpretation.Reject:
                return DraftResult.Reject("Descreva um lançamento financeiro, por exemplo: \"recebi 150 de uma venda hoje\".", provider);

            case RawInterpretation.Clarify:
                var question = Clean(raw.Question);

                return DraftResult.Ask(question.Length is > 0 and <= 200 ? question : "Pode descrever o lançamento com mais detalhes (tipo, valor e dia)?", provider);

            case RawInterpretation.Draft:
                break;

            default:
                return DraftResult.Reject("Não foi possível interpretar a resposta do assistente.", provider);
        }

        if (raw.Type is not ("Credit" or "Debit"))
        {
            return DraftResult.Ask("Foi uma entrada (crédito) ou uma saída (débito)?", provider);
        }

        if (raw.Amount is null or <= 0 || raw.Amount > MaxAmount || decimal.Round(raw.Amount.Value, 2) != raw.Amount)
        {
            return DraftResult.Ask("Qual foi o valor? Use até duas casas decimais.", provider);
        }

        if (raw.Amount > MaxPlausibleAmount)
        {
            return DraftResult.Ask($"O valor {raw.Amount.Value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"))} é muito alto. Confirme o valor.", provider);
        }

        if (!DateOnly.TryParseExact(raw.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return DraftResult.Ask("Em que dia foi? Informe a data.", provider);
        }

        if (date > today)
        {
            return DraftResult.Ask("A data não pode ser no futuro. Em que dia foi?", provider);
        }

        if (today.DayNumber - date.DayNumber > MaxDaysInThePast)
        {
            return DraftResult.Ask("Essa data é de mais de um ano atrás. Confirme o dia.", provider);
        }

        var description = Clean(raw.Description);
        if (description.Length > MaxDescriptionLength)
        {
            description = description[..MaxDescriptionLength].TrimEnd();
        }

        return DraftResult.Proposal(new EntryDraft(raw.Type, raw.Amount.Value, date, description.Length == 0 ? null : description), provider);
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text.Trim())
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString();
    }
}
