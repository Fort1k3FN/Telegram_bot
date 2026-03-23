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
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.IO;

// Simple Telegram bot: on /start scrape timetable and send one message per day.
class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // Prompt the user to enter the bot token interactively
        Console.Write("Введіть токен бота (TELEGRAM_BOT_TOKEN): ");
        var token = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Token not provided. Program will exit.");
            return;
        }

        var timetableUrl = Environment.GetEnvironmentVariable("TIMETABLE_URL") ?? "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient { BaseAddress = new Uri($"https://api.telegram.org/bot{token}/") };
        using var cts = new CancellationTokenSource();

        Console.WriteLine($"Using timetable URL: {timetableUrl}");
        Console.WriteLine("Polling for updates. Send /start to the bot to receive the schedule (one message per day). Press Ctrl+C to exit.");

        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Stopping...");
            cts.Cancel();
            e.Cancel = true;
        };

        await PollUpdatesAsync(http, timetableUrl, cts.Token);
    }

    static async Task PollUpdatesAsync(HttpClient http, string timetableUrl, CancellationToken ct)
    {
        int offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await http.GetAsync($"getUpdates?timeout=30&offset={offset}", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine("getUpdates failed: " + resp.StatusCode);
                    await Task.Delay(2000, ct);
                    continue;
                }

                var content = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (!root.GetProperty("ok").GetBoolean()) { await Task.Delay(1000, ct); continue; }

                var results = root.GetProperty("result");
                foreach (var upd in results.EnumerateArray())
                {
                    offset = Math.Max(offset, upd.GetProperty("update_id").GetInt32() + 1);
                    if (!upd.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? string.Empty;
                    var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();
                    Console.WriteLine($"Message from {chatId}: {text}");

                    if (text.TrimStart().StartsWith("/start", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendMessageAsync(http, chatId, "Завантажую розклад...", ct);
                        try
                        {
                            var dayMessages = await ScrapeScheduleAsync(timetableUrl, ct);
                            if (dayMessages == null || dayMessages.Count == 0)
                            {
                                await SendMessageAsync(http, chatId, "Не вдалося найдитись розклад на сторінці.", ct);
                                continue;
                            }
                            foreach (var day in dayMessages)
                            {
                                if (string.IsNullOrWhiteSpace(day)) continue;
                                await SendMessageAsync(http, chatId, day, ct);
                                await Task.Delay(300, ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error scraping/sending: " + ex.Message);
                            await SendMessageAsync(http, chatId, "Помилка при отриманні розкладу: " + ex.Message, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine("Polling error: " + ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }

    static async Task SendMessageAsync(HttpClient http, long chatId, string text, CancellationToken ct)
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
        options.AddArgument("--disable-gpu");
        options.AddArgument("--disable-dev-shm-usage");

        using var driver = new ChromeDriver(options);
        try
        {
            driver.Navigate().GoToUrl(url);
            await Task.Delay(800, ct);

            var rows = driver.FindElements(By.TagName("tr"));

            var timeRegex = new Regex("\\b\\d{2}:\\d{2}\\b");
            var dateRegex = new Regex("\\b\\d{2}\\.\\d{2}\\.\\d{4}\\b");
            string[] weekdays = new[] { "Понеділок", "Вівторок", "Середа", "Четвер", "П'ятниця", "Субота", "Неділя" };

            var extracted = new List<string>();
            foreach (var r in rows)
            {
                var t = r.Text?.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                var norm = Regex.Replace(t, "\\s+", " ");
                if (timeRegex.IsMatch(norm) || dateRegex.IsMatch(norm) || weekdays.Any(w => norm.Contains(w))) extracted.Add(norm);
            }

            var dayMessages = new List<string>();
            var sb = new StringBuilder();
            string currentDate = null;
            string currentWeekday = null;

            for (int i = 0; i < extracted.Count; i++)
            {
                var line = extracted[i];
                var dateM = dateRegex.Match(line);
                var wd = weekdays.FirstOrDefault(w => line.Contains(w));

                if (dateM.Success || wd != null)
                {
                    if (sb.Length > 0)
                    {
                        dayMessages.Add(sb.ToString().Trim());
                        sb.Clear();
                    }
                    if (dateM.Success) currentDate = dateM.Value;
                    if (wd != null) currentWeekday = wd;
                    var header = !string.IsNullOrEmpty(currentWeekday) && !string.IsNullOrEmpty(currentDate)
                        ? $"{currentWeekday} ({currentDate})"
                        : (!string.IsNullOrEmpty(currentWeekday) ? currentWeekday : currentDate ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(header)) sb.AppendLine(header);
                    continue;
                }

                var times = timeRegex.Matches(line).Cast<Match>().Select(m => m.Value).ToArray();
                if (times.Length >= 2)
                {
                    for (int ti = 0; ti + 1 < times.Length; ti += 2)
                    {
                        var start = times[ti];
                        var end = times[ti + 1];
                        int idxSecond = line.IndexOf(end, StringComparison.Ordinal);
                        int nextIdx = line.Length;
                        if (ti + 2 < times.Length) nextIdx = line.IndexOf(times[ti + 2], StringComparison.Ordinal);
                        var after = line.Substring(idxSecond + end.Length, nextIdx - (idxSecond + end.Length)).Trim();

                        // attempt to extract teacher (Lastname I.O.)
                        string teacher = string.Empty;
                        string subject = after;
                        var tmatch = Regex.Match(after, "\\b[А-ЯІЇЄҐЁ][\\p{L}-]+\\s+[А-ЯІЇЄҐЁ]\\.[А-ЯІЇЄҐЁ]?\\b");
                        if (tmatch.Success)
                        {
                            teacher = tmatch.Value.Trim();
                            subject = after.Substring(0, tmatch.Index).Trim().TrimEnd(',', '-');
                        }
                        else if (i + 1 < extracted.Count)
                        {
                            var nextLine = extracted[i + 1];
                            var nxt = Regex.Match(nextLine, "\\b[А-ЯІЇЄҐЁ][\\p{L}-]+\\s+[А-ЯІЇЄҐЁ]\\.[А-ЯІЇЄҐЁ]?\\b");
                            if (nxt.Success)
                            {
                                teacher = nxt.Value.Trim();
                                i++; // skip next line
                            }
                        }

                        sb.AppendLine($"{start} - {end} {subject}".Trim());
                        if (!string.IsNullOrEmpty(teacher)) sb.AppendLine(teacher);
                        sb.AppendLine();
                    }
                    continue;
                }

                if (line.Length > 40)
                {
                    sb.AppendLine(line);
                    sb.AppendLine();
                }
            }

            if (sb.Length > 0) dayMessages.Add(sb.ToString().Trim());
            return dayMessages;
        }
        finally
        {
            try { driver.Quit(); } catch { }
        }
    }
}
