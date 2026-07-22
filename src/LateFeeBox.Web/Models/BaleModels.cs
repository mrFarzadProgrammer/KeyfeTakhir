using System.Text.Json.Serialization;

namespace LateFeeBox.Web.Models;

public sealed class BaleResponse<T>
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public T? Result { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("error_code")] public int? ErrorCode { get; set; }
}

public sealed class BaleUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public BaleMessage? Message { get; set; }
    [JsonPropertyName("edited_message")] public BaleMessage? EditedMessage { get; set; }
    [JsonPropertyName("pre_checkout_query")] public BalePreCheckoutQuery? PreCheckoutQuery { get; set; }
}

public sealed class BaleMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("from")] public BaleUser? From { get; set; }
    [JsonPropertyName("chat")] public BaleChat Chat { get; set; } = new();
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("reply_to_message")] public BaleMessage? ReplyToMessage { get; set; }
    [JsonPropertyName("new_chat_members")] public List<BaleUser>? NewChatMembers { get; set; }
    [JsonPropertyName("left_chat_member")] public BaleUser? LeftChatMember { get; set; }
    [JsonPropertyName("successful_payment")] public BaleSuccessfulPayment? SuccessfulPayment { get; set; }
}

public sealed class BaleUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("is_bot")] public bool IsBot { get; set; }
    [JsonPropertyName("first_name")] public string FirstName { get; set; } = string.Empty;
    [JsonPropertyName("last_name")] public string? LastName { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    public string DisplayName => string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
}

public sealed class BaleChat
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public sealed class BaleChatMember
{
    [JsonPropertyName("user")] public BaleUser User { get; set; } = new();
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("is_member")] public bool? IsMember { get; set; }
}

public sealed class BalePreCheckoutQuery
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("from")] public BaleUser From { get; set; } = new();
    [JsonPropertyName("currency")] public string Currency { get; set; } = string.Empty;
    [JsonPropertyName("total_amount")] public long TotalAmount { get; set; }
    [JsonPropertyName("invoice_payload")] public string InvoicePayload { get; set; } = string.Empty;
}

public sealed class BaleSuccessfulPayment
{
    [JsonPropertyName("currency")] public string Currency { get; set; } = string.Empty;
    [JsonPropertyName("total_amount")] public long TotalAmount { get; set; }
    [JsonPropertyName("invoice_payload")] public string InvoicePayload { get; set; } = string.Empty;
    [JsonPropertyName("telegram_payment_charge_id")] public string? TelegramPaymentChargeId { get; set; }
    [JsonPropertyName("provider_payment_charge_id")] public string? ProviderPaymentChargeId { get; set; }
}
