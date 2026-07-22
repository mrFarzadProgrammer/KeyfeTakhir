namespace LateFeeBox.Web.Options;

public sealed class StorageOptions
{
    public string Path { get; set; } = "data/state.json";
}

public sealed class BaleOptions
{
    public string BotToken { get; set; } = string.Empty;
    public string ProviderToken { get; set; } = string.Empty;
    public string BotUsername { get; set; } = string.Empty;
    public bool UseLongPolling { get; set; } = true;
    public int LongPollingTimeoutSeconds { get; set; } = 25;
    public long[] AdminUserIds { get; set; } = [];
    public long[] AllowedGroupChatIds { get; set; } = [];
}

public sealed class InitialAdminOptions
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "ChangeMe123!";
}

public sealed class MoneyOptions
{
    public bool DisplayInTomans { get; set; } = true;
}
