using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HtmlAgilityPack;
using System.IO;
using System.Text.RegularExpressions;

class Program
{
    static string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";
    static string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

    static HashSet<long> Users = new HashSet<long>();
    static string LastSnapshot = "";

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        LoadUsers();

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        Console.WriteLine("BOT STARTED");

        // 🔄 авто перевірка
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var data = await ParseSchedule();

                    var snap = string.Join("\n", data)
                        .Replace(" ", "")
                        .Replace("\n", "")
                        .Replace("\r", "");

                    if (!string.IsNullOrEmpty(LastSnapshot) && snap != LastSnapshot)
                    {
                        Console.WriteLine("РОЗКЛАД ЗМІНИВСЯ!");

                        foreach (var user in Users)
                        {
                            await Send(http, user,
                                "⚠️ РОЗКЛАД ОНОВЛЕНО!\n\nНатисни кнопку 👇");

                            await SendButtons(http, user,
                                "🔽 Оновити розклад",
                                "check_schedule");
                        }

                        LastSnapshot = snap;
                    }
                    else if (string.IsNullOrEmpty(LastSnapshot))
                    {
                        LastSnapshot = snap;
                    }
                }
                catch { }

                await Task.Delay(30000);
            }
        });

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

                    // 📩 MESSAGE
                    if (upd.TryGetProperty("message", out var msg))
                    {
                        if (!msg.TryGetProperty("text", out var textEl)) continue;

                        var text = textEl.GetString() ?? "";
                        var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();

                        Users.Add(chatId);
                        SaveUsers();

                        if (text == "/start")
                        {
                            await Send(http, chatId,
                                "👋 Привіт!\n\nНатисни кнопку або введи /розклад");

                            await http.PostAsJsonAsync("sendMessage", new
                            {
                                chat_id = chatId,
                                text = "👇 Обери:",
                                reply_markup = new
                                {
                                    keyboard = new[]
                                    {
                                        new[] { new { text = "📅 Розклад" } }
                                    },
                                    resize_keyboard = true
                                }
                            });
                        }

                        if (text == "/розклад" || text == "📅 Розклад")
                        {
                            await Send(http, chatId, "⏳ Завантажую розклад...");

                            var data = await ParseSchedule();

                            foreach (var d in data)
                            {
                                await Send(http, chatId, d);
                                await Task.Delay(300);
                            }
                        }
                    }

                    // 🔘 CALLBACK
                    if (upd.TryGetProperty("callback_query", out var cb))
                    {
                        var data = cb.GetProperty("data").GetString();
                        var chatId = cb.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64();

                        if (data == "check_schedule")
                        {
                            var newData = await ParseSchedule();

                            var snap = string.Join("\n", newData)
                                .Replace(" ", "")
                                .Replace("\n", "")
                                .Replace("\r", "");

                            if (snap == LastSnapshot)
                            {
                                await http.PostAsJsonAsync("sendMessage", new
                                {
                                    chat_id = chatId,
                                    text = "✅ В розкладі немає змін\n\nПоказати ще раз?",
                                    reply_markup = new
                                    {
                                        inline_keyboard = new[]
                                        {
                                            new[]
                                            {
                                                new { text = "✔️ Так", callback_data = "force" },
                                                new { text = "❌ Ні", callback_data = "cancel" }
                                            }
                                        }
                                    }
                                });
                            }
                            else
                            {
                                foreach (var d in newData)
                                    await Send(http, chatId, d);

                                LastSnapshot = snap;
                            }
                        }

                        if (data == "force")
                        {
                            var newData = await ParseSchedule();

                            foreach (var d in newData)
                                await Send(http, chatId, d);
                        }

                        if (data == "cancel")
                        {
                            await Send(http, chatId,
                                "😊 Добре, я повідомлю якщо буде зміна!");
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

    static async Task SendButtons(HttpClient http, long chatId, string text, string data)
    {
        await http.PostAsJsonAsync("sendMessage", new
        {
            chat_id = chatId,
            text = text,
            reply_markup = new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "🔄 Оновлення розкладу", callback_data = data }
                    }
                }
            }
        });
    }

    static void SaveUsers()
    {
        File.WriteAllLines("users.txt", Users.Select(x => x.ToString()));
    }

    static void LoadUsers()
    {
        if (!File.Exists("users.txt")) return;

        foreach (var line in File.ReadAllLines("users.txt"))
        {
            if (long.TryParse(line, out long id))
                Users.Add(id);
        }
    }

    static async Task<List<string>> ParseSchedule()
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

        string[] days = {
            "Неділя","Понеділок","Вівторок",
            "Середа","Четвер","П'ятниця","Субота"
        };

        int i = 0;

        foreach (var table in tables)
        {
            var date = DateTime.Today.AddDays(i);
            var day = days[(int)date.DayOfWeek];

            var sb = new StringBuilder();
            sb.AppendLine($"📅 {date:dd.MM.yy} - {day}");
            sb.AppendLine("━━━━━━━━━━━━━━");

            var rows = table.SelectNodes(".//tr");
            if (rows == null) continue;

            foreach (var row in rows)
            {
                var cols = row.SelectNodes(".//td");
                if (cols == null || cols.Count < 3) continue;

                var timeRaw = cols[1].InnerText.Trim();
                var subject = cols[2].InnerText.Trim();

                var times = Regex.Matches(timeRaw, @"\d{2}:\d{2}")
                    .Select(x => x.Value).ToArray();

                string time = times.Length >= 2
                    ? $"{times[0]} - {times[1]}"
                    : timeRaw;

                if (string.IsNullOrWhiteSpace(subject))
                    subject = "😴 Пари немає";

                sb.AppendLine($"⏰ {time}");
                sb.AppendLine($"📖 {subject}");
                sb.AppendLine();
            }

            var text = sb.ToString().Trim();

            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);

            i++;
        }

        return result;
    }
}