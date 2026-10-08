using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RustPlusDesk.Services
{
    public sealed class ScrapedPlayerInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
    }

    public static class BattleMetricsScraperService
    {
        private static WebView2? _webView;
        private static CoreWebView2Environment? _environment;
        private static bool _isInitialized;
        private static bool _isScraping;
        private static DispatcherTimer? _timer;
        private static string? _currentServerId;

        public static event Action<List<ScrapedPlayerInfo>>? OnPlayersScraped;
        public static event Action<string>? OnServerPlayersScraped;
        public static bool IsEnabled { get; set; } = true;

        public static async Task InitializeAsync(WebView2 webView)
        {
            if (_isInitialized) return;
            try
            {
                _webView = webView;
                string webViewDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RustPlusDesk",
                    "WebView2_BM");
                Directory.CreateDirectory(webViewDataFolder);

                _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataFolder);
                await _webView.EnsureCoreWebView2Async(_environment);

                _isInitialized = true;
                Debug.WriteLine("[BM-SCRAPER] WebView2 scraper initialized successfully.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BM-SCRAPER] Init error: {ex.Message}");
            }
        }

        public static void StartPeriodicScraping(string bmServerId, int intervalSeconds = 180)
        {
            _currentServerId = bmServerId;
            if (string.IsNullOrWhiteSpace(bmServerId) || !IsEnabled) return;

            _timer?.Stop();
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(120, intervalSeconds))
            };
            _timer.Tick += async (_, _) => await TriggerScrapeAsync();
            _timer.Start();

            // Run first scrape after a short delay
            var initTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            initTimer.Tick += async (_, _) =>
            {
                initTimer.Stop();
                await TriggerScrapeAsync();
            };
            initTimer.Start();
        }

        public static void StopScraping()
        {
            _timer?.Stop();
            _timer = null;
        }

        public static async Task TriggerScrapeAsync()
        {
            if (string.IsNullOrWhiteSpace(_currentServerId)) return;
            await ScrapeServerAsync(_currentServerId);
        }

        public static async Task ScrapeServerAsync(string bmServerId)
        {
            if (_webView == null || !_isInitialized || _isScraping) return;
            if (string.IsNullOrWhiteSpace(bmServerId)) return;

            _isScraping = true;
            try
            {
                string targetUrl = $"https://www.battlemetrics.com/servers/rust/{bmServerId}";
                var tcs = new TaskCompletionSource<bool>();

                void NavHandler(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    _webView.NavigationCompleted -= NavHandler;
                    tcs.TrySetResult(e.IsSuccess);
                }

                if (_webView.CoreWebView2 == null)
                {
                    _webView.NavigationCompleted -= NavHandler;
                    return;
                }

                _webView.CoreWebView2.Navigate(targetUrl);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                if (completedTask != tcs.Task)
                {
                    _webView.NavigationCompleted -= NavHandler;
                    Debug.WriteLine("[BM-SCRAPER] Navigation timed out.");
                }

                // Allow 3 seconds for DOM rendering
                await Task.Delay(3000);

                string js = @"
(() => {
    try {
        const title = document.title || '';
        if (title.includes('Just a moment') || title.includes('Attention Required')) {
            return JSON.stringify({ status: 'challenge', players: [], serverPlayers: '' });
        }
        let serverPlayers = '';
        const dts = document.querySelectorAll('dt');
        for (const dt of dts) {
            const txt = (dt.innerText || dt.textContent || '').trim().toLowerCase();
            if (txt.includes('player') || txt.includes('oyuncu')) {
                const dd = dt.nextElementSibling;
                if (dd) {
                    serverPlayers = (dd.innerText || dd.textContent || '').trim();
                    break;
                }
            }
        }
        const players = [];
        const links = document.querySelectorAll('a[href*=""/players/""]');
        for (const a of links) {
            const href = a.getAttribute('href') || '';
            const m = href.match(/\/players\/(\d+)/);
            if (m) {
                const id = m[1];
                const name = (a.innerText || a.textContent || '').trim();
                if (name && name.length > 0 && !players.some(p => p.id === id)) {
                    let time = '';
                    const tr = a.closest('tr');
                    if (tr) {
                        const tds = tr.querySelectorAll('td');
                        if (tds.length > 1) {
                            time = tds[tds.length - 1].innerText.trim();
                        }
                    }
                    players.push({ id, name, time });
                }
            }
        }
        return JSON.stringify({ status: 'ok', players, serverPlayers });
    } catch (e) {
        return JSON.stringify({ status: 'error', error: e.toString(), players: [], serverPlayers: '' });
    }
})()";

                string rawJson = await _webView.ExecuteScriptAsync(js);
                if (!string.IsNullOrWhiteSpace(rawJson))
                {
                    string unescaped = rawJson;
                    if (rawJson.StartsWith("\"") && rawJson.EndsWith("\""))
                    {
                        unescaped = JsonSerializer.Deserialize<string>(rawJson) ?? "";
                    }

                    using var doc = JsonDocument.Parse(unescaped);
                    var root = doc.RootElement;
                    string status = root.TryGetProperty("status", out var sProp) ? sProp.GetString() ?? "" : "";

                    if (root.TryGetProperty("serverPlayers", out var spProp))
                    {
                        string sp = spProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(sp))
                        {
                            OnServerPlayersScraped?.Invoke(sp);
                        }
                    }

                    if (status == "ok" && root.TryGetProperty("players", out var pArray) && pArray.ValueKind == JsonValueKind.Array)
                    {
                        var players = new List<ScrapedPlayerInfo>();
                        foreach (var el in pArray.EnumerateArray())
                        {
                            string id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            string name = el.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                            string time = el.TryGetProperty("time", out var tProp) ? tProp.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(name))
                            {
                                players.Add(new ScrapedPlayerInfo { Id = id, Name = name, Time = time });
                            }
                        }

                        if (players.Count > 0)
                        {
                            TrackingService.IngestBattleMetricsPlayers(players, bmServerId);
                            OnPlayersScraped?.Invoke(players);
                            Debug.WriteLine($"[BM-SCRAPER] Successfully scraped {players.Count} players from BattleMetrics.");
                        }
                    }
                    else if (status == "challenge")
                    {
                        Debug.WriteLine("[BM-SCRAPER] Cloudflare challenge encountered. Will retry automatically.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BM-SCRAPER] Scrape error: {ex.Message}");
            }
            finally
            {
                _isScraping = false;
            }
        }
    }
}
