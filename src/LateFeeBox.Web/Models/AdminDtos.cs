namespace LateFeeBox.Web.Models;

public sealed record LoginRequest(string Username, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record MemberRequest(string DisplayName, long? BaleUserId, string? BaleUsername);
public sealed record ObservedMemberRequest(string DisplayName);
public sealed record AmountRequest(long Amount, string Description);
