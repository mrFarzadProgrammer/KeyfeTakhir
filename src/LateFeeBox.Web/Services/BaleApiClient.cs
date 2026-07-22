using System.Net.Http.Json;
using System.Text.Json;
using LateFeeBox.Web.Models;
using LateFeeBox.Web.Options;
using Microsoft.Extensions.Options;

namespace LateFeeBox.Web.Services;

public sealed class BaleApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly BaleOptions _options;
    private readonly HttpClient _httpClient;

    public BaleApiClient(IOptions<BaleOptions> options)
    {
        _options = options.Value;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task<bool> DeleteWebhookAsync(CancellationToken cancellationToken = default)
        => PostAsync<object, bool>("deleteWebhook", new { drop_pending_updates = false }, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<List<BaleUpdate>> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
        => PostAsync<object, List<BaleUpdate>>(
            "getUpdates",
            new
            {
                offset,
                limit = 100,
                timeout = timeoutSeconds,
                allowed_updates = new[] { "message", "edited_message", "pre_checkout_query" }
            },
            TimeSpan.FromSeconds(timeoutSeconds + 30),
            cancellationToken);

    public Task<object> SendMessageAsync(
        long chatId,
        string text,
        long? replyToMessageId = null,
        object? replyMarkup = null,
        CancellationToken cancellationToken = default)
        => PostAsync<object, object>(
            "sendMessage",
            new
            {
                chat_id = chatId,
                text,
                reply_to_message_id = replyToMessageId,
                reply_markup = replyMarkup
            },
            TimeSpan.FromSeconds(30),
            cancellationToken);

    public Task<BaleChatMember> GetChatMemberAsync(
        long chatId,
        long userId,
        CancellationToken cancellationToken = default)
        => PostAsync<object, BaleChatMember>(
            "getChatMember",
            new { chat_id = chatId, user_id = userId },
            TimeSpan.FromSeconds(30),
            cancellationToken);

    public Task<List<BaleChatMember>> GetChatAdministratorsAsync(
        long chatId,
        CancellationToken cancellationToken = default)
        => PostAsync<object, List<BaleChatMember>>(
            "getChatAdministrators",
            new { chat_id = chatId },
            TimeSpan.FromSeconds(30),
            cancellationToken);

    public Task<long> GetChatMembersCountAsync(
        long chatId,
        CancellationToken cancellationToken = default)
        => PostAsync<object, long>(
            "getChatMembersCount",
            new { chat_id = chatId },
            TimeSpan.FromSeconds(30),
            cancellationToken);

    public Task<object> SendInvoiceAsync(
        long chatId,
        string title,
        string description,
        string payload,
        long amountRials,
        long? replyToMessageId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(_options.ProviderToken))
        {
            throw new InvalidOperationException("Bale:ProviderToken is not configured.");
        }

        return PostAsync<object, object>(
            "sendInvoice",
            new
            {
                chat_id = chatId,
                title,
                description,
                payload,
                provider_token = _options.ProviderToken.Trim(),
                currency = "IRR",
                prices = new[] { new { label = title, amount = amountRials } },
                start_parameter = "late-fee-payment",
                reply_to_message_id = replyToMessageId
            },
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public Task<bool> AnswerPreCheckoutQueryAsync(
        string preCheckoutQueryId,
        bool ok,
        string? errorMessage,
        CancellationToken cancellationToken = default)
        => PostAsync<object, bool>(
            "answerPreCheckoutQuery",
            new
            {
                pre_checkout_query_id = preCheckoutQueryId,
                ok,
                error_message = ok ? null : errorMessage
            },
            TimeSpan.FromSeconds(8),
            cancellationToken);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout);

        var endpoint = BuildEndpoint(method);
        using var response = await _httpClient.PostAsJsonAsync(endpoint, request, JsonOptions, timeoutCts.Token);
        var body = await response.Content.ReadFromJsonAsync<BaleResponse<TResponse>>(JsonOptions, timeoutCts.Token);

        if (!response.IsSuccessStatusCode || body is null || !body.Ok)
        {
            var description = body?.Description ?? $"HTTP {(int)response.StatusCode}";
            throw new BaleApiException(method, body?.ErrorCode, description);
        }

        if (body.Result is null)
        {
            throw new BaleApiException(method, body.ErrorCode, "Bale API returned an empty result.");
        }

        return body.Result;
    }

    private Uri BuildEndpoint(string method)
    {
        var token = _options.BotToken.Trim();
        return new Uri($"https://tapi.bale.ai/bot{token}/{method}", UriKind.Absolute);
    }

    private void EnsureConfigured()
    {
        var token = _options.BotToken.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Bale:BotToken is not configured.");
        }

        if (!token.Contains(':') || token.StartsWith("bot", StringComparison.OrdinalIgnoreCase) || token.Contains("://"))
        {
            throw new InvalidOperationException("Bale:BotToken format is invalid. Store only the token returned by @botfather.");
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class BaleApiException(string method, int? errorCode, string description)
    : Exception($"Bale API method '{method}' failed ({errorCode?.ToString() ?? "no-code"}): {description}")
{
    public string Method { get; } = method;
    public int? ErrorCode { get; } = errorCode;
    public string Description { get; } = description;
}
