using System.Text.Json.Serialization;

namespace LateFeeBox.Web.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DebtEntryKind { Penalty = 1, ManualAdjustment = 2, Payment = 3, Refund = 4 }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FundEntryKind { OpeningBalance = 1, ManualAdjustment = 2, Payment = 3, Expense = 4, Refund = 5 }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentRequestStatus { Pending = 1, Approved = 2, Paid = 3, Rejected = 4, Expired = 5 }

public sealed class AppState
{
    public AdminUser? Admin { get; set; }
    public List<TeamMember> Members { get; set; } = [];
    public List<DebtLedgerEntry> DebtEntries { get; set; } = [];
    public List<FundLedgerEntry> FundEntries { get; set; } = [];
    public List<PaymentRequest> Payments { get; set; } = [];
    public List<GroupInfo> Groups { get; set; } = [];
    public Dictionary<long, ObservedBaleUser> ObservedUsers { get; set; } = [];
    public long LastProcessedUpdateId { get; set; } = -1;
}

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public sealed class GroupInfo
{
    public long ChatId { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TeamMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public long GroupChatId { get; set; }
    public long? BaleUserId { get; set; }
    public string? BaleUsername { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class DebtLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamMemberId { get; set; }
    public long GroupChatId { get; set; }
    public long AmountRials { get; set; }
    public DebtEntryKind Kind { get; set; }
    public string Description { get; set; } = string.Empty;
    public Guid? PaymentRequestId { get; set; }
    public Guid? CreatedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class FundLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long GroupChatId { get; set; }
    public long AmountRials { get; set; }
    public FundEntryKind Kind { get; set; }
    public string Description { get; set; } = string.Empty;
    public Guid? PaymentRequestId { get; set; }
    public Guid? CreatedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class PaymentRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Payload { get; set; } = string.Empty;
    public Guid TeamMemberId { get; set; }
    public long BaleUserId { get; set; }
    public long AmountRials { get; set; }
    public string Currency { get; set; } = "IRR";
    public PaymentRequestStatus Status { get; set; } = PaymentRequestStatus.Pending;
    public long GroupChatId { get; set; }
    public long? SourceMessageId { get; set; }
    public string? PreCheckoutQueryId { get; set; }
    public string? TelegramPaymentChargeId { get; set; }
    public string? ProviderPaymentChargeId { get; set; }
    public string? RejectionReason { get; set; }
    public long AppliedDebtRials { get; set; }
    public long OverpaymentRials { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PaidAt { get; set; }
}

public sealed class ObservedBaleUser
{
    public long BaleUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Username { get; set; }
    public long LastChatId { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
