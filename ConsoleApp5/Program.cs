using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Threading;

class Program
{
    static string lastSnapshot = "";

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";
        string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;

        Console.WriteLine("BOT STARTED");

        // 🔁 фоновий чек змін
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var days = await ParseSchedule(url);
                    var snapshot = string.Join("\n", days);

                    if (!string.IsNullOrEmpty(lastSnapshot) && snapshot != lastSnapshot)
                    {
                        Console.WriteLine("РОЗКЛАД ЗМІНИВСЯ");

                        // ⚠️ встав свій chatId
                        long chatId = 123456789;

                        await http.PostAsJsonAsync("sendMessage", new
                        {
                            chat_id = chatId,
                            text = "⚠️ Розклад змінився!"
                        });
                    }

                    lastSnapshot = snapshot;
                }
                catch { }

                await Task.Delay(30000);
            }
        });

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

                    if (text.StartsWith("/start"))
                    {
                        await Send(http, chatId, "📅 Завантажую розклад...");

                        var days = await ParseSchedule(url);

                        if (days.Count == 0)
                        {
                            await Send(http, chatId, "❌ Не вдалося знайти розклад");
                            continue;
                        }

                        await Send(http, chatId, "📆 Розклад на наступний тиждень:\n");

                        foreach (var d in days)
                        {
                            await Send(http, chatId, d);
                            await Task.Delay(400);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                await Task.Delay(5000);
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

    static async Task<List<string>> ParseSchedule(string url)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var html = await http.GetStringAsync(url);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new List<string>();

        var days = new Dictionary<int, string>
        {
            {0, "Понеділок"},
            {1, "Вівторок"},
            {2, "Середа"},
            {3, "Четвер"},
            {4, "П'ятниця"}
        };

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null) return result;

        for (int i = 0; i < Math.Min(5, tables.Count); i++)
        {
            var table = tables[i];
            var rows = table.SelectNodes(".//tr");

            if (rows == null) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"📚 Розклад дня на {days[i]}:\n");

            bool hasLessons = false;

            foreach (var row in rows)
            {
                var cols = row.SelectNodes(".//td");
                if (cols == null || cols.Count < 3) continue;

                var num = cols[0].InnerText.Trim();
                var timeRaw = cols[1].InnerText.Trim();
                var subject = cols[2].InnerText.Trim();

                if (string.IsNullOrWhiteSpace(timeRaw)) continue;

                // 🔥 FIX ЧАСУ
                var times = Regex.Matches(timeRaw, @"\d{2}:\d{2}")
                    .Select(x => x.Value)
                    .ToList();

                string time = timeRaw;

                if (times.Count >= 2)
                    time = $"{times[0]} - {times[1]}";

                if (string.IsNullOrWhiteSpace(subject))
                    subject = "Пари немає";

                sb.AppendLine($"🕐 {num}. {time}");
                sb.AppendLine($"📖 {subject}\n");

                hasLessons = true;
            }

            if (!hasLessons)
                sb.AppendLine("😴 Пари немає");

            result.Add(sb.ToString());
        }

        return result;
    }
}