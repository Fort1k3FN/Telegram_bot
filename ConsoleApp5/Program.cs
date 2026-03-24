using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HtmlAgilityPack;

class Program
{
    static async Task Main()
    {
        // 🔥 потрібно для windows-1251
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Console.OutputEncoding = Encoding.UTF8;

        string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";

        string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;

        Console.WriteLine("BOT STARTED");

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

                    Console.WriteLine($"MSG: {text}");

                    if (text.StartsWith("/start"))
                    {
                        await Send(http, chatId, "⏳ Завантажую розклад...");

                        var days = await ParseSchedule(url);

                        if (days.Count == 0)
                        {
                            await Send(http, chatId, "❌ Не вдалося знайти розклад");
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

    static async Task<List<string>> ParseSchedule(string url)
    {
        using var http = new HttpClient();

        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        // 🔥 фікс кодування
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
                var time = cols[1].InnerText.Trim();
                var subject = cols[2].InnerText.Trim();

                if (string.IsNullOrWhiteSpace(time)) continue;

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