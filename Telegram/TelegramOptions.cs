namespace ChartAnalyzer.Telegram;

/// <summary>
/// Bound from the "Telegram" section. Keep the token out of appsettings.json:
/// locally use user secrets, in Azure it comes from a Container App secret.
/// </summary>
public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";

    /// <summary>Your chat with the bot. Alerts go here, and only commands from here are accepted.</summary>
    public long ChatId { get; set; }

    public bool IsConfigured => BotToken != "";
}
