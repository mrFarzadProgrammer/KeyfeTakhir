using System.Collections.Concurrent;
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
    private static readonly TimeSpan AdminCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly BaleOptions _options = options.Value;
    private readonly HashSet<long> _adminIds = options.Value.AdminUserIds.ToHashSet();
    private readonly ConcurrentDictionary<(long ChatId, long UserId), DateTime> _groupAdminCache = new();

    public async Task HandleAsync(BaleUpdate update, CancellationToken cancellationToken)
    {
        if (update.PreCheckoutQuery is not null)
        {
            await HandlePreCheckoutAsync(update.PreCheckoutQuery, cancellationToken);
            return;
        }

        var message = update.Message ?? update.EditedMessage;
        if (message is null) return;

        var isGroupChat = IsGroupChat(message.Chat);

        // The bot only receives group updates for chats it is a member of, so an
        // unknown group means the bot was added without the panel seeing it happen.
        if (isGroupChat)
        {
            await EnsureGroupRegisteredAsync(message, cancellationToken);
        }

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
                if (isGroupChat)
                {
                    await UpsertMemberAsync(user, message.Chat.Id, cancellationToken);
                }
            }
        }

        if (message.LeftChatMember is not null && isGroupChat)
        {
            if (IsSelf(message.LeftChatMember))
            {
                await DeactivateGroupAsync(message.Chat.Id, cancellationToken);
            }
            else if (!message.LeftChatMember.IsBot)
            {
                await DeactivateMemberAsync(message.LeftChatMember.Id, message.Chat.Id, cancellationToken);
            }
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

        if (!isGroupChat) return;
        if (!await IsGroupActiveAsync(message.Chat.Id, cancellationToken)) return;

        if (IsAddMemberCommand(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await HandleAddMemberAsync(message, cancellationToken);
            return;
        }

        if (IsRemoveMemberCommand(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await HandleRemoveMemberAsync(message, cancellationToken);
            return;
        }

        if (IsFine200Command(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await HandleFine200Async(message, cancellationToken);
            return;
        }

        if (IsFineAllCommand(command))
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await HandleFineAllAsync(message, cancellationToken);
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
            var member = await UpsertMemberAsync(message.From, message.Chat.Id, cancellationToken);
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

        if (command is "/addobserved" or "/ثبتهمه")
        {
            if (!await EnsureAdminAsync(message, cancellationToken)) return;
            await AddObservedMembersAsync(message, cancellationToken);
            return;
        }

        if (command is "/fund" or "/صندوق")
        {
            // Every member may see the fund balance; the detailed breakdown is admin-only.
            var isAdmin = await IsGroupAdminAsync(message, quiet: true, cancellationToken);
            await SendFundAsync(message.Chat.Id, message.MessageId, isAdmin, cancellationToken);
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

    private bool IsSelf(BaleUser user)
    {
        if (!user.IsBot) return false;
        var username = _options.BotUsername.Trim().TrimStart('@');
        return !string.IsNullOrWhiteSpace(username) &&
               string.Equals(user.Username?.Trim().TrimStart('@'), username, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGroupChat(BaleChat chat)
        => string.Equals(chat.Type, "group", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(chat.Type, "supergroup", StringComparison.OrdinalIgnoreCase);

    private async Task EnsureGroupRegisteredAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var registration = await store.WriteAsync(state =>
        {
            var group = state.Groups.FirstOrDefault(x => x.ChatId == message.Chat.Id);
            if (group is null)
            {
                // Exactly two active groups are supported: the first one added becomes
                // the main group and the second one the test group. Removing the bot
                // frees the slot again.
                var activeGroups = state.Groups.Count(x => x.IsActive && x.ChatId != 0);
                if (activeGroups >= 2)
                {
                    return (Registered: false, Rejected: true);
                }

                var isMain = activeGroups == 0;
                state.Groups.Add(new GroupInfo
                {
                    ChatId = message.Chat.Id,
                    Title = isMain ? "گروه اصلی" : "گروه تست"
                });
                return (Registered: true, Rejected: false);
            }

            if (!group.IsActive)
            {
                // The bot was removed earlier and re-added; make the group usable again.
                group.IsActive = true;
                return (Registered: true, Rejected: false);
            }

            return (Registered: false, Rejected: false);
        }, cancellationToken);

        if (registration.Rejected)
        {
            try
            {
                await bale.SendMessageAsync(
                    message.Chat.Id,
                    "⚠️ کیف‌تاخیر فقط در دو گروه (اصلی و تست) فعال می‌شود و ظرفیت آن پر است.",
                    message.MessageId,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is BaleApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not send the group limit message for {ChatId}.", message.Chat.Id);
            }
            return;
        }

        if (registration.Registered)
        {
            _groupAdminCache.Clear();
            try
            {
                await bale.SendMessageAsync(
                    message.Chat.Id,
                    "سلام 👋\nکیف‌تاخیر به این گروه اضافه شد و به‌عنوان «گروه اصلی» یا «گروه تست» ثبت شد.\nمدیران گروه می‌توانند با /commands راهنمای فرمان‌ها را ببینند؛ اعضا در گفت‌وگوی خصوصی بات صورت‌حساب خود را دریافت می‌کنند.",
                    message.MessageId,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is BaleApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not send the group welcome message for {ChatId}.", message.Chat.Id);
            }
        }
    }

    private Task DeactivateGroupAsync(long chatId, CancellationToken cancellationToken)
        => store.WriteAsync(state =>
        {
            var group = state.Groups.FirstOrDefault(x => x.ChatId == chatId);
            if (group is null) return;
            group.IsActive = false;
            group.UpdatedAt = DateTime.UtcNow;

            var memberIds = state.Members.Where(x => x.GroupChatId == chatId).Select(x => x.Id).ToHashSet();
            foreach (var payment in state.Payments.Where(x =>
                         x.GroupChatId == chatId &&
                         memberIds.Contains(x.TeamMemberId) &&
                         x.Status is PaymentRequestStatus.Pending or PaymentRequestStatus.Approved))
            {
                payment.Status = PaymentRequestStatus.Expired;
                payment.RejectionReason = "بات از گروه حذف شد.";
            }
        }, cancellationToken);

    private async Task<bool> IsGroupActiveAsync(long chatId, CancellationToken cancellationToken)
        => await store.ReadAsync(
            state => state.Groups.Any(x => x.ChatId == chatId && x.IsActive),
            cancellationToken);

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

        var groups = await store.ReadAsync(
            state => state.Groups.Where(x => x.IsActive && x.ChatId != 0).Select(x => x.ChatId).ToArray(),
            cancellationToken);
        if (groups.Length == 0)
        {
            await bale.SendMessageAsync(message.Chat.Id, "هنوز گروه فعالی برای کیف‌تاخیر ثبت نشده است.", message.MessageId, cancellationToken: cancellationToken);
            return null;
        }

        foreach (var groupId in groups)
        {
            try
            {
                var membership = await bale.GetChatMemberAsync(groupId, message.From.Id, cancellationToken);
                if (!IsCurrentGroupMember(membership)) continue;

                return await UpsertMemberAsync(message.From, groupId, cancellationToken);
            }
            catch (Exception ex) when (ex is BaleApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not validate Bale group membership for user {UserId} in group {ChatId}.", message.From.Id, groupId);
            }
        }

        await DeactivateMemberEverywhereAsync(message.From.Id, cancellationToken);
        await bale.SendMessageAsync(message.Chat.Id, "این حساب در حال حاضر عضو هیچ گروه فعال کیف‌تاخیر نیست و امکان دریافت صورت‌حساب ندارد.", message.MessageId, cancellationToken: cancellationToken);
        return null;
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

        await CreateAndSendInvoiceAsync(
            member,
            message.From.Id,
            message.Chat.Id,
            member.GroupChatId,
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
                await UpsertMemberAsync(administrator.User, message.Chat.Id, cancellationToken);
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

    private async Task AddObservedMembersAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var names = await store.WriteAsync(state =>
        {
            // Bale has no API to list group members, so register everyone the bot has
            // already seen writing in this group.
            var candidates = state.ObservedUsers.Values
                .Where(x => x.LastChatId == message.Chat.Id)
                .ToArray();

            var added = new List<string>();
            foreach (var candidate in candidates)
            {
                if (state.Members.Any(x => x.BaleUserId == candidate.BaleUserId && x.GroupChatId == message.Chat.Id))
                {
                    continue;
                }

                var member = new TeamMember
                {
                    DisplayName = string.IsNullOrWhiteSpace(candidate.DisplayName) ? $"کاربر {candidate.BaleUserId}" : candidate.DisplayName,
                    GroupChatId = message.Chat.Id,
                    BaleUserId = candidate.BaleUserId,
                    BaleUsername = candidate.Username
                };
                state.Members.Add(member);
                added.Add(member.DisplayName);
            }

            return added;
        }, cancellationToken);

        var details = names.Count == 0
            ? "همه کاربران دیده‌شده این گروه قبلاً عضو ثبت شده‌اند."
            : string.Join("\n", names.Select((name, index) => $"{index + 1}. {name}"));

        await SendLongMessageAsync(
            message.Chat.Id,
            $"✅ ثبت کاربران دیده‌شده انجام شد.\nتعداد افزوده‌شده: {names.Count:N0}\n\n{details}\n\nکاربرانی که هنوز در این گروه پیامی نداده‌اند، با Reply و /addmember یا /start در خصوصی بات اضافه می‌شوند.",
            message.MessageId,
            cancellationToken);
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

        var member = await UpsertMemberAsync(target, message.Chat.Id, cancellationToken);
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
        var member = await UpsertMemberAsync(target, message.Chat.Id, cancellationToken);
        var newDebt = await store.WriteAsync(state =>
        {
            state.DebtEntries.Add(new DebtLedgerEntry
            {
                TeamMemberId = member.Id,
                GroupChatId = message.Chat.Id,
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

    private async Task HandleFineAllAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var amountTomans = ParseAmountArgument(message.Text);
        if (amountTomans is null || amountTomans <= 0)
        {
            await bale.SendMessageAsync(
                message.Chat.Id,
                "شکل درست فرمان:\n/fineall 50000\nیعنی ۵۰,۰۰۰ تومان به همه اعضای فعال این گروه اضافه می‌شود.",
                message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        var amountRials = money.ToRials(amountTomans.Value);
        var result = await store.WriteAsync(state =>
        {
            var members = state.Members.Where(x => x.GroupChatId == message.Chat.Id && x.IsActive).ToArray();
            foreach (var member in members)
            {
                state.DebtEntries.Add(new DebtLedgerEntry
                {
                    TeamMemberId = member.Id,
                    GroupChatId = message.Chat.Id,
                    AmountRials = amountRials,
                    Kind = DebtEntryKind.Penalty,
                    Description = "جریمه گروهی ثبت‌شده توسط مدیر در گروه"
                });

                foreach (var payment in state.Payments.Where(x =>
                             x.TeamMemberId == member.Id &&
                             x.Status is PaymentRequestStatus.Pending or PaymentRequestStatus.Approved))
                {
                    payment.Status = PaymentRequestStatus.Expired;
                    payment.RejectionReason = "مبلغ بدهی پس از صدور فاکتور تغییر کرد.";
                }
            }

            var totalDebt = state.DebtEntries
                .Where(x => x.GroupChatId == message.Chat.Id)
                .Sum(x => x.AmountRials);
            return (Count: members.Length, TotalDebt: Math.Max(0, totalDebt));
        }, cancellationToken);

        await bale.SendMessageAsync(
            message.Chat.Id,
            result.Count == 0
                ? "هیچ عضو فعالی برای این گروه ثبت نشده است.\nاعضا با زدن دکمه ثبت عضویت یا /start در خصوصی بات اضافه می‌شوند."
                : $"✅ جریمه گروهی ثبت شد.\nمبلغ برای هر عضو: {money.Format(amountRials)}\nتعداد اعضا: {result.Count:N0}\nجمع کل بدهی گروه: {money.Format(result.TotalDebt)}\n\nهر عضو می‌تواند در خصوصی بات با /bill صورت‌حساب و کارت پرداخت خود را دریافت کند.",
            message.MessageId,
            cancellationToken: cancellationToken);
    }

    private static long? ParseAmountArgument(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = text
            .Replace('٬', ' ')
            .Replace(',', ' ')
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) return null;

        var digits = new string(tokens[1].Select(ToLatinDigit).ToArray());
        return long.TryParse(digits, out var amount) ? amount : null;
    }

    private static char ToLatinDigit(char c) => c switch
    {
        >= '۰' and <= '۹' => (char)('0' + c - '۰'),
        >= '٠' and <= '٩' => (char)('0' + c - '٠'),
        _ => c
    };

    private async Task SendDebtorsAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        var data = await store.ReadAsync(state =>
        {
            var groupTitle = state.Groups.FirstOrDefault(x => x.ChatId == chatId)?.Title;
            var debtMap = state.DebtEntries
                .Where(x => x.GroupChatId == chatId)
                .GroupBy(x => x.TeamMemberId)
                .ToDictionary(x => x.Key, x => Math.Max(0, x.Sum(y => y.AmountRials)));

            var debtors = state.Members
                .Where(x => x.IsActive && x.GroupChatId == chatId && debtMap.GetValueOrDefault(x.Id) > 0)
                .OrderByDescending(x => debtMap.GetValueOrDefault(x.Id))
                .Select(x => new
                {
                    x.DisplayName,
                    Debt = debtMap.GetValueOrDefault(x.Id)
                })
                .ToArray();

            return new
            {
                GroupTitle = groupTitle,
                Count = debtors.Length,
                Total = debtors.Sum(x => x.Debt),
                Lines = debtors.Select((x, index) => $"{index + 1}. {x.DisplayName}: {money.Format(x.Debt)}").ToArray()
            };
        }, cancellationToken);

        var details = data.Lines.Length == 0
            ? "هیچ بدهکار فعلی وجود ندارد."
            : string.Join("\n", data.Lines);

        await SendLongMessageAsync(
            chatId,
            $"📋 فهرست بدهکاران کیف‌تاخیر{GroupSuffix(data.GroupTitle)}\nتعداد بدهکاران: {data.Count:N0}\nجمع کل بدهی: {money.Format(data.Total)}\n\n{details}",
            replyToMessageId,
            cancellationToken);
    }

    private async Task SendAdminHelpAsync(long chatId, long replyToMessageId, CancellationToken cancellationToken)
    {
        await bale.SendMessageAsync(
            chatId,
            "📖 راهنمای کامل کیف‌تاخیر 👇\n\n" +
            "— فرمان‌های مدیر گروه (نیاز به دسترسی مدیر) —\n\n" +
            "👤 ثبت عضو:\nروی پیام او Reply بزنید و /addmember یا «ثبت عضو» بفرستید.\n\n" +
            "🗑 حذف عضو:\nروی پیام او Reply بزنید و /removemember یا «حذف عضو» بفرستید.\n\n" +
            "💸 ثبت جریمه ثابت ۲۰۰,۰۰۰ تومانی برای یک نفر:\nReply + /fine200\n\n" +
            "💸 جریمه گروهی (مثلاً سهم اشتراک مشترک):\n/fineall مبلغ — مثال: /fineall 50000 یعنی ۵۰,۰۰۰ تومان به همه اعضای فعال اضافه می‌شود. معادل فارسی: «جریمه همه ۵۰۰۰۰».\n\n" +
            "💳 بدهی یک نفر + صدور فاکتور:\nReply + /debt\n\n" +
            "📋 فهرست بدهکاران و جمع بدهی:\n/debtors یا «لیست بدهکاران»\n\n" +
            "👥 ثبت همه کاربرانی که در گروه پیام داده‌اند:\n/addobserved\n\n" +
            "🔄 ثبت مدیران گروه:\n/syncmembers\n\n" +
            "📨 ارسال دکمه ثبت عضویت برای همه اعضا:\n/registermembers\n\n" +
            "— فرمان‌های همه اعضا —\n\n" +
            "💰 موجودی صندوق گروه:\n/fund — برای مدیران همراه با تفکیک گردش (پرداخت جریمه‌ها، موجودی اولیه، هزینه‌ها و...).\n\n" +
            "— در گفت‌وگوی خصوصی بات —\n\n" +
            "📄 صورت‌حساب و کارت پرداخت بدهی خودتان: /bill یا /pay یا پیام «صورتحساب من»\n" +
            "✅ ثبت یا به‌روزرسانی عضویت: /start\n" +
            "ℹ️ راهنمای عضو: /help\n\n" +
            "نکته: فرمان‌های مدیریتی فقط برای مدیران همین گروه فعال است و اعداد هر گروه کاملاً جدا محاسبه می‌شود.",
            replyToMessageId,
            cancellationToken: cancellationToken);
    }

    private async Task SendFundAsync(long chatId, long replyToMessageId, bool includeBreakdown, CancellationToken cancellationToken)
    {
        var data = await store.ReadAsync(state => new
        {
            GroupTitle = state.Groups.FirstOrDefault(x => x.ChatId == chatId)?.Title,
            Balance = state.FundEntries.Where(x => x.GroupChatId == chatId).Sum(x => x.AmountRials),
            PaidCount = state.Payments.Count(x => x.GroupChatId == chatId && x.Status == PaymentRequestStatus.Paid),
            Breakdown = state.FundEntries
                .Where(x => x.GroupChatId == chatId)
                .GroupBy(x => x.Kind)
                .Select(g => (Kind: g.Key, Count: g.Count(), Sum: g.Sum(x => x.AmountRials)))
                .ToArray()
        }, cancellationToken);

        var text = $"💰 گزارش کیف‌تاخیر{GroupSuffix(data.GroupTitle)}\nموجودی فعلی: {money.Format(data.Balance)}\nتعداد پرداخت موفق: {data.PaidCount:N0}";

        if (includeBreakdown && data.Breakdown.Length > 0)
        {
            var lines = data.Breakdown
                .OrderByDescending(x => x.Sum)
                .Select(x => $"• {FundKindLabel(x.Kind)}: {money.Format(x.Sum)} ({x.Count:N0} مورد)");
            text += "\n\nتفکیک گردش صندوق:\n" + string.Join("\n", lines);
        }

        await bale.SendMessageAsync(chatId, text, replyToMessageId, cancellationToken: cancellationToken);
    }

    private static string FundKindLabel(FundEntryKind kind) => kind switch
    {
        FundEntryKind.Payment => "پرداخت جریمه اعضا",
        FundEntryKind.OpeningBalance => "موجودی اولیه",
        FundEntryKind.ManualAdjustment => "اصلاح دستی/واریز",
        FundEntryKind.Expense => "هزینه/خروجی",
        FundEntryKind.Refund => "بازپرداخت",
        _ => kind.ToString()
    };

    private static string GroupSuffix(string? groupTitle)
        => string.IsNullOrWhiteSpace(groupTitle) ? string.Empty : $" — {groupTitle}";

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

        var member = await UpsertMemberAsync(target, message.Chat.Id, cancellationToken);
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
                    GroupChatId = payment.GroupChatId,
                    AmountRials = -applied,
                    Kind = DebtEntryKind.Payment,
                    Description = "پرداخت جریمه از بله",
                    PaymentRequestId = payment.Id
                });
            }

            state.FundEntries.Add(new FundLedgerEntry
            {
                GroupChatId = payment.GroupChatId,
                AmountRials = payment.AmountRials,
                Kind = FundEntryKind.Payment,
                Description = overpayment > 0 ? "واریز جریمه از بله همراه مازاد پرداخت" : "واریز جریمه از بله",
                PaymentRequestId = payment.Id
            });

            var name = state.Members.FirstOrDefault(x => x.Id == payment.TeamMemberId)?.DisplayName
                       ?? message.From.DisplayName;
            var remainingDebt = Math.Max(0, currentDebt - applied);
            var fundBalance = state.FundEntries.Where(x => x.GroupChatId == payment.GroupChatId).Sum(x => x.AmountRials);

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

    private Task<TeamMember> UpsertMemberAsync(BaleUser user, long groupChatId, CancellationToken cancellationToken)
        => store.WriteAsync(state =>
        {
            var name = string.IsNullOrWhiteSpace(user.DisplayName) ? $"کاربر {user.Id}" : user.DisplayName;
            var member = state.Members.FirstOrDefault(x => x.BaleUserId == user.Id && x.GroupChatId == groupChatId);
            if (member is null)
            {
                member = new TeamMember
                {
                    DisplayName = name,
                    GroupChatId = groupChatId,
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

    private Task DeactivateMemberAsync(long baleUserId, long groupChatId, CancellationToken cancellationToken)
        => store.WriteAsync(state =>
        {
            var member = state.Members.FirstOrDefault(x => x.BaleUserId == baleUserId && x.GroupChatId == groupChatId);
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

    private Task DeactivateMemberEverywhereAsync(long baleUserId, CancellationToken cancellationToken)
        => store.WriteAsync(state =>
        {
            foreach (var member in state.Members.Where(x => x.BaleUserId == baleUserId))
            {
                member.IsActive = false;
                member.UpdatedAt = DateTime.UtcNow;
            }
        }, cancellationToken);

    private Task<bool> EnsureAdminAsync(BaleMessage message, CancellationToken cancellationToken)
        => IsGroupAdminAsync(message, quiet: false, cancellationToken);

    private async Task<bool> IsGroupAdminAsync(BaleMessage message, bool quiet, CancellationToken cancellationToken)
    {
        if (message.From is null) return false;
        if (_adminIds.Contains(message.From.Id)) return true;

        var cacheKey = (message.Chat.Id, message.From.Id);
        if (_groupAdminCache.TryGetValue(cacheKey, out var cachedAt) && DateTime.UtcNow - cachedAt < AdminCacheLifetime)
        {
            return true;
        }

        try
        {
            var admins = await bale.GetChatAdministratorsAsync(message.Chat.Id, cancellationToken);
            var isAdmin = admins.Any(x => x.User.Id == message.From.Id &&
                                           (string.Equals(x.Status, "administrator", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(x.Status, "creator", StringComparison.OrdinalIgnoreCase)));
            if (!isAdmin)
            {
                if (!quiet)
                {
                    await bale.SendMessageAsync(message.Chat.Id, "این فرمان فقط برای مدیران همین گروه فعال است.", message.MessageId, cancellationToken: cancellationToken);
                }
                return false;
            }

            _groupAdminCache[cacheKey] = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex) when (ex is BaleApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch administrators of group {ChatId}.", message.Chat.Id);
            if (!quiet)
            {
                await bale.SendMessageAsync(message.Chat.Id, "بررسی دسترسی مدیر موقتاً ناموفق بود. چند لحظه بعد دوباره تلاش کنید.", message.MessageId, cancellationToken: cancellationToken);
            }
            return false;
        }
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

    private static bool IsFineAllCommand(string command)
    {
        var normalized = command.Replace('‌', ' ').Replace("  ", " ").Trim();
        var firstToken = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return firstToken is "/fineall" or "/chargeall" or "/جریمههمه" or "/جریمه_همه" ||
               normalized.StartsWith("جریمه همه", StringComparison.Ordinal) ||
               normalized.StartsWith("همه جریمه", StringComparison.Ordinal);
    }

    private async Task HandleRemoveMemberAsync(BaleMessage message, CancellationToken cancellationToken)
    {
        var target = message.ReplyToMessage?.From;
        if (target is null || target.IsBot)
        {
            await bale.SendMessageAsync(
                message.Chat.Id,
                "برای حذف عضو، روی یکی از پیام‌های او Reply بزنید و /removemember یا «حذف عضو» را ارسال کنید.",
                message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        var removed = await store.WriteAsync(state =>
        {
            var member = state.Members.FirstOrDefault(x => x.BaleUserId == target.Id && x.GroupChatId == message.Chat.Id);
            if (member is null) return (Found: false, Name: string.Empty, Debt: 0L);

            member.IsActive = false;
            member.UpdatedAt = DateTime.UtcNow;
            foreach (var payment in state.Payments.Where(x =>
                         x.TeamMemberId == member.Id &&
                         x.Status is PaymentRequestStatus.Pending or PaymentRequestStatus.Approved))
            {
                payment.Status = PaymentRequestStatus.Expired;
                payment.RejectionReason = "عضو توسط مدیر از گروه حذف شد.";
            }

            return (Found: true, Name: member.DisplayName, Debt: Math.Max(0, state.DebtEntries.Where(x => x.TeamMemberId == member.Id).Sum(x => x.AmountRials)));
        }, cancellationToken);

        await bale.SendMessageAsync(
            message.Chat.Id,
            removed.Found
                ? $"🗑 {removed.Name} از اعضای کیف‌تاخیر این گروه حذف شد.\nبدهی باز او: {money.Format(removed.Debt)}\n(برای بازگشت، دوباره با /addmember ثبت کنید)"
                : "این کاربر در اعضای این گروه ثبت نشده است.",
            message.MessageId,
            cancellationToken: cancellationToken);
    }

    private static bool IsRemoveMemberCommand(string command)
    {
        var normalized = command.Replace('‌', ' ').Replace("  ", " ").Trim();
        return normalized is
            "/removemember" or "/deletemember" or "/حذفعضو" or "/حذف_عضو" or
            "حذف عضو" or "حذفش کن";
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
