using ChartAnalyzer;
using ChartAnalyzer.Analysis;
using ChartAnalyzer.Binance;
using ChartAnalyzer.Coinbase;
using ChartAnalyzer.Data;
using ChartAnalyzer.Telegram;
using ChartAnalyzer.Workers;
using ChartAnalyzer.Zones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.Configure<AnalyzerOptions>(builder.Configuration.GetSection("Analyzer"));

// The app runs for weeks, so connections are recycled regularly to pick up DNS changes.
static SocketsHttpHandler LongLivedHandler() => new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

builder.Services.AddHttpClient<BinanceClient>((sp, http) =>
        http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AnalyzerOptions>>().Value.BinanceBaseUrl))
    .ConfigurePrimaryHttpMessageHandler(LongLivedHandler);
builder.Services.AddHttpClient<BinancePerpClient>((sp, http) =>
        http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AnalyzerOptions>>().Value.BinancePerpBaseUrl))
    .ConfigurePrimaryHttpMessageHandler(LongLivedHandler);
builder.Services.AddSingleton<AbsorptionDetector>();
builder.Services.AddSingleton<ZoneReport>();
builder.Services.AddSingleton<LevelScanner>();

// With --from (and optionally --to), scan the past and exit. Otherwise watch your zones live.
// Dates are local time, e.g. --from 2026-10-01 or --from "2026-10-01 14:00".
// Add --support 80700-81600 (or --resistance) for a zone report, with --profile macro for a high-timeframe zone;
// --zone 80700-81600 lists the minute signals at that zone instead.
// Add --market perp to scan the Binance perpetual instead of spot (signal scans only).
// Add --levels true to find candidate support/resistance levels instead (optionally --bin 100 for the price bin size).
if (builder.Configuration["from"] is { } from)
{
    var to = builder.Configuration["to"];
    var profile = builder.Configuration["profile"] is { } p ? Enum.Parse<ZoneProfile>(p, ignoreCase: true) : ZoneProfile.Local;
    PriceZone? zone =
        builder.Configuration["support"] is { } support ? PriceZone.Parse(support, ZoneRole.Support, profile) :
        builder.Configuration["resistance"] is { } resistance ? PriceZone.Parse(resistance, ZoneRole.Resistance, profile) :
        builder.Configuration["zone"] is { } any ? PriceZone.Parse(any, ZoneRole.Any) :
        null;
    builder.Services.AddSingleton(new HistoryRange(
        DateTimeOffset.Parse(from),
        to is null ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(to),
        zone,
        builder.Configuration.GetValue<bool>("summary"),
        string.Equals(builder.Configuration["market"], "perp", StringComparison.OrdinalIgnoreCase),
        builder.Configuration.GetValue<bool>("levels"),
        builder.Configuration.GetValue<decimal?>("bin")));
    builder.Services.AddHostedService<HistoryWorker>();
}
else
{
    builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
    builder.Services.AddHttpClient<TelegramClient>()
        .ConfigurePrimaryHttpMessageHandler(LongLivedHandler)
        .RemoveAllLoggers();   // request URLs contain the bot token
    builder.Services.AddSingleton<TelegramNotifier>();

    builder.Services.AddDbContextFactory<ChartDbContext>(o => o.UseSqlServer(
        builder.Configuration.GetConnectionString("ChartAnalyzer"),
        sql => sql.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null)));

    builder.Services.AddSingleton<DbWriteQueue>();
    builder.Services.AddSingleton<AlertLog>();
    builder.Services.AddSingleton<ZoneStore>();
    builder.Services.AddSingleton<ZoneMonitor>();

    // Coinbase requires a User-Agent on API requests.
    builder.Services.AddHttpClient<CoinbaseClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AnalyzerOptions>>().Value.CoinbaseBaseUrl);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("chart-analyzer/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(LongLivedHandler);
    builder.Services.AddSingleton<CoinbaseCandles>();
    builder.Services.AddSingleton<CoinbaseHistory>();

    builder.Services.AddHostedService<DbWriter>();
    builder.Services.AddHostedService<TelegramBotWorker>();
    builder.Services.AddHostedService<ZoneWorker>();
    builder.Services.AddHostedService<LiveAbsorptionWorker>();
}

builder.Build().Run();
