using System.Globalization;
using System.Net;
using ChartAnalyzer.Analysis;
using ChartAnalyzer.Data;
using ChartAnalyzer.Zones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Telegram;

/// <summary>
/// Listens for your Telegram commands to manage watched zones (see HelpText).
/// </summary>
public sealed class TelegramBotWorker(
    TelegramClient telegram,
    ZoneStore zones,
    ZoneMonitor zoneMonitor,
    DbWriteQueue dbQueue,
    IOptions<TelegramOptions> options,
    IOptions<AnalyzerOptions> analyzerOptions,
    ILogger<TelegramBotWorker> logger) : BackgroundService
{
    private const string HelpText =
        "Zones:\n" +
        "/zone support 75000 77000 - watch a local zone (days to weeks)\n" +
        "/zone support 58000 63700 macro - watch a macro zone (months)\n" +
        "Add a start date to analyze from then: /zone support 75000 77000 2026-10-29\n" +
        "Use resistance instead of support for a ceiling.\n" +
        "\n" +
        "/zones - list watched zones\n" +
        "/removezone 75000 77000 - stop watching a zone\n" +
        "/zonereport - summary of every zone now\n" +
        "\n" +
        "Local zones: alerts as they happen, summary daily.\n" +
        "Macro zones: checked after each daily close, summary on Mondays.";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly TelegramOptions _options = options.Value;
    private readonly TimeZoneInfo _timeZone = analyzerOptions.Value.GetTimeZone();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning("Telegram:BotToken is not set. Telegram alerts and commands are off.");
            return;
        }

        var offset = 0L;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var message in await telegram.GetMessagesAsync(offset, ct))
                {
                    offset = message.UpdateId + 1;
                    if (message.Text != "")
                        await HandleAsync(message, ct);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Telegram allows only one listener per bot.
                logger.LogWarning("Another copy of this app is listening to the same bot (local and Azure at once?). Retrying in 1 minute.");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning("Telegram polling failed, retrying: {Message}", ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private async Task HandleAsync(TelegramMessage message, CancellationToken ct)
    {
        if (_options.ChatId == 0)
        {
            // Setup helper: tells you which chat ID to configure.
            logger.LogWarning("Message from chat {ChatId}. Set Telegram:ChatId to this value to use the bot.", message.ChatId);
            await ReplyAsync(message.ChatId, $"Your chat ID is {message.ChatId}. Set Telegram:ChatId to this value and restart the app.", ct);
            return;
        }
        if (message.ChatId != _options.ChatId)
        {
            logger.LogWarning("Ignored a message from unknown chat {ChatId}.", message.ChatId);
            return;
        }

        var parts = message.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant().Split('@')[0];   // "/zones@MyBot" in group chats
        var args = parts.Skip(1).ToArray();

        if (command is "/zone" or "/zones" or "/removezone" or "/zonereport" && !zones.IsLoaded)
        {
            await ReplyAsync(message.ChatId, "Still starting up (connecting to the database). Try again in a minute.", ct);
            return;
        }

        switch (command)
        {
            case "/zone":
                await AddZoneAsync(message.ChatId, args, ct);
                break;
            case "/zonereport":
                await SendZoneReportsAsync(message.ChatId, ct);
                break;
            case "/zones":
                await ReplyAsync(message.ChatId, ListZones(), ct);
                break;
            case "/removezone":
                await ReplyAsync(message.ChatId, RemoveZone(args), ct);
                break;
            default:
                await ReplyAsync(message.ChatId, HelpText, ct);
                break;
        }
    }

    // /zone support|resistance <price> <price> [local|macro] [yyyy-MM-dd]
    private async Task AddZoneAsync(long chatId, string[] args, CancellationToken ct)
    {
        const string usage = "Usage: /zone support 75000 77000 (optionally macro, and a start date like 2026-10-29)";
        ZoneRole? role = args.Length >= 3
            ? args[0].ToLowerInvariant() switch { "support" => ZoneRole.Support, "resistance" => ZoneRole.Resistance, _ => null }
            : null;
        var prices = args.Length >= 3 ? ParsePrices(args[1..3]) : null;
        if (role is null || prices is null)
        {
            await ReplyAsync(chatId, usage, ct);
            return;
        }

        // Optional extras, in any order: the profile and a start date.
        var profile = ZoneProfile.Local;
        DateTimeOffset? since = null;
        foreach (var extra in args[3..])
        {
            if (Enum.TryParse<ZoneProfile>(extra, ignoreCase: true, out var p))
                profile = p;
            else if (DateTime.TryParseExact(extra, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                since = new DateTimeOffset(date, _timeZone.GetUtcOffset(date));
            else
            {
                await ReplyAsync(chatId, usage, ct);
                return;
            }
        }

        var settings = ZoneSettings.For(profile);
        var zone = new WatchedZone
        {
            Low = Math.Min(prices[0], prices[1]),
            High = Math.Max(prices[0], prices[1]),
            Role = role.Value,
            Profile = profile,
            Since = since ?? DateTimeOffset.UtcNow.AddDays(-settings.DefaultDays),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (!zones.Add(zone))
        {
            await ReplyAsync(chatId, $"Already watching {zone.Low:N0}–{zone.High:N0}. Remove it first with /removezone {zone.Low:0} {zone.High:0}", ct);
            return;
        }

        var stored = new WatchedZone { Low = zone.Low, High = zone.High, Role = zone.Role, Profile = zone.Profile, Since = zone.Since, CreatedAt = zone.CreatedAt };
        dbQueue.Enqueue($"add zone {zone.Low}-{zone.High}", async (db, ct) =>
        {
            db.Zones.Add(stored);
            await db.SaveChangesAsync(ct);
        });

        var schedule = profile == ZoneProfile.Macro
            ? "It's checked after each daily close; summary on Mondays."
            : "You'll get alerts as they happen and a daily summary.";
        await ReplyAsync(chatId, $"✅ Watching {Describe(zone)}. {schedule} Analyzing now…", ct);

        // The first summary already shows what's there, so only later signs trigger alerts.
        var analysis = await AnalyzeOrReportAsync(chatId, zone, ct);
        if (analysis is not null)
        {
            foreach (var sign in analysis.Signs.Where(s => s.Alert))
                zone.MarkAlerted(sign.Key);
            ZoneWorker.PersistAlertedKeys(dbQueue, zone);
        }
    }

    private async Task SendZoneReportsAsync(long chatId, CancellationToken ct)
    {
        var all = zones.GetAll();
        if (all.Count == 0)
        {
            await ReplyAsync(chatId, "No zones yet. Add one with /zone support 75000 77000", ct);
            return;
        }
        foreach (var zone in all)
            await AnalyzeOrReportAsync(chatId, zone, ct);
    }

    // Sends the zone summary, or an error message if Binance couldn't be reached.
    private async Task<ZoneAnalysis?> AnalyzeOrReportAsync(long chatId, WatchedZone zone, CancellationToken ct)
    {
        try
        {
            var analysis = await zoneMonitor.AnalyzeAsync(zone, ct);
            await ReplyAsync(chatId, zoneMonitor.FormatSummary(analysis) + "\n\n" + await zoneMonitor.ScoreboardAsync(zone, ct), ct);
            return analysis;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning("Zone analysis failed: {Message}", ex.Message);
            await ReplyAsync(chatId, $"Couldn't analyze {zone.Low:N0}–{zone.High:N0} right now (Binance unreachable). Try /zonereport later.", ct);
            return null;
        }
    }

    private string ListZones()
    {
        var all = zones.GetAll();
        if (all.Count == 0)
            return "No zones yet. Add one with /zone support 75000 77000";
        return string.Join("\n", all.Select(z =>
            $"{(z.Role == ZoneRole.Support ? "🟩" : "🟥")} {Describe(z)}, since {TimeZoneInfo.ConvertTime(z.Since, _timeZone):yyyy-MM-dd}"));
    }

    private string RemoveZone(string[] args)
    {
        var prices = args.Length == 2 ? ParsePrices(args) : null;
        if (prices is null)
            return "Usage: /removezone 75000 77000";

        var zone = zones.Remove(prices[0], prices[1]);
        if (zone is null)
            return "That's not one of your zones. See /zones";

        var (low, high) = (zone.Low, zone.High);
        dbQueue.Enqueue($"remove zone {low}-{high}", (db, ct) => db.Zones.Where(z => z.Low == low && z.High == high).ExecuteDeleteAsync(ct));
        return $"🗑 Stopped watching {low:N0}–{high:N0}.";
    }

    private static string Describe(WatchedZone z) =>
        $"{z.Profile.ToString().ToLowerInvariant()} {z.Role.ToString().ToLowerInvariant()} zone {z.Low:N0}–{z.High:N0}";

    // Accepts "76500", "76,500" and "76500.5". Returns null if any argument isn't a positive number.
    private static List<decimal>? ParsePrices(string[] args)
    {
        if (args.Length == 0)
            return null;

        var prices = new List<decimal>();
        foreach (var arg in args)
        {
            if (!decimal.TryParse(arg.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0)
                return null;
            prices.Add(price);
        }
        return prices;
    }

    private async Task ReplyAsync(long chatId, string text, CancellationToken ct)
    {
        try
        {
            await telegram.SendMessageAsync(chatId, text, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Telegram reply failed: {Message}", ex.Message);
        }
    }
}
