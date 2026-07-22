using System.Security.Cryptography;
using System.Text;
using LateFeeBox.Web.Data;
using LateFeeBox.Web.Domain;
using LateFeeBox.Web.Models;
using LateFeeBox.Web.Options;
using Microsoft.Extensions.Options;

namespace LateFeeBox.Web.Services;

public sealed class BotUpdateHandler(
    JsonStore store,
    BaleApiClient bale,
    MoneyService money,
    IOptions<BaleOptions> options,
    ILogger<BotUpdateHandler> logger)
{
    private readonly BaleOptions _options = options.Value;
    private readonly HashSet<long> _adminIds = options.Value.AdminUserIds.ToHashSet();
    private readonly HashSet<long> _allowedGroups = options.Value.AllowedGroupChatIds.ToHashSet();

    public async Task HandleAsync(BaleUpdate update, CancellationToken cancellationToken)
    {
        if (update.PreCheckoutQuery is not null)
        {
            await HandlePreCheckoutAsync(update.PreCheckoutQuery, cancellationToken);
            return;
        }

        var message = update.Message ?? update.EditedMessage;
        if (message is null) return;

        if (message.From is { IsBot: false } sender)
        {
            await ObserveUserAsync(sender, message.Chat.Id, cancellationToken);
        }

        if (message.ReplyToMessage?.From is { IsBot: false } replySender)
        {
            await ObserveUserAsync(replySender, message.Chat.Id, cancellationToken);
        }

        if (message.NewChatMembers is not null)
        {
            foreach (var user in message.NewChatMembers.Where(x => !x.IsBot))
            {
                await ObserveUserAsync(user, message.Chat.Id, cancellationToken);
                if (_allowedGroups.Contains(message.Chat.Id))
                {
                    await UpsertMemberAsync(user, cancellationToken);
                }
            }
        }

        if (message.LeftChatMember is { IsBot: false } leftUser && _allowedGroups.Contains(message.Chat.Id))
        {
            await DeactivateMemberAsync(leftUser.Id, cancellationToken);
        }

        if (message.SuccessfulPayment is not null)
        {
            await HandleSuccessfulPaymentAsync(message, message.SuccessfulPayment, cancellationToken);
            return;
        }

        if (message.From is null || string.IsNullOrWhiteSpace(message.Text)) return;

        var isPrivate = string.Equals(message.Chat.Type, "private", StringComparison.OrdinalIgnoreCase);
        var command = NormalizeCommand(message.Text);

        if (isPrivate)
        {
            if (IsRegistrationCommand(command))
            {
                await RegisterFromPrivateAsync(message, cancellationToken);
                return;
            }

            if (IsSelfInvoiceCommand(command))
            {
                await HandleSelfInvoiceAsync(message, cancellationToken);
                return;
            }

            if (IsHelpCommand(command))
            {
                await SendHelpAsync(message.Chat.Id, message.MessageId, cancellationToken);
                return;
            }

            await SendPrivateMenuAsync(message.Chat.Id, message.MessageId, cancellationToken);
            return;
        }

        if (!_allowedGroups.Contains(message.Chat.Id)) return;

        if (IsAddMemberCommand(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await HandleAddMemberAsync(message, cancellationToken);
            return;
        }

        if (IsFine200Command(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await HandleFine200Async(message, cancellationToken);
            return;
        }

        if (IsDebtorsCommand(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await SendDebtorsAsync(message.Chat.Id, message.MessageId, cancellationToken);
            return;
        }

        if (IsAdminHelpCommand(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await SendAdminHelpAsync(message.Chat.Id, message.MessageId, cancellationToken);
            return;
        }

        if (IsSelfInvoiceCommand(command))
        {
            await SendPrivateInvoiceHintAsync(message, cancellationToken);
            return;
        }

        if (IsRegistrationCommand(command))
        {
            var member = await UpsertMemberAsync(message.From, cancellationToken);
            await bale.SendMessageAsync(
                message.Chat.Id,
                $"✅ {member.DisplayName} به‌صورت خودکار به اعضای کیف‌تاخیر اضافه شد.",
                message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        if (command is "/registermembers" or "/ثبتاعضا")
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await SendRegistrationInvitationAsync(message, cancellationToken);
            return;
        }

        if (command is "/syncmembers" or "/همگامسازی")
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await SyncAvailableMembersAsync(message, cancellationToken);
            return;
        }

        if (command is "/status" or "/وضعیت")
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await SendStatusAsync(message.Chat.Id, message.MessageId, cancellationToken);
            return;
        }

        if (command is "/fund" or "/صندوق")
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await SendFundAsync(message.Chat.Id, message.MessageId, cancellationToken);
            return;
        }

        var mention = "@" + _options.BotUsername.Trim().TrimStart('@').ToLowerInvariant();
        var isDebtCommand = command is "/debt" or "/بدهی" ||
                            (!string.IsNullOrWhiteSpace(_options.BotUsername) &&
                             message.Text.Trim().Equals(mention, StringComparison.OrdinalIgnoreCase));

        if (isDebtCommand)
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await HandleAdminDebtRequestAsync(message, cancellationToken);
        }
    }

    private async Task RegisterFromPrivateAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        if (message.From is null) return;
        var member = await EnsurePrivateMemberAsync(message, cancellationToken);
        if (member is null) return;

        await bale.SendMessageAsync(
            message.Chat.Id,
            $"✅ {member.DisplayName}، عضویت شما در کیف‌تاخیر ثبت شد.\nاز منوی زیر می‌توانید بدهی و فاکتور پرداخت خود را دریافت کنید.",
            message.MessageId,
            BuildPrivateMenu(),
            cancellationToken);
    }

    private async Task<TeamMember?> EnsurePrivateMemberAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        if (message.From is null) return null;

        var groupId = _options.AllowedGroupChatIds.FirstOrDefault();
        if (groupId == 0)
        {
            await bale.SendMessageAsync(message.Chat.Id, "گروه مجاز در تنظیمات ثبت نشده است.", message.MessageId, cancellationToken: cancellationToken);
            return null;
        }

        try
        {
            var membership = await bale.GetChatMemberAsync(groupId, message.From.Id, cancellationToken);
            if (!IsCurrentGroupMember(membership))
            {
                await DeactivateMemberAsync(message.From.Id, cancellationToken);
                await bale.SendMessageAsync(message.Chat.Id, "این حساب در حال حاضر عضو گروه کیف‌تاخیر نیست و امکان دریافت صورت‌حساب ندارد.", message.MessageId, cancellationToken: cancellationToken);
                return null;
            }

            return await UpsertMemberAsync(message.From, cancellationToken);
        }
        catch (Exception ex) when (ex is BaleApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not validate Bale group membership for user {UserId}.", message.From.Id);
            await bale.SendMessageAsync(
                message.Chat.Id,
                "بررسی عضویت در گروه موقتاً ناموفق بود. چند لحظه بعد دوباره تلاش کنید.",
                message.MessageId,
                cancellationToken: cancellationToken);
            return null;
        }
    }

    private async Task SendPrivateMenuAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        await bale.SendMessageAsync(
            chatId,
            "سلام 👋\nبه کیف‌تاخیر خوش آمدید. برای مشاهده بدهی و دریافت فاکتور، یکی از گزینه‌های زیر را انتخاب کنید.",
            replyToMessageId,
            BuildPrivateMenu(),
            cancellationToken);
    }

    private static object BuildPrivateMenu() => new
    {
        keyboard = new object[]
        {
            new[] { "📄 صورت‌حساب من", "💳 پرداخت بدهی" },
            new[] { "ℹ️ راهنما" }
        },
        resize_keyboard = true
    };

    private async Task SendHelpAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        await bale.SendMessageAsync(
            chatId,
            "راهنمای کیف‌تاخیر 👇\n\n📄 «صورت‌حساب من» یا /bill: مشاهده بدهی و دریافت کارت پرداخت\n💳 «پرداخت بدهی»: صدور فاکتور برای بدهی فعلی\n✅ /start: ثبت یا به‌روزرسانی عضویت\n\nپس از پرداخت موفق، مبلغ به‌صورت خودکار از بدهی شما کم، موجودی صندوق به‌روزرسانی و گزارش جدید در گروه منتشر می‌شود.",
            replyToMessageId,
            BuildPrivateMenu(),
            cancellationToken);
    }

    private async Task HandleSelfInvoiceAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        if (message.From is null) return;
        var member = await EnsurePrivateMemberAsync(message, cancellationToken);
        if (member is null) return;

        var debt = await GetDebtAsync(member.Id, cancellationToken);
        if (debt <= 0)
        {
            await bale.SendMessageAsync(
                message.Chat.Id,
                $"✅ {member.DisplayName} عزیز، حساب شما تسویه است و بدهی بازی ندارید.",
                message.MessageId,
                BuildPrivateMenu(),
                cancellationToken);
            return;
        }

        await bale.SendMessageAsync(
            message.Chat.Id,
            $"📄 صورت‌حساب {member.DisplayName}\nمبلغ بدهی فعلی: {money.Format(debt)}\n\nکارت پرداخت زیر فقط توسط حساب بله شما قابل پرداخت است و ۳۰ دقیقه اعتبار دارد. روی دکمه «پرداخت» بزنید.",
            message.MessageId,
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(_options.ProviderToken))
        {
            await bale.SendMessageAsync(
                message.Chat.Id,
                "⚠️ درگاه پرداخت هنوز در تنظیمات فعال نشده است. مبلغ بدهی شما نمایش داده شد، اما فاکتور صادر نشد.",
                message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        var announcementGroupId = _options.AllowedGroupChatIds.FirstOrDefault();
        await CreateAndSendInvoiceAsync(
            member,
            message.From.Id,
            message.Chat.Id,
            announcementGroupId,
            message.MessageId,
            debt,
            cancellationToken);
    }

    private async Task SendPrivateInvoiceHintAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var username = _options.BotUsername.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
        {
            await bale.SendMessageAsync(message.Chat.Id, "نام کاربری بات در تنظیمات ثبت نشده است.", message.MessageId, cancellationToken: cancellationToken);
            return;
        }

        var keyboard = new
        {
            inline_keyboard = new[]
            {
                new[] { new { text = "📄 دریافت صورت‌حساب خصوصی", url = $"https://ble.ir/{username}" } }
            }
        };

        await bale.SendMessageAsync(
            message.Chat.Id,
            $"🔐 {message.From?.DisplayName ?? "عضو گروه"}، برای حفظ حریم خصوصی، صورت‌حساب و فاکتور پرداخت در گفت‌وگوی خصوصی بات ارسال می‌شود.\nپس از ورود، پیام «صورتحساب» یا /bill را بفرستید.",
            message.MessageId,
            keyboard,
            cancellationToken);
    }

    private async Task SendRegistrationInvitationAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var username = _options.BotUsername.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
        {
            await bale.SendMessageAsync(message.Chat.Id, "نام کاربری بات در تنظیمات ثبت نشده است.", message.MessageId, cancellationToken: cancellationToken);
            return;
        }

        var keyboard = new
        {
            inline_keyboard = new[]
            {
                new[] { new { text = "✅ شروع و ثبت خودکار عضویت", url = $"https://ble.ir/{username}" } }
            }
        };

        await bale.SendMessageAsync(
            message.Chat.Id,
            "👥 ثبت خودکار اعضای کیف‌تاخیر\nهر عضو یک‌بار دکمه زیر را بزند و در گفت‌وگوی خصوصی بات روی «شروع» بزند.",
            message.MessageId,
            keyboard,
            cancellationToken);
    }

    private async Task SyncAvailableMembersAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var count = await bale.GetChatMembersCountAsync(message.Chat.Id, cancellationToken);
            var admins = await bale.GetChatAdministratorsAsync(message.Chat.Id, cancellationToken);
            var registered = 0;
            foreach (var administrator in admins.Where(x => !x.User.IsBot))
            {
                await ObserveUserAsync(administrator.User, message.Chat.Id, cancellationToken);
                await UpsertMemberAsync(administrator.User, cancellationToken);
                registered++;
            }

            await bale.SendMessageAsync(
                message.Chat.Id,
                $"✅ همگام‌سازی انجام شد.\nتعداد کل اعضای گروه: {count:N0}\nمدیران قابل دریافت و ثبت‌شده: {registered:N0}\nاعضای عادی با زدن دکمه ثبت عضویت اضافه می‌شوند.",
                message.MessageId,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is BaleApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Bale member synchronization failed for group {ChatId}.", message.Chat.Id);
            await bale.SendMessageAsync(message.Chat.Id, "همگام‌سازی موقتاً ناموفق بود.", message.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAddMemberAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var target = message.ReplyToMessage?.From;
        if (target is null || target.IsBot)
        {
            await bale.SendMessageAsync(
                message.Chat.Id,
                "برای ثبت عضو، روی یکی از پیام‌های او Reply بزنید و /addmember یا «ثبت عضو» را ارسال کنید.",
                message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        var member = await UpsertMemberAsync(target, cancellationToken);
        var debt = Math.Max(0, await GetDebtAsync(member.Id, cancellationToken));
        await bale.SendMessageAsync(
            message.Chat.Id,
            $"✅ {member.DisplayName} به اعضای کیف‌تاخیر اضافه شد.\nبدهی فعلی: {money.Format(debt)}",
            message.MessageId,
            cancellationToken: cancellationToken);
    }

    private async Task HandleFine200Async(BaleMessage message, CancellationToken cancellationToken)
    {
        var target = message.ReplyToMessage?.From;
        if (target is null || target.IsBot)
        {
            await bale.SendMessageAsync(
                message.Chat.Id,
                "برای ثبت جریمه ۲۰۰,۰۰۰ تومانی، روی پیام شخص Reply بزنید و /fine200 را ارسال کنید.",
                message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        const long fineRials = 2_000_000;
        var member = await UpsertMemberAsync(target, cancellationToken);
        var newDebt = await store.WriteAsync(state =>
        {
            state.DebtEntries.Add(new DebtLedgerEntry
            {
                TeamMemberId = member.Id,
                AmountRials = fineRials,
                Kind = DebtEntryKind.Penalty,
                Description = "جریمه ثابت ۲۰۰,۰۰۰ تومانی ثبت‌شده توسط مدیر در گروه"
            });

            foreach (var payment in state.Payments.Where(x =>
                         x.TeamMemberId == member.Id &&
                         x.Status is PaymentRequestStatus.Pending or PaymentRequestStatus.Approved))
            {
                payment.Status = PaymentRequestStatus.Expired;
                payment.RejectionReason = "مبلغ بدهی پس از صدور فاکتور تغییر کرد.";
            }

            return Math.Max(0, state.DebtEntries
                .Where(x => x.TeamMemberId == member.Id)
                .Sum(x => x.AmountRials));
        }, cancellationToken);

        await bale.SendMessageAsync(
            message.Chat.Id,
            $"✅ برای {member.DisplayName} جریمه ثبت شد.\nمبلغ جریمه: ۲۰۰,۰۰۰ تومان\nبدهی جدید: {money.Format(newDebt)}",
            message.MessageId,
            cancellationToken: cancellationToken);
    }

    private async Task SendDebtorsAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        var data = await store.ReadAsync(state =>
        {
            var debtMap = state.DebtEntries
                .GroupBy(x => x.TeamMemberId)
                .ToDictionary(x => x.Key, x => Math.Max(0, x.Sum(y => y.AmountRials)));

            var debtors = state.Members
                .Where(x => x.IsActive && debtMap.GetValueOrDefault(x.Id) > 0)
                .OrderByDescending(x => debtMap.GetValueOrDefault(x.Id))
                .Select(x => new
                {
                    x.DisplayName,
                    Debt = debtMap.GetValueOrDefault(x.Id)
                })
                .ToArray();

            return new
            {
                Count = debtors.Length,
                Total = debtors.Sum(x => x.Debt),
                Lines = debtors.Select((x, index) => $"{index + 1}. {x.DisplayName}: {money.Format(x.Debt)}").ToArray()
            };
        }, cancellationToken);

        var details = data.Lines.Length == 0
            ? "هیچ بدهکار فعالی وجود ندارد."
            : string.Join("\n", data.Lines);

        await SendLongMessageAsync(
            chatId,
            $"📋 فهرست بدهکاران کیف‌تاخیر\nتعداد بدهکاران: {data.Count:N0}\nجمع کل بدهی: {money.Format(data.Total)}\n\n{details}",
            replyToMessageId,
            cancellationToken);
    }

    private async Task SendAdminHelpAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        await bale.SendMessageAsync(
            chatId,
            "راهنمای مدیر کیف‌تاخیر 👇\n\n" +
            "👤 ثبت عضو با Reply: /addmember یا «ثبت عضو»\n" +
            "💸 ثبت جریمه ۲۰۰,۰۰۰ تومانی با Reply: /fine200\n" +
            "👥 دعوت همه اعضا برای ثبت: /registermembers\n" +
            "🔄 همگام‌سازی مدیران گروه: /syncmembers\n" +
            "📋 فهرست بدهکاران: /debtors\n" +
            "📊 گزارش کلی اعضا و بدهی: /status\n" +
            "💰 گزارش موجودی صندوق: /fund\n" +
            "💳 نمایش بدهی شخص با Reply: /debt\n\n" +
            "اعضا در خصوصی بات با /bill یا /pay صورت‌حساب و کارت پرداخت خود را دریافت می‌کنند.",
            replyToMessageId,
            cancellationToken: cancellationToken);
    }

    private async Task SendStatusAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        var data = await store.ReadAsync(state =>
        {
            var active = state.Members.Where(x => x.IsActive).ToArray();
            var debtMap = state.DebtEntries.GroupBy(x => x.TeamMemberId).ToDictionary(x => x.Key, x => x.Sum(y => y.AmountRials));
            var debtors = active.Where(x => debtMap.GetValueOrDefault(x.Id) > 0).ToArray();
            var total = debtors.Sum(x => debtMap.GetValueOrDefault(x.Id));
            var lines = debtors
                .OrderByDescending(x => debtMap.GetValueOrDefault(x.Id))
                .Select(x => $"• {x.DisplayName}: {money.Format(debtMap.GetValueOrDefault(x.Id))}")
                .ToArray();
            return new { Active = active.Length, DebtorCount = debtors.Length, Total = total, Lines = lines };
        }, cancellationToken);

        var details = data.Lines.Length == 0 ? "\nهیچ بدهی بازی وجود ندارد." : "\n\n" + string.Join("\n", data.Lines);
        await SendLongMessageAsync(
            chatId,
            $"📊 وضعیت کیف‌تاخیر\nاعضای فعال: {data.Active:N0}\nافراد بدهکار: {data.DebtorCount:N0}\nکل بدهی باز: {money.Format(data.Total)}{details}",
            replyToMessageId,
            cancellationToken);
    }

    private async Task SendFundAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        var data = await store.ReadAsync(state => new
        {
            Balance = state.FundEntries.Sum(x => x.AmountRials),
            Collected = state.FundEntries.Where(x => x.Kind == FundEntryKind.Payment).Sum(x => x.AmountRials),
            Expenses = -state.FundEntries.Where(x => x.AmountRials < 0).Sum(x => x.AmountRials),
            PaidCount = state.Payments.Count(x => x.Status == PaymentRequestStatus.Paid)
        }, cancellationToken);

        await bale.SendMessageAsync(
            chatId,
            $"💰 گزارش کیف‌تاخیر\nموجودی فعلی: {money.Format(data.Balance)}\nجمع جریمه‌های پرداخت‌شده: {money.Format(data.Collected)}\nجمع خروجی‌ها/هزینه‌ها: {money.Format(data.Expenses)}\nتعداد پرداخت موفق: {data.PaidCount:N0}",
            replyToMessageId,
            cancellationToken: cancellationToken);
    }

    private async Task SendLongMessageAsync(
        long chatId,
        string text,
        long? replyToMessageId,
        CancellationToken cancellationToken)
    {
        const int maxLength = 3900;
        if (text.Length <= maxLength)
        {
            await bale.SendMessageAsync(
                chatId,
                text,
                replyToMessageId,
                cancellationToken: cancellationToken);
            return;
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > maxLength)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            if (line.Length > maxLength)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                for (var offset = 0; offset < line.Length; offset += maxLength)
                {
                    chunks.Add(line.Substring(offset, Math.Min(maxLength, line.Length - offset)));
                }
                continue;
            }

            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }

        if (current.Length > 0) chunks.Add(current.ToString());

        for (var index = 0; index < chunks.Count; index++)
        {
            await bale.SendMessageAsync(
                chatId,
                chunks[index],
                index == 0 ? replyToMessageId : null,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAdminDebtRequestAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var target = message.ReplyToMessage?.From;
        if (target is null || target.IsBot)
        {
            await bale.SendMessageAsync(message.Chat.Id, "این فرمان را در پاسخ به پیام شخص بدهکار ارسال کنید.", message.MessageId, cancellationToken: cancellationToken);
            return;
        }

        var member = await UpsertMemberAsync(target, cancellationToken);
        var debt = await GetDebtAsync(member.Id, cancellationToken);

        if (debt <= 0)
        {
            await bale.SendMessageAsync(message.Chat.Id, $"✅ {member.DisplayName} بدهی بازی ندارد.", message.MessageId, cancellationToken: cancellationToken);
            return;
        }

        await bale.SendMessageAsync(
            message.Chat.Id,
            $"💳 بدهی {member.DisplayName}: {money.Format(debt)}\nفاکتور فقط توسط خود این شخص قابل پرداخت است.",
            message.MessageId,
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(_options.ProviderToken)) return;
        await CreateAndSendInvoiceAsync(
            member,
            target.Id,
            message.Chat.Id,
            message.Chat.Id,
            message.MessageId,
            debt,
            cancellationToken);
    }

    private async Task CreateAndSendInvoiceAsync(
        TeamMember member,
        long baleUserId,
        long invoiceChatId,
        long announcementGroupChatId,
        long sourceMessageId,
        long debtRials,
        CancellationToken cancellationToken)
    {
        var payment = await store.WriteAsync(state =>
        {
            foreach (var pending in state.Payments.Where(x =>
                         x.TeamMemberId == member.Id &&
                         x.Status is PaymentRequestStatus.Pending or PaymentRequestStatus.Approved))
            {
                pending.Status = PaymentRequestStatus.Expired;
            }

            var item = new PaymentRequest
            {
                Payload = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                TeamMemberId = member.Id,
                BaleUserId = baleUserId,
                AmountRials = debtRials,
                GroupChatId = announcementGroupChatId,
                SourceMessageId = sourceMessageId,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            };
            state.Payments.Add(item);
            return item;
        }, cancellationToken);

        try
        {
            await bale.SendInvoiceAsync(
                invoiceChatId,
                "تسویه کیف‌تاخیر",
                $"پرداخت بدهی {member.DisplayName}",
                payment.Payload,
                payment.AmountRials,
                sourceMessageId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send invoice for payment request {PaymentId}.", payment.Id);
            await store.WriteAsync(state =>
            {
                var current = state.Payments.FirstOrDefault(x => x.Id == payment.Id);
                if (current is not null)
                {
                    current.Status = PaymentRequestStatus.Rejected;
                    current.RejectionReason = ex.Message;
                }
            }, cancellationToken);
            await bale.SendMessageAsync(invoiceChatId, "ساخت فاکتور پرداخت ناموفق بود؛ بدهی در پنل ثبت باقی مانده است.", sourceMessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task<long> GetDebtAsync(Guid memberId, CancellationToken cancellationToken)
        => await store.ReadAsync(
            state => state.DebtEntries.Where(x => x.TeamMemberId == memberId).Sum(x => x.AmountRials),
            cancellationToken);

    private async Task HandlePreCheckoutAsync(BalePreCheckoutQuery query, CancellationToken cancellationToken)
    {
        var validation = await store.WriteAsync(state =>
        {
            var payment = state.Payments.FirstOrDefault(x => x.Payload == query.InvoicePayload);
            if (payment is null) return (Ok: false, Error: "درخواست پرداخت پیدا نشد.");
            if (payment.Status is PaymentRequestStatus.Paid or PaymentRequestStatus.Rejected or PaymentRequestStatus.Expired)
                return (Ok: false, Error: "این درخواست پرداخت دیگر معتبر نیست.");
            if (payment.ExpiresAt <= DateTime.UtcNow)
            {
                payment.Status = PaymentRequestStatus.Expired;
                return (Ok: false, Error: "مهلت پرداخت این فاکتور تمام شده است.");
            }
            if (query.From.Id != payment.BaleUserId)
                return (Ok: false, Error: "این فاکتور فقط توسط شخص بدهکار قابل پرداخت است.");
            if (!string.Equals(query.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase) || query.TotalAmount != payment.AmountRials)
                return (Ok: false, Error: "مبلغ یا واحد پول فاکتور معتبر نیست.");

            var currentDebt = state.DebtEntries.Where(x => x.TeamMemberId == payment.TeamMemberId).Sum(x => x.AmountRials);
            if (currentDebt < payment.AmountRials)
            {
                payment.Status = PaymentRequestStatus.Expired;
                return (Ok: false, Error: "مبلغ بدهی از زمان صدور فاکتور تغییر کرده است. صورت‌حساب جدید بگیرید.");
            }

            payment.Status = PaymentRequestStatus.Approved;
            payment.PreCheckoutQueryId = query.Id;
            payment.ApprovedAt = DateTime.UtcNow;
            return (Ok: true, Error: string.Empty);
        }, cancellationToken);

        await bale.AnswerPreCheckoutQueryAsync(query.Id, validation.Ok, validation.Error, cancellationToken);
    }

    private async Task HandleSuccessfulPaymentAsync(
        BaleMessage message,
        BaleSuccessfulPayment successfulPayment,
        CancellationToken cancellationToken)
    {
        if (message.From is null) return;

        var result = await store.WriteAsync(state =>
        {
            var payment = state.Payments.FirstOrDefault(x => x.Payload == successfulPayment.InvoicePayload);
            if (payment is null || payment.Status != PaymentRequestStatus.Approved)
                return (Done: false, Name: string.Empty, TotalPaid: 0L, Applied: 0L, Overpayment: 0L, RemainingDebt: 0L, FundBalance: 0L, GroupChatId: 0L);

            if (payment.BaleUserId != message.From.Id || payment.AmountRials != successfulPayment.TotalAmount ||
                !string.Equals(payment.Currency, successfulPayment.Currency, StringComparison.OrdinalIgnoreCase))
                return (Done: false, Name: string.Empty, TotalPaid: 0L, Applied: 0L, Overpayment: 0L, RemainingDebt: 0L, FundBalance: 0L, GroupChatId: 0L);

            var duplicateCharge = state.Payments.Any(x =>
                x.Id != payment.Id &&
                x.Status == PaymentRequestStatus.Paid &&
                ((!string.IsNullOrWhiteSpace(successfulPayment.ProviderPaymentChargeId) &&
                  x.ProviderPaymentChargeId == successfulPayment.ProviderPaymentChargeId) ||
                 (!string.IsNullOrWhiteSpace(successfulPayment.TelegramPaymentChargeId) &&
                  x.TelegramPaymentChargeId == successfulPayment.TelegramPaymentChargeId)));
            if (duplicateCharge)
                return (Done: false, Name: string.Empty, TotalPaid: 0L, Applied: 0L, Overpayment: 0L, RemainingDebt: 0L, FundBalance: 0L, GroupChatId: 0L);

            var currentDebt = Math.Max(0, state.DebtEntries
                .Where(x => x.TeamMemberId == payment.TeamMemberId)
                .Sum(x => x.AmountRials));
            var applied = Math.Min(currentDebt, payment.AmountRials);
            var overpayment = payment.AmountRials - applied;

            payment.Status = PaymentRequestStatus.Paid;
            payment.PaidAt = DateTime.UtcNow;
            payment.TelegramPaymentChargeId = successfulPayment.TelegramPaymentChargeId;
            payment.ProviderPaymentChargeId = successfulPayment.ProviderPaymentChargeId;
            payment.AppliedDebtRials = applied;
            payment.OverpaymentRials = overpayment;

            if (applied > 0)
            {
                state.DebtEntries.Add(new DebtLedgerEntry
                {
                    TeamMemberId = payment.TeamMemberId,
                    AmountRials = -applied,
                    Kind = DebtEntryKind.Payment,
                    Description = "پرداخت جریمه از بله",
                    PaymentRequestId = payment.Id
                });
            }

            state.FundEntries.Add(new FundLedgerEntry
            {
                AmountRials = payment.AmountRials,
                Kind = FundEntryKind.Payment,
                Description = overpayment > 0 ? "واریز جریمه از بله همراه مازاد پرداخت" : "واریز جریمه از بله",
                PaymentRequestId = payment.Id
            });

            var name = state.Members.FirstOrDefault(x => x.Id == payment.TeamMemberId)?.DisplayName
                       ?? message.From.DisplayName;
            var remainingDebt = Math.Max(0, currentDebt - applied);
            var fundBalance = state.FundEntries.Sum(x => x.AmountRials);

            return (
                Done: true,
                Name: name,
                TotalPaid: payment.AmountRials,
                Applied: applied,
                Overpayment: overpayment,
                RemainingDebt: remainingDebt,
                FundBalance: fundBalance,
                GroupChatId: payment.GroupChatId);
        }, cancellationToken);

        if (!result.Done) return;

        var privateText = result.Overpayment > 0
            ? $"✅ پرداخت شما با موفقیت ثبت شد.\nمبلغ پرداخت‌شده: {money.Format(result.TotalPaid)}\nکسر از بدهی: {money.Format(result.Applied)}\nمازاد ثبت‌شده در صندوق: {money.Format(result.Overpayment)}\nمانده بدهی شما: {money.Format(result.RemainingDebt)}"
            : $"✅ پرداخت شما با موفقیت ثبت شد.\nمبلغ پرداخت‌شده: {money.Format(result.TotalPaid)}\nمانده بدهی شما: {money.Format(result.RemainingDebt)}";

        var groupText =
            $"✅ پرداخت جدید در کیف‌تاخیر\n" +
            $"پرداخت‌کننده: {result.Name}\n" +
            $"مبلغ پرداخت: {money.Format(result.TotalPaid)}\n" +
            $"مانده بدهی فرد: {money.Format(result.RemainingDebt)}\n" +
            $"موجودی جدید صندوق: {money.Format(result.FundBalance)}";

        if (result.GroupChatId <= 0 || result.GroupChatId != message.Chat.Id)
        {
            try
            {
                await bale.SendMessageAsync(
                    message.Chat.Id,
                    privateText,
                    message.MessageId,
                    BuildPrivateMenu(),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Payment {Payload} was saved but the payer confirmation could not be sent.", successfulPayment.InvoicePayload);
            }
        }

        if (result.GroupChatId > 0)
        {
            try
            {
                await bale.SendMessageAsync(
                    result.GroupChatId,
                    groupText,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Payment {Payload} was saved but the group fund announcement could not be sent.", successfulPayment.InvoicePayload);
            }
        }
    }

    private async Task ObserveUserAsync(BaleUser user, long chatId, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? $"کاربر {user.Id}" : user.DisplayName;
        await store.WriteAsync(state => state.ObservedUsers[user.Id] = new ObservedBaleUser
        {
            BaleUserId = user.Id,
            DisplayName = name,
            Username = NormalizeUsername(user.Username),
            LastChatId = chatId,
            LastSeenAt = DateTime.UtcNow
        }, cancellationToken);
    }

    private Task<TeamMember> UpsertMemberAsync(BaleUser user, CancellationToken cancellationToken)
        => store.WriteAsync(state =>
        {
            var name = string.IsNullOrWhiteSpace(user.DisplayName) ? $"کاربر {user.Id}" : user.DisplayName;
            var member = state.Members.FirstOrDefault(x => x.BaleUserId == user.Id);
            if (member is null)
            {
                member = new TeamMember
                {
                    DisplayName = name,
                    BaleUserId = user.Id,
                    BaleUsername = NormalizeUsername(user.Username)
                };
                state.Members.Add(member);
            }
            else
            {
                member.DisplayName = name;
                member.BaleUsername = NormalizeUsername(user.Username);
                member.IsActive = true;
                member.UpdatedAt = DateTime.UtcNow;
            }
            return member;
        }, cancellationToken);

    private Task DeactivateMemberAsync(long baleUserId, CancellationToken cancellationToken)
        => store.WriteAsync(state =>
        {
            var member = state.Members.FirstOrDefault(x => x.BaleUserId == baleUserId);
            if (member is null) return;
            member.IsActive = false;
            member.UpdatedAt = DateTime.UtcNow;
            foreach (var payment in state.Payments.Where(x =>
                         x.TeamMemberId == member.Id &&
                         x.Status is PaymentRequestStatus.Pending or PaymentRequestStatus.Approved))
            {
                payment.Status = PaymentRequestStatus.Expired;
            }
        }, cancellationToken);

    private async Task<bool> EnsureAdminAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        if (message.From is not null && _adminIds.Contains(message.From.Id)) return true;
        await bale.SendMessageAsync(message.Chat.Id, "این فرمان فقط برای مدیر کیف‌تاخیر فعال است.", message.MessageId, cancellationToken: cancellationToken);
        return false;
    }

    private static bool IsRegistrationCommand(string command)
        => command is "/start" or "/register" or "/عضویت" || command.StartsWith("/start ", StringComparison.Ordinal);

    private static bool IsHelpCommand(string command)
        => command is "/help" or "/راهنما" or "راهنما" or "ℹ️ راهنما";

    private static bool IsSelfInvoiceCommand(string command)
    {
        var normalized = command.Replace('‌', ' ').Replace("  ", " ").Trim();
        return normalized is
            "/bill" or "/invoice" or "/pay" or "/mydebt" or "/صورتحساب" or "/حساب" or "/پرداخت" or "/بدهیمن" or "/بدهی_من" or
            "صورتحساب" or "صورت حساب" or "صورت‌حساب" or "صورتحساب من" or "صورت حساب من" or "صورت‌حساب من" or
            "بدهی من" or "مشاهده بدهی" or "پرداخت" or "پرداخت بدهی" or "پرداخت بدهی من" or
            "📄 صورتحساب من" or "📄 صورت حساب من" or "📄 صورت‌حساب من" or "💳 پرداخت بدهی";
    }

    private static bool IsAddMemberCommand(string command)
    {
        var normalized = command.Replace('‌', ' ').Replace("  ", " ").Trim();
        return normalized is
            "/addmember" or "/member" or "/ثبتعضو" or "/ثبت_عضو" or
            "ثبت عضو" or "عضو کن" or "ثبتش کن";
    }

    private static bool IsFine200Command(string command)
    {
        var normalized = command
            .Replace('‌', ' ')
            .Replace("٬", string.Empty)
            .Replace(",", string.Empty)
            .Replace("  ", " ")
            .Trim();

        return normalized is
            "/fine200" or "/penalty200" or "/جریمه200" or "/جریمه_200" or
            "جریمه 200" or "جریمه ۲۰۰" or "جریمه 200000" or "جریمه ۲۰۰۰۰۰" or
            "جریمه دویست" or "دویست جریمه";
    }

    private static bool IsDebtorsCommand(string command)
    {
        var normalized = command.Replace('‌', ' ').Replace("  ", " ").Trim();
        return normalized is "/debtors" or "/debtorlist" or "/بدهکاران" or "لیست بدهکاران" or "فهرست بدهکاران";
    }

    private static bool IsAdminHelpCommand(string command)
        => command is "/commands" or "/adminhelp" or "/دستورات" or "دستورات مدیر";

    private string NormalizeCommand(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        var username = _options.BotUsername.Trim().TrimStart('@').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(username))
        {
            normalized = normalized.Replace("@" + username, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }
        return normalized;
    }

    private static bool IsCurrentGroupMember(BaleChatMember membership)
        => membership.Status.ToLowerInvariant() switch
        {
            "creator" => true,
            "administrator" => true,
            "member" => true,
            "restricted" => membership.IsMember is not false,
            _ => false
        };

    private static string? NormalizeUsername(string? username)
    {
        var result = username?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
