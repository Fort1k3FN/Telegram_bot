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
using System.IO;

class Program
{
    static readonly string SubscribersFile = Path.Combine(AppContext.BaseDirectory, "subscribers.txt");
    static readonly string SnapshotFile = Path.Combine(AppContext.BaseDirectory, "last_snapshot.txt");
    static readonly HashSet<long> Subscribers = new HashSet<long>();
    static string LastSnapshot = null;
    static readonly SemaphoreSlim ScrapeLock = new SemaphoreSlim(1, 1);

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";

        var timetableUrl = Environment.GetEnvironmentVariable("TIMETABLE_URL")
            ?? "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        Console.WriteLine("Bot started");

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;

        while (true)
        {
            try
            {
                var resp = await http.GetAsync($"getUpdates?timeout=30&offset={offset}");
                var json = await resp.Content.ReadAsStringAsync();

                var doc = JsonDocument.Parse(json);
                var result = doc.RootElement.GetProperty("result");

                foreach (var upd in result.EnumerateArray())
                {
                    offset = upd.GetProperty("update_id").GetInt32() + 1;

                    if (!upd.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? "";
                    var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();

                    Console.WriteLine($"Message: {text}");

                    if (text.StartsWith("/start"))
                    {
                        await Send(http, chatId, "Завантажую розклад...");

                        try
                        {
                            var days = await ScrapeScheduleAsync(timetableUrl);

                            if (days.Count == 0)
                            {
                                await Send(http, chatId, "Не вдалося знайти розклад");
                                continue;
                            }

                            foreach (var day in days)
                            {
                                await Send(http, chatId, day);
                                await Task.Delay(300);
                            }
                        }
                        catch (Exception ex)
                        {
                            await Send(http, chatId, "Помилка: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                await Task.Delay(2000);
            }
        }
    }

    static async Task Send(HttpClient http, long chatId, string text)
    {
        await http.PostAsJsonAsync("sendMessage", new
        {
            chat_id = chatId,
            text = text
        });
    }

    // 🔥 НОВИЙ ПАРСЕР БЕЗ SELENIUM
    static async Task<List<string>> ScrapeScheduleAsync(string url)
    {
        using var http = new HttpClient();

        var html = await http.GetStringAsync(url);

        var daysOrder = new[]
        {
            "Понеділок",
            "Вівторок",
            "Середа",
            "Четвер",
            "П'ятниця",
            "Субота",
            "Неділя"
        };

        var result = new List<string>();

        foreach (var day in daysOrder)
        {
            if (!html.Contains(day)) continue;

            var block = new StringBuilder();
            block.AppendLine(day);

            var matches = Regex.Matches(html, @"\d{2}:\d{2}.*?</tr>", RegexOptions.Singleline);

            foreach (Match m in matches)
            {
                var clean = Regex.Replace(m.Value, "<.*?>", "").Trim();

                if (!string.IsNullOrWhiteSpace(clean))
                    block.AppendLine(clean);
            }

            if (block.Length > day.Length)
                result.Add(block.ToString());
        }

        return result;
    }
}