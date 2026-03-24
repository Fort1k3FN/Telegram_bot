using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
// using Telegram.Bot; (not used for HTTP polling)
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.IO;

// Simple Telegram bot: on /start scrape timetable and send one message per day.
class Program
{
    // Subscribers and snapshot storage for change notifications
    static readonly string SubscribersFile = Path.Combine(AppContext.BaseDirectory, "subscribers.txt");
    static readonly string SnapshotFile = Path.Combine(AppContext.BaseDirectory, "last_snapshot.txt");
    static readonly HashSet<long> Subscribers = new HashSet<long>();
    static string LastSnapshot = null;
    static readonly SemaphoreSlim ScrapeLock = new SemaphoreSlim(1, 1);

    static void LoadSubscribers()
    {
        try
        {
            if (File.Exists(SubscribersFile))
            {
                var lines = File.ReadAllLines(SubscribersFile).Select(l => l.Trim()).Where(l => long.TryParse(l, out _));
                Subscribers.Clear();
                foreach (var l in lines) Subscribers.Add(long.Parse(l));
            }
        }
        catch { }
    }

    static void SaveSubscribers()
    {
        try
        {
            File.WriteAllLines(SubscribersFile, Subscribers.Select(s => s.ToString()));
        }
        catch { }
    }

    static void AddSubscriber(long chatId)
    {
        lock (Subscribers)
        {
            if (Subscribers.Add(chatId)) SaveSubscribers();
        }
    }

    static void RemoveSubscriber(long chatId)
    {
        lock (Subscribers)
        {
            if (Subscribers.Remove(chatId)) SaveSubscribers();
        }
    }

    static void LoadLastSnapshot()
    {
        try { if (File.Exists(SnapshotFile)) LastSnapshot = File.ReadAllText(SnapshotFile); } catch { }
    }

    static void SaveLastSnapshot(string snapshot)
    {
        try { File.WriteAllText(SnapshotFile, snapshot ?? string.Empty); } catch { }
    }

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // Use fixed bot token for automated server deployment.
        // WARNING: token is embedded here as requested. For security prefer BOT_TOKEN env var.
        string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";
        // Allow overriding via environment variable if needed
        var botTokenEnv = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (!string.IsNullOrWhiteSpace(botTokenEnv)) token = botTokenEnv.Trim();

        var timetableUrl = Environment.GetEnvironmentVariable("TIMETABLE_URL") ?? "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        Console.WriteLine($"Using timetable URL: {timetableUrl}");
        Console.WriteLine("Starting bot receiver. Press Ctrl+C to stop.");

        using var stopCts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Stopping...");
            stopCts.Cancel();
            e.Cancel = true;
        };

        // Load persisted subscribers and last snapshot
        LoadSubscribers();
        LoadLastSnapshot();

        // Start background task that checks schedule every 30s and notifies subscribers on changes
        _ = Task.Run(async () =>
        {
            while (!stopCts.IsCancellationRequested)
            {
                try
                {
                    await ScrapeLock.WaitAsync(stopCts.Token);
                    var blocks = await ScrapeScheduleAsync(timetableUrl, stopCts.Token);
                    var snapshot = string.Join("\n---\n", blocks ?? new List<string>());
                    if (LastSnapshot == null) LastSnapshot = snapshot;
                    else if (snapshot != LastSnapshot)
                    {
                        // notify subscribers with a short summary and include timestamp
                        using var http = new HttpClient { BaseAddress = new Uri($"https://api.telegram.org/bot{token}/") };
                        var notice = "Відбулась зміна розкладу. Оновлення: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " (UTC)";
                        foreach (var sub in Subscribers.ToList())
                        {
                            try { await SendMessageHttp(http, sub, notice, stopCts.Token); } catch { }
                        }
                        LastSnapshot = snapshot;
                        SaveLastSnapshot(snapshot);
                    }
                }
                catch (OperationCanceledException) when (stopCts.IsCancellationRequested) { break; }
                catch { }
                finally { try { ScrapeLock.Release(); } catch { } }

                await Task.Delay(TimeSpan.FromSeconds(30), stopCts.Token);
            }
        }, stopCts.Token);

        // Run HTTP long-polling loop; this runs until canceled
        await HttpLongPollingLoop(token, timetableUrl, stopCts.Token);
    }

    // Not used when running as long-lived receiver

    static async Task SendMessageAsync(HttpClient http, long chatId, string text, CancellationToken ct)
    {
        var payload = new { chat_id = chatId, text = text };
        var resp = await http.PostAsJsonAsync("sendMessage", payload, ct);
        if (!resp.IsSuccessStatusCode) Console.WriteLine("sendMessage failed: " + resp.StatusCode);
    }

    // (No direct Telegram.Bot client wrapper — using HTTP API polling)

    // Fallback HTTP long-polling loop (uses Telegram HTTP API directly) to avoid dependency on specific Telegram.Bot StartReceiving API
    static async Task HttpLongPollingLoop(string token, string timetableUrl, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"https://api.telegram.org/bot{token}/") };
        int offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await http.GetAsync($"getUpdates?timeout=30&offset={offset}", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = string.Empty;
                    try { body = await resp.Content.ReadAsStringAsync(ct); } catch { }
                    Console.WriteLine($"getUpdates failed: {resp.StatusCode}. Body: {body}");

                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        Console.WriteLine("Bot token appears invalid or unauthorized. Exiting polling loop.");
                        return; // stop the loop
                    }

                    if ((int)resp.StatusCode == 429) // Too Many Requests
                    {
                        // Try to honor Retry-After header
                        if (resp.Headers.TryGetValues("Retry-After", out var vals))
                        {
                            var ra = vals.FirstOrDefault();
                            if (int.TryParse(ra, out var seconds))
                            {
                                Console.WriteLine($"Rate limited, retry after {seconds}s");
                                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                                continue;
                            }
                        }
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    await Task.Delay(2000, ct);
                    continue;
                }

                var content = string.Empty;
                try
                {
                    content = await resp.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to read getUpdates response body: " + ex.Message);
                    await Task.Delay(1000, ct);
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(content);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("getUpdates returned non-JSON response: " + ex.Message + " Body: " + content);
                    await Task.Delay(1000, ct);
                    continue;
                }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                    {
                        Console.WriteLine("getUpdates response 'ok' is false or missing. Body: " + content);
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    var results = root.GetProperty("result");

                    foreach (var upd in results.EnumerateArray())
                    {

                        offset = Math.Max(offset, upd.GetProperty("update_id").GetInt32() + 1);
                        if (!upd.TryGetProperty("message", out var msg)) continue;
                        if (!msg.TryGetProperty("text", out var textEl)) continue;

                        var text = textEl.GetString() ?? string.Empty;
                        var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();
                        Console.WriteLine($"Message from {chatId}: {text}");

                        var trimmed = text.Trim();

                        // fun reply when user writes "Кохаю"
                        if (trimmed.Equals("Кохаю", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendMessageHttp(http, chatId, "І я тебе дуже сильно кохаю, і чекаю в Польші<3", ct);
                            continue;
                        }

                        if (text.TrimStart().StartsWith("/start", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendMessageHttp(http, chatId, "Завантажую розклад...", ct);
                            try
                            {
                                var dayMessages = await ScrapeScheduleAsync(timetableUrl, ct);
                                if (dayMessages == null || dayMessages.Count == 0)
                                {
                                    await SendMessageHttp(http, chatId, "Не вдалося знайти розклад на сторінці.", ct);
                                    continue;
                                }
                                foreach (var day in dayMessages)
                                {
                                    if (string.IsNullOrWhiteSpace(day)) continue;
                                    await SendMessageHttp(http, chatId, day, ct);
                                    await Task.Delay(300, ct);
                                }
                                // Add chat as subscriber to changes
                                AddSubscriber(chatId);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Error scraping/sending: " + ex.Message);
                                await SendMessageHttp(http, chatId, "Помилка при отриманні розкладу: " + ex.Message, ct);
                            }
                        }
                    }
                }
            }

            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            catch (Exception ex)
            {
                Console.WriteLine("Polling error: " + ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }

    static async Task SendMessageHttp(HttpClient http, long chatId, string text, CancellationToken ct)
    {
        var payload = new { chat_id = chatId, text = text };
        var resp = await http.PostAsJsonAsync("sendMessage", payload, ct);
        if (!resp.IsSuccessStatusCode) Console.WriteLine("sendMessage failed: " + resp.StatusCode);
    }

    // Scrapes the timetable and returns a list of day messages. Each message:
    // Header: "Weekday (dd.MM.yyyy)"
    // Then lines: "HH:MM - HH:MM Subject" and next line: "Teacher"
    static async Task<List<string>> ScrapeScheduleAsync(string url, CancellationToken ct)
    {
        var options = new ChromeOptions();

        options.AddArgument("--headless=new");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--window-size=1920,1080");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddArgument("--remote-debugging-port=9222");
        options.AddArgument("--single-process");
        options.AddArgument("--no-zygote");
        options.BinaryLocation = "/usr/bin/google-chrome";

        using var driver = new ChromeDriver(options);
        try
        {
            driver.Navigate().GoToUrl(url);
            await Task.Delay(800, ct);

            // Build linear list of body elements to map date headers to following tables (columns)
            var bodyElems = driver.FindElements(By.CssSelector("body *")).ToList();
            var timeRegex = new Regex("\\b\\d{2}:\\d{2}\\b");
            var dateRegex = new Regex("\\b\\d{2}\\.\\d{2}\\.\\d{4}\\b");
            string[] weekdays = new[] { "Понеділок", "Вівторок", "Середа", "Четвер", "П'ятниця", "Субота", "Неділя" };

            var dayMessages = new List<string>();

            string currentDate = null;
            string currentWeekday = null;

            for (int i = 0; i < bodyElems.Count; i++)
            {
                var el = bodyElems[i];
                var txt = Regex.Replace(el.Text ?? string.Empty, "\\s+", " ").Trim();
                if (string.IsNullOrEmpty(txt)) continue;

                // detect date header
                var dmatch = dateRegex.Match(txt);
                if (dmatch.Success || weekdays.Any(w => txt.Contains(w)))
                {
                    if (dmatch.Success) currentDate = dmatch.Value;
                    var wd = weekdays.FirstOrDefault(w => txt.Contains(w));
                    if (wd != null) currentWeekday = wd;
                    continue;
                }

                // if element is a table with timetable class, parse it as a day/column
                if (el.TagName.Equals("table", StringComparison.OrdinalIgnoreCase) && (el.GetAttribute("class") ?? string.Empty).Contains("table"))
                {
                    // try to find subgroup label in previous few elements
                    string subgroup = null;
                    for (int k = i - 1; k >= Math.Max(0, i - 8); k--)
                    {
                        var pt = Regex.Match(bodyElems[k].Text ?? string.Empty, "\\b(?=.{1,4}$)(?=.*\\d)(?=.*\\p{L})[^\\s]+\\b");
                        if (pt.Success)
                        {
                            subgroup = pt.Value.Trim();
                            break;
                        }
                    }

                    var sb = new StringBuilder();
                    var header = string.Empty;
                    if (!string.IsNullOrEmpty(subgroup)) header += subgroup + " ";
                    if (!string.IsNullOrEmpty(currentWeekday) && !string.IsNullOrEmpty(currentDate)) header += $"{currentWeekday} ({currentDate})";
                    else if (!string.IsNullOrEmpty(currentWeekday)) header += currentWeekday;
                    else if (!string.IsNullOrEmpty(currentDate)) header += currentDate;
                    if (!string.IsNullOrWhiteSpace(header)) sb.AppendLine(header);

                    // parse rows
                    var trs = el.FindElements(By.CssSelector("tbody tr")).ToList();
                    foreach (var tr in trs)
                    {
                        var tds = tr.FindElements(By.TagName("td")).ToArray();
                        if (tds.Length < 3) continue;

                        var num = Regex.Replace(tds[0].Text ?? string.Empty, "\\s+", " ").Trim();
                        var time = Regex.Replace(tds[1].Text ?? string.Empty, "\\s+", " ").Trim();
                        var detailsTd = tds[2];
                        var detailsText = Regex.Replace(detailsTd.Text ?? string.Empty, "\\s+", " ").Trim();

                        // gather URLs from anchors and plain text
                        var zoomLinks = new List<string>();
                        try
                        {
                            var links = detailsTd.FindElements(By.TagName("a"));
                            foreach (var a in links)
                            {
                                var href = a.GetAttribute("href") ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(href) && (href.StartsWith("http://") || href.StartsWith("https://")))
                                {
                                    if (!zoomLinks.Contains(href)) zoomLinks.Add(href);
                                }
                            }
                        }
                        catch { }

                        // also find plain URLs in text
                        foreach (Match m in Regex.Matches(detailsText ?? string.Empty, "https?://\\S+"))
                        {
                            var u = m.Value.Trim().TrimEnd('.', ',', ';');
                            if (!zoomLinks.Contains(u)) zoomLinks.Add(u);
                        }

                        // determine subject/teacher or special markers
                        string subject = detailsText;
                        string teacher = string.Empty;

                        if (string.IsNullOrWhiteSpace(detailsText))
                        {
                            subject = "Немає пари";
                        }
                        else if (detailsText.IndexOf("вікно", StringComparison.OrdinalIgnoreCase) >= 0 || detailsText.IndexOf("вiкно", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            subject = "Вікно";
                        }
                        else
                        {
                            var teacherMatch = Regex.Match(detailsText, "\\b[А-ЯІЇЄҐЁ][\\p{L}-]+\\s+[А-ЯІЇЄҐЁ]\\.?\\s*[А-ЯІЇЄҐЁ]\\.?$");
                            if (teacherMatch.Success)
                            {
                                teacher = teacherMatch.Value.Trim();
                                subject = detailsText.Substring(0, teacherMatch.Index).Trim().TrimEnd('-', ',', ';');
                            }
                            else
                            {
                                // maybe teacher on next line
                                var parts = detailsText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                                if (parts.Length >= 2)
                                {
                                    // last part likely teacher
                                    var last = parts.Last();
                                    if (Regex.IsMatch(last, "^[А-ЯІЇЄҐЁ].*\\.[А-ЯІЇЄҐЁ]?$") || Regex.IsMatch(last, "[А-ЯІЇЄҐЁ]\\."))
                                    {
                                        teacher = last;
                                        subject = string.Join(" ", parts.Take(parts.Length - 1));
                                    }
                                    else
                                    {
                                        subject = string.Join(" ", parts);
                                    }
                                }
                            }
                        }

                        var line = new StringBuilder();
                        // Number with dot
                        if (!string.IsNullOrEmpty(num))
                        {
                            var n = num.Trim();
                            line.Append(n + ". ");
                        }

                        // normalize time to 'HH:MM - HH:MM'
                        var timesInCell = Regex.Matches(time ?? string.Empty, "\\d{2}:\\d{2}").Cast<Match>().Select(m => m.Value).ToArray();
                        if (timesInCell.Length >= 2)
                        {
                            line.Append(timesInCell[0] + " - " + timesInCell[1] + " - ");
                        }
                        else if (!string.IsNullOrEmpty(time))
                        {
                            line.Append(time + " - ");
                        }

                        // Subject
                        var cleanedSubject = subject?.Trim() ?? string.Empty;
                        line.Append(cleanedSubject);

                        // Teacher
                        if (!string.IsNullOrEmpty(teacher))
                        {
                            line.Append(" - " + teacher);
                        }

                        sb.AppendLine(line.ToString().Trim());

                        // include zoom links each on its own line; prefer explicit zoom links first
                        foreach (var lnk in zoomLinks)
                        {
                            if (lnk.Contains("zoom")) sb.AppendLine("Zoom: " + lnk);
                        }
                        // if no explicit zoom in links but there are links, show them
                        foreach (var lnk in zoomLinks.Where(l => !l.Contains("zoom"))) sb.AppendLine(lnk);

                        sb.AppendLine();
                    }

                    var block = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(block)) dayMessages.Add(block);
                }
            }

            return dayMessages;
        }
        finally
        {
            try { driver.Quit(); } catch { }
        }
    }
}
