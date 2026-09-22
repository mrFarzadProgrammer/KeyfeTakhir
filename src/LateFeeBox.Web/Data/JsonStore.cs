using System.Text.Json;
using System.Text.Json.Serialization;
using LateFeeBox.Web.Domain;
using LateFeeBox.Web.Options;
using LateFeeBox.Web.Security;
using Microsoft.Extensions.Options;

namespace LateFeeBox.Web.Data;

public sealed class JsonStore(
    IOptions<StorageOptions> storageOptions,
    IOptions<BaleOptions> baleOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly StorageOptions _options = storageOptions.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppState? _state;

    private string StoragePath => Path.GetFullPath(_options.Path);
    private string BackupPath => StoragePath + ".bak";
    private string TempPath => StoragePath + ".tmp";

    public async Task InitializeAsync(string initialUsername, string initialPassword, PasswordService passwordService)
    {
        await _gate.WaitAsync();
        try
        {
            await LoadAsync();
            if (_state!.Admin is null)
            {
                _state.Admin = new AdminUser
                {
                    Username = initialUsername,
                    PasswordHash = passwordService.Hash(initialPassword)
                };
                await SaveAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> ReadAsync<T>(Func<AppState, T> reader, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync();
            return reader(_state!);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task WriteAsync(Action<AppState> writer, CancellationToken cancellationToken = default)
        => WriteAsync<object?>(state => { writer(state); return null; }, cancellationToken);

    public async Task<T> WriteAsync<T>(Func<AppState, T> writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync();
            var result = writer(_state!);
            await SaveAsync();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadAsync()
    {
        if (_state is not null) return;

        _state = await TryReadFileAsync(StoragePath) ?? await TryReadFileAsync(BackupPath) ?? new AppState();
        Migrate(_state);
    }

    private static async Task<AppState?> TryReadFileAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task SaveAsync()
    {
        if (File.Exists(StoragePath))
        {
            try
            {
                File.Copy(StoragePath, BackupPath, overwrite: true);
            }
            catch (IOException)
            {
                // The backup is best-effort; the atomic replace below is what matters.
            }
        }

        await using (var stream = File.Create(TempPath))
        {
            await JsonSerializer.SerializeAsync(stream, _state, JsonOptions);
        }

        File.Move(TempPath, StoragePath, overwrite: true);
    }

    /// <summary>
    /// Upgrades an older single-group state file to the multi-group schema.
    /// Legacy rows keep their original values; when no group was configured in
    /// Bale:AllowedGroupChatIds they are attached to a synthetic default group (ChatId = 0).
    /// </summary>
    private void Migrate(AppState state)
    {
        if (state.Groups.Count == 0)
        {
            // Only real, positive Bale chat ids count; placeholder zeros in the old
            // config must not occupy a group slot.
            var configured = baleOptions.Value.AllowedGroupChatIds.Where(x => x > 0).ToArray();
            foreach (var chatId in configured)
            {
                state.Groups.Add(new GroupInfo { ChatId = chatId, Title = $"گروه {chatId}" });
            }
        }

        // Drop any phantom placeholder group that may have been created earlier.
        state.Groups.RemoveAll(x => x.ChatId == 0);

        var fallbackGroup = state.Groups.FirstOrDefault()?.ChatId ?? 0;

        foreach (var member in state.Members.Where(x => x.GroupChatId == 0))
        {
            member.GroupChatId = fallbackGroup;
        }

        foreach (var debt in state.DebtEntries.Where(x => x.GroupChatId == 0))
        {
            debt.GroupChatId = fallbackGroup;
        }

        foreach (var fund in state.FundEntries.Where(x => x.GroupChatId == 0))
        {
            fund.GroupChatId = fallbackGroup;
        }
    }
}
