using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CashFlow.Consolidation.Application;

namespace CashFlow.Consolidation.Infrastructure.Reconciliation;

/// <summary>
/// Reads the totals from the Entries service with a service token (client credentials, scope entries:reconcile).
/// The token is kept until shortly before it expires, so the identity provider is not called on every run.
/// </summary>
public sealed class EntriesTotalsClient(HttpClient http, ReconciliationOptions options, TimeProvider clock)
    : IEntriesTotalsSource, IDisposable
{
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public async Task<IReadOnlyList<DailyTotal>> GetAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{options.EntriesUrl.TrimEnd('/')}/v1/reconciliation/daily-totals" +
            $"?from={fromDate:yyyy-MM-dd}&to={toDate:yyyy-MM-dd}&registeredBefore={Uri.EscapeDataString(registeredBefore.ToString("O", CultureInfo.InvariantCulture))}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<DailyTotal>>(cancellationToken) ?? [];
    }

    public void Dispose() => _tokenGate.Dispose();

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpiresAt)
            {
                return _token;
            }

            using var response = await http.PostAsync(
                options.TokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                }),
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("The identity provider returned an empty token response.");

            _token = token.AccessToken;
            _tokenExpiresAt = clock.GetUtcNow().AddSeconds(Math.Max(0, token.ExpiresIn - 30));

            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);
}
