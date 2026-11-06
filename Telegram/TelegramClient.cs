using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Telegram;

public sealed record TelegramMessage(long UpdateId, long ChatId, string Text);

/// <summary>Minimal Telegram Bot API client: send messages and receive them by long polling.</summary>
public sealed class TelegramClient(HttpClient http, IOptions<TelegramOptions> options)
{
    // The token is part of the URL, so this client's request logging is switched off in Program.cs.
    private readonly string _baseUrl = $"https://api.telegram.org/bot{options.Value.BotToken}/";

    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(_baseUrl + "sendMessage", new { chat_id = chatId, text }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Waits up to 50 seconds for new text messages after <paramref name="offset"/>.</summary>
    public async Task<IReadOnlyList<TelegramMessage>> GetMessagesAsync(long offset, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{_baseUrl}getUpdates?offset={offset}&timeout=50&allowed_updates=[\"message\"]", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var messages = new List<TelegramMessage>();
        foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var updateId = update.GetProperty("update_id").GetInt64();
            var text = "";
            var chatId = 0L;
            if (update.TryGetProperty("message", out var message))
            {
                chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
                if (message.TryGetProperty("text", out var t))
                    text = t.GetString() ?? "";
            }
            // Updates without text are still returned, so the caller can move the offset past them.
            messages.Add(new TelegramMessage(updateId, chatId, text));
        }
        return messages;
    }
}
