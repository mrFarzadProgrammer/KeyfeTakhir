using System.Security.Claims;
using LateFeeBox.Web.Data;
using LateFeeBox.Web.Domain;
using LateFeeBox.Web.Models;
using LateFeeBox.Web.Security;
using LateFeeBox.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LateFeeBox.Web.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/login", LoginAsync).AllowAnonymous().RequireRateLimiting("admin-login");

        var admin = app.MapGroup("/api/admin").RequireAuthorization();
        admin.MapGet("/session", SessionAsync);
        admin.MapGet("/dashboard", DashboardAsync);
        admin.MapGet("/members", MembersAsync);
        admin.MapGet("/observed-users", ObservedUsersAsync);
        admin.MapGet("/fund/entries", FundEntriesAsync);
        admin.MapGet("/payments", PaymentsAsync);

        admin.MapPost("/logout", (Func<HttpContext, Task<IResult>>)LogoutAsync)
            .AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/change-password", ChangePasswordAsync).AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/members", CreateMemberAsync).AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPut("/members/{id:guid}", UpdateMemberAsync).AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/members/{id:guid}/toggle", ToggleMemberAsync).AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/members/{id:guid}/debt/set", SetDebtAsync).AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/members/{id:guid}/debt/add", AddPenaltyAsync).AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/observed-users/{baleUserId:long}/create-member", CreateMemberFromObservedAsync)
            .AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/fund/set-balance", SetFundBalanceAsync).AddEndpointFilter<CsrfEndpointFilter>();
        admin.MapPost("/fund/adjust", AdjustFundAsync).AddEndpointFilter<CsrfEndpointFilter>();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        JsonStore store,
        PasswordService passwordService,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var valid = await store.ReadAsync(
            state => state.Admin is not null &&
                     string.Equals(state.Admin.Username, username, StringComparison.Ordinal) &&
                     passwordService.Verify(state.Admin.PasswordHash, request.Password),
            cancellationToken);

        if (!valid)
        {
            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
            return Results.Unauthorized();
        }

        var admin = await store.WriteAsync(state =>
        {
            state.Admin!.LastLoginAt = DateTime.UtcNow;
            return new { state.Admin.Id, state.Admin.Username };
        }, cancellationToken);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim(ClaimTypes.Name, admin.Username),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });
        httpContext.User = principal;

        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return Results.Ok(new { username = admin.Username, csrfToken = tokens.RequestToken });
    }

    private static IResult SessionAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return Results.Ok(new { username = httpContext.User.Identity?.Name, csrfToken = tokens.RequestToken });
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        JsonStore store,
        PasswordService passwordService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12)
        {
            return Results.BadRequest(new { message = "رمز عبور جدید باید حداقل ۱۲ کاراکتر باشد." });
        }

        var changed = await store.WriteAsync(state =>
        {
            if (state.Admin is null || !passwordService.Verify(state.Admin.PasswordHash, request.CurrentPassword))
            {
                return false;
            }

            state.Admin.PasswordHash = passwordService.Hash(request.NewPassword);
            return true;
        }, cancellationToken);

        return changed
            ? Results.Ok(new { message = "رمز عبور تغییر کرد." })
            : Results.BadRequest(new { message = "رمز عبور فعلی صحیح نیست." });
    }

    private static async Task<IResult> DashboardAsync(
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
    {
        var result = await store.ReadAsync(state =>
        {
            var activeMembers = state.Members.Where(x => x.IsActive).ToArray();
            var debts = DebtMap(state);
            var totalDebtRials = activeMembers.Sum(x => Math.Max(0, debts.GetValueOrDefault(x.Id)));
            var debtorCount = activeMembers.Count(x => debts.GetValueOrDefault(x.Id) > 0);
            var fundBalanceRials = state.FundEntries.Sum(x => x.AmountRials);
            var collectedRials = state.FundEntries.Where(x => x.Kind == FundEntryKind.Payment).Sum(x => x.AmountRials);
            var paidCount = state.Payments.Count(x => x.Status == PaymentRequestStatus.Paid);
            return new
            {
                activeMemberCount = activeMembers.Length,
                debtorCount,
                totalDebt = money.ToDisplayUnits(totalDebtRials),
                fundBalance = money.ToDisplayUnits(fundBalanceRials),
                collected = money.ToDisplayUnits(collectedRials),
                paidCount,
                unit = money.UnitName
            };
        }, cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> MembersAsync(
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
    {
        var result = await store.ReadAsync(state =>
        {
            var debts = DebtMap(state);
            return state.Members
                .OrderByDescending(x => x.IsActive)
                .ThenBy(x => x.DisplayName)
                .Select(x => new
                {
                    x.Id,
                    x.DisplayName,
                    x.BaleUserId,
                    x.BaleUsername,
                    x.IsActive,
                    debt = money.ToDisplayUnits(debts.GetValueOrDefault(x.Id)),
                    x.CreatedAt,
                    x.UpdatedAt
                })
                .ToArray();
        }, cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ObservedUsersAsync(JsonStore store, CancellationToken cancellationToken)
    {
        var result = await store.ReadAsync(state =>
        {
            var linked = state.Members.Where(x => x.BaleUserId.HasValue).Select(x => x.BaleUserId!.Value).ToHashSet();
            return state.ObservedUsers.Values
                .Where(x => !linked.Contains(x.BaleUserId))
                .OrderByDescending(x => x.LastSeenAt)
                .Take(200)
                .Select(x => new { x.BaleUserId, x.DisplayName, x.Username, x.LastChatId, x.LastSeenAt })
                .ToArray();
        }, cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> FundEntriesAsync(
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
    {
        var result = await store.ReadAsync(state => state.FundEntries
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new
            {
                x.Id,
                amount = money.ToDisplayUnits(x.AmountRials),
                kind = x.Kind.ToString(),
                x.Description,
                x.CreatedAt
            })
            .ToArray(), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> PaymentsAsync(
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
    {
        var result = await store.ReadAsync(state => state.Payments
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new
            {
                x.Id,
                memberName = state.Members.FirstOrDefault(m => m.Id == x.TeamMemberId)?.DisplayName ?? "عضو حذف‌شده",
                amount = money.ToDisplayUnits(x.AmountRials),
                status = x.Status.ToString(),
                appliedDebt = money.ToDisplayUnits(x.AppliedDebtRials),
                overpayment = money.ToDisplayUnits(x.OverpaymentRials),
                x.CreatedAt,
                x.PaidAt,
                x.ProviderPaymentChargeId
            })
            .ToArray(), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateMemberAsync(
        MemberRequest request,
        JsonStore store,
        CancellationToken cancellationToken)
    {
        var validation = ValidateMemberRequest(request);
        if (validation is not null) return validation;

        var result = await store.WriteAsync(state =>
        {
            if (request.BaleUserId.HasValue && state.Members.Any(x => x.BaleUserId == request.BaleUserId))
                return (Success: false, Message: "این شناسه بله قبلاً برای عضو دیگری ثبت شده است.", Id: Guid.Empty);

            var member = new TeamMember
            {
                DisplayName = request.DisplayName.Trim(),
                BaleUserId = request.BaleUserId,
                BaleUsername = NormalizeUsername(request.BaleUsername)
            };
            state.Members.Add(member);
            return (Success: true, Message: string.Empty, member.Id);
        }, cancellationToken);

        return result.Success
            ? Results.Created($"/api/admin/members/{result.Id}", new { result.Id })
            : Results.BadRequest(new { message = result.Message });
    }

    private static async Task<IResult> UpdateMemberAsync(
        Guid id,
        MemberRequest request,
        JsonStore store,
        CancellationToken cancellationToken)
    {
        var validation = ValidateMemberRequest(request);
        if (validation is not null) return validation;

        var result = await store.WriteAsync(state =>
        {
            var member = state.Members.FirstOrDefault(x => x.Id == id);
            if (member is null) return (Status: 404, Message: "عضو پیدا نشد.");
            if (request.BaleUserId.HasValue && state.Members.Any(x => x.Id != id && x.BaleUserId == request.BaleUserId))
                return (Status: 400, Message: "این شناسه بله قبلاً برای عضو دیگری ثبت شده است.");

            var baleIdentityChanged = member.BaleUserId != request.BaleUserId;
            member.DisplayName = request.DisplayName.Trim();
            member.BaleUserId = request.BaleUserId;
            member.BaleUsername = NormalizeUsername(request.BaleUsername);
            member.UpdatedAt = DateTime.UtcNow;

            if (baleIdentityChanged)
            {
                ExpireOpenPayments(state, member.Id, "شناسه بله عضو پس از صدور فاکتور تغییر کرد.");
            }

            return (Status: 200, Message: string.Empty);
        }, cancellationToken);

        return result.Status switch
        {
            200 => Results.Ok(),
            404 => Results.NotFound(new { message = result.Message }),
            _ => Results.BadRequest(new { message = result.Message })
        };
    }

    private static async Task<IResult> ToggleMemberAsync(Guid id, JsonStore store, CancellationToken cancellationToken)
    {
        var found = await store.WriteAsync(state =>
        {
            var member = state.Members.FirstOrDefault(x => x.Id == id);
            if (member is null) return false;
            member.IsActive = !member.IsActive;
            member.UpdatedAt = DateTime.UtcNow;

            if (!member.IsActive)
            {
                ExpireOpenPayments(state, member.Id, "عضو در پنل غیرفعال شد.");
            }

            return true;
        }, cancellationToken);
        return found ? Results.Ok() : Results.NotFound();
    }

    private static Task<IResult> SetDebtAsync(
        Guid id,
        AmountRequest request,
        HttpContext httpContext,
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
        => ChangeDebtAsync(id, request, true, httpContext, store, money, cancellationToken);

    private static Task<IResult> AddPenaltyAsync(
        Guid id,
        AmountRequest request,
        HttpContext httpContext,
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
        => ChangeDebtAsync(id, request, false, httpContext, store, money, cancellationToken);

    private static async Task<IResult> ChangeDebtAsync(
        Guid id,
        AmountRequest request,
        bool setCurrent,
        HttpContext httpContext,
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
    {
        if (request.Amount < 0 || (!setCurrent && request.Amount <= 0))
            return Results.BadRequest(new { message = "مبلغ نامعتبر است." });
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 300)
            return Results.BadRequest(new { message = "توضیح الزامی است و حداکثر ۳۰۰ کاراکتر دارد." });

        var adminId = GetAdminId(httpContext);
        var result = await store.WriteAsync(state =>
        {
            if (state.Members.All(x => x.Id != id)) return false;
            var current = state.DebtEntries.Where(x => x.TeamMemberId == id).Sum(x => x.AmountRials);
            var target = money.ToRials(request.Amount);
            var delta = setCurrent ? target - current : target;
            if (delta != 0)
            {
                state.DebtEntries.Add(new DebtLedgerEntry
                {
                    TeamMemberId = id,
                    AmountRials = delta,
                    Kind = setCurrent ? DebtEntryKind.ManualAdjustment : DebtEntryKind.Penalty,
                    Description = request.Description.Trim(),
                    CreatedByAdminId = adminId
                });

                ExpireOpenPayments(state, id, "بدهی عضو پس از صدور فاکتور تغییر کرد.");
            }
            return true;
        }, cancellationToken);

        return result ? Results.Ok() : Results.NotFound();
    }

    private static async Task<IResult> CreateMemberFromObservedAsync(
        long baleUserId,
        ObservedMemberRequest request,
        JsonStore store,
        CancellationToken cancellationToken)
    {
        var result = await store.WriteAsync(state =>
        {
            if (!state.ObservedUsers.TryGetValue(baleUserId, out var observed))
                return (Status: 404, Message: "کاربر دیده‌شده پیدا نشد.");
            if (state.Members.Any(x => x.BaleUserId == baleUserId))
                return (Status: 400, Message: "این کاربر قبلاً عضو شده است.");

            state.Members.Add(new TeamMember
            {
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? observed.DisplayName : request.DisplayName.Trim(),
                BaleUserId = observed.BaleUserId,
                BaleUsername = observed.Username
            });
            return (Status: 200, Message: string.Empty);
        }, cancellationToken);

        return result.Status switch
        {
            200 => Results.Ok(),
            404 => Results.NotFound(new { message = result.Message }),
            _ => Results.BadRequest(new { message = result.Message })
        };
    }

    private static async Task<IResult> SetFundBalanceAsync(
        AmountRequest request,
        HttpContext httpContext,
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
    {
        if (request.Amount < 0) return Results.BadRequest(new { message = "موجودی نمی‌تواند منفی باشد." });
        var adminId = GetAdminId(httpContext);
        await store.WriteAsync(state =>
        {
            var current = state.FundEntries.Sum(x => x.AmountRials);
            var target = money.ToRials(request.Amount);
            var delta = target - current;
            if (delta != 0)
            {
                state.FundEntries.Add(new FundLedgerEntry
                {
                    AmountRials = delta,
                    Kind = state.FundEntries.Count == 0 ? FundEntryKind.OpeningBalance : FundEntryKind.ManualAdjustment,
                    Description = string.IsNullOrWhiteSpace(request.Description) ? "تنظیم موجودی صندوق" : request.Description.Trim(),
                    CreatedByAdminId = adminId
                });
            }
        }, cancellationToken);
        return Results.Ok();
    }

    private static async Task<IResult> AdjustFundAsync(
        AmountRequest request,
        HttpContext httpContext,
        JsonStore store,
        MoneyService money,
        CancellationToken cancellationToken)
    {
        if (request.Amount == 0) return Results.BadRequest(new { message = "مبلغ تغییر نباید صفر باشد." });
        if (string.IsNullOrWhiteSpace(request.Description)) return Results.BadRequest(new { message = "توضیح الزامی است." });
        var adminId = GetAdminId(httpContext);
        var amountRials = money.ToRials(request.Amount);
        await store.WriteAsync(state => state.FundEntries.Add(new FundLedgerEntry
        {
            AmountRials = amountRials,
            Kind = amountRials < 0 ? FundEntryKind.Expense : FundEntryKind.ManualAdjustment,
            Description = request.Description.Trim(),
            CreatedByAdminId = adminId
        }), cancellationToken);
        return Results.Ok();
    }

    private static IResult? ValidateMemberRequest(MemberRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120)
            return Results.BadRequest(new { message = "نام عضو الزامی است و حداکثر ۱۲۰ کاراکتر دارد." });
        if (request.BaleUsername?.Trim().TrimStart('@').Length > 64)
            return Results.BadRequest(new { message = "نام کاربری بله بیش از حد طولانی است." });
        return null;
    }

    private static void ExpireOpenPayments(AppState state, Guid memberId, string reason)
    {
        foreach (var payment in state.Payments.Where(x =>
                     x.TeamMemberId == memberId &&
                     x.Status is PaymentRequestStatus.Pending or PaymentRequestStatus.Approved))
        {
            payment.Status = PaymentRequestStatus.Expired;
            payment.RejectionReason = reason;
        }
    }

    private static Dictionary<Guid, long> DebtMap(AppState state)
        => state.DebtEntries
            .GroupBy(x => x.TeamMemberId)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.AmountRials));

    private static string? NormalizeUsername(string? username)
    {
        var result = username?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static Guid GetAdminId(HttpContext httpContext)
    {
        var value = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
