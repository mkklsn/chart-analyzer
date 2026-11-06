using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Telegram;

/// <summary>Sends alerts to your chat. Never throws, and gives up after a short timeout so detection isn't held up.</summary>
public sealed class TelegramNotifier(TelegramClient telegram, IOptions<TelegramOptions> options, ILogger<TelegramNotifier> logger)
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly TelegramOptions _options = options.Value;

    /// <summary>True if the message was sent.</summary>
    public async Task<bool> SendAsync(string text, CancellationToken ct)
    {
        if (!_options.IsConfigured || _options.ChatId == 0)
        {
            logger.LogInformation("Telegram not configured, alert not sent.");
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await telegram.SendMessageAsync(_options.ChatId, text, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Telegram alert failed: {Message}", ex.Message);
            return false;
        }
    }
}
