using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HtmlAgilityPack;
using System.Threading;

class Program
{
    static string lastSnapshot = "";

    static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;

        string token = Environment.GetEnvironmentVariable("BOT_TOKEN")
                       ?? "ТУТ_СВІЙ_ТОКЕН";

        string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;
        long? lastUser = null;

        Console.WriteLine("BOT STARTED");

        // 🔥 Фоновий таск (перевірка змін)
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var current = await GetRawSchedule(url);

                    if (!string.IsNullOrEmpty(lastSnapshot) && current != lastSnapshot && lastUser != null)
                    {
                        await Send(http, lastUser.Value, "⚠️ Розклад змінився!");
                    }

                    lastSnapshot = current;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("CHECK ERROR: " + ex.Message);
                }

                await Task.Delay(30000);
            }
        });

        // 🔥 анти-сон (пінг самого себе)
        _ = Task.Run(async () =>
        {
            using var pingHttp = new HttpClient();
            while (true)
            {
                try
                {
                    await pingHttp.GetAsync("https://google.com");
                }
                catch { }

                await Task.Delay(60000);
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

                    lastUser = chatId;

                    Console.WriteLine($"MSG: {text}");

                    if (text.StartsWith("/start"))
                    {
                        await Send(http, chatId, "⏳ Завантажую розклад...");

                        var days = await ParseSchedule(url);

                        if (days.Count == 0)
                        {
                            await Send(http, chatId, "❌ Не знайдено розклад");
                            continue;
                        }

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
            text = text,
            parse_mode = "HTML"
        });
    }

    // 🔥 СИРИЙ HTML (для перевірки змін)
    static async Task<string> GetRawSchedule(string url)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var bytes = await http.GetByteArrayAsync(url);
        return Encoding.GetEncoding("windows-1251").GetString(bytes);
    }

    // 🔥 ОСНОВНИЙ ПАРСЕР
    static async Task<List<string>> ParseSchedule(string url)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var bytes = await http.GetByteArrayAsync(url);
        var html = Encoding.GetEncoding("windows-1251").GetString(bytes);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new List<string>();

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null) return result;

        foreach (var table in tables)
        {
            var rows = table.SelectNodes(".//tr");
            if (rows == null) continue;

            var sb = new StringBuilder();

            sb.AppendLine("<b>📅 Розклад дня</b>");
            sb.AppendLine("━━━━━━━━━━━━━━");

            foreach (var row in rows)
            {
                var cols = row.SelectNodes(".//td");
                if (cols == null || cols.Count < 3) continue;

                var num = cols[0].InnerText.Trim();
                var timeRaw = cols[1].InnerText.Trim();
                var subject = cols[2].InnerText.Trim();

                // 🔥 ФІКС ЧАСУ
                var times = System.Text.RegularExpressions.Regex
                    .Matches(timeRaw, @"\d{2}:\d{2}")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => m.Value)
                    .ToList();

                string time = times.Count >= 2
                    ? $"{times[0]} - {times[1]}"
                    : timeRaw;

                // 🔥 ЯКЩО НЕМАЄ ПАРИ
                if (string.IsNullOrWhiteSpace(subject))
                    subject = "Пари немає";

                var number = num.Replace(".", "").Trim();

                sb.AppendLine($"🔹 <b>Пара {number}</b>");
                sb.AppendLine($"⏰ {time}");
                sb.AppendLine($"📘 {subject}");
                sb.AppendLine("──────────────");
            }

            var text = sb.ToString().Trim();

            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);
        }

        return result;
    }
}