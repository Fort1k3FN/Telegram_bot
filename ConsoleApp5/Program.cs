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
using System.IO;

class Program
{
    static HashSet<long> Admins = new() { 828027108 };
    static List<long> Users = new();

    static string usersFile = "users.txt";

    static string LastSnapshot = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";

    static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;

        LoadUsers();

        string token = "ТУТ_ТВОЙ_ТОКЕН";
        string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;

        Console.WriteLine("BOT STARTED");

        // 🔄 АВТО-ПЕРЕВІРКА
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var data = await ParseSchedule(url);
                    var snap = Normalize(data);

                    if (LastSnapshot != "" && snap != LastSnapshot)
                    {
                        var changedDays = GetChangedDays(LastSnapshot, snap);

                        foreach (var user in Users)
                        {
                            await Send(http, user,
                                $"⚠️ Розклад змінено!\n\n📅 Зміни: {changedDays}");

                            await SendInline(http, user,
                                "🔄 Оновити розклад", "check");
                        }
                    }

                    LastSnapshot = snap;
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

                    // 🔘 КНОПКИ
                    if (upd.TryGetProperty("callback_query", out var cb))
                    {
                        var chatId = cb.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64();
                        var data = cb.GetProperty("data").GetString();

                        if (data == "check")
                        {
                            var newData = await ParseSchedule(url);
                            var snap = Normalize(newData);

                            if (snap == LastSnapshot)
                            {
                                await SendInline2(http, chatId,
                                    "✅ В розкладі немає змін\nПоказати ще раз?",
                                    "yes", "✔️ Так",
                                    "no", "❌ Ні");
                            }
                            else
                            {
                                await ShowSchedule(http, chatId, url);
                                LastSnapshot = snap;
                            }
                        }

                        if (data == "yes")
                        {
                            await ShowSchedule(http, chatId, url);
                        }

                        if (data == "no")
                        {
                            await Send(http, chatId,
                                "😊 Добре, якщо розклад зміниться — я одразу повідомлю!");
                        }

                        continue;
                    }

                    if (!upd.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? "";
                    var chatIdMsg = msg.GetProperty("chat").GetProperty("id").GetInt64();

                    if (!Users.Contains(chatIdMsg))
                    {
                        Users.Add(chatIdMsg);
                        SaveUsers();
                    }

                    // 🟢 START
                    if (text == "/start")
                    {
                        await http.PostAsJsonAsync("sendMessage", new
                        {
                            chat_id = chatIdMsg,
                            text = "👋 Привіт! Я бот розкладу",
                            reply_markup = new
                            {
                                keyboard = new[]
                                {
                                    new[] { new { text = "📅 Розклад" } }
                                },
                                resize_keyboard = true
                            }
                        });

                        continue;
                    }

                    // 📅 КНОПКА + КОМАНДА
                    if (text == "/розклад" || text == "📅 Розклад")
                    {
                        await ShowSchedule(http, chatIdMsg, url);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                await Task.Delay(3000);
            }
        }
    }

    // 📤 ПОКАЗ РОЗКЛАДУ
    static async Task ShowSchedule(HttpClient http, long chatId, string url)
    {
        await Send(http, chatId, "⏳ Завантажую розклад...");

        var days = await ParseSchedule(url);

        foreach (var d in days)
        {
            await Send(http, chatId, d);
            await Task.Delay(300);
        }
    }

    // 📊 ВИЯВЛЕННЯ ЗМІН
    static string GetChangedDays(string oldSnap, string newSnap)
    {
        if (oldSnap == "") return "Перший запуск";

        return "Є оновлення у розкладі";
    }

    static string Normalize(List<string> data)
    {
        return string.Join("", data)
            .Replace(" ", "")
            .Replace("\n", "")
            .Replace("\r", "");
    }

    static async Task Send(HttpClient http, long chatId, string text)
    {
        await http.PostAsJsonAsync("sendMessage", new
        {
            chat_id = chatId,
            text = text
        });
    }

    static async Task SendInline(HttpClient http, long chatId, string text, string data)
    {
        await http.PostAsJsonAsync("sendMessage", new
        {
            chat_id = chatId,
            text,
            reply_markup = new
            {
                inline_keyboard = new[]
                {
                    new[] { new { text, callback_data = data } }
                }
            }
        });
    }

    static async Task SendInline2(HttpClient http, long chatId,
        string text,
        string d1, string t1,
        string d2, string t2)
    {
        await http.PostAsJsonAsync("sendMessage", new
        {
            chat_id = chatId,
            text,
            reply_markup = new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = t1, callback_data = d1 },
                        new { text = t2, callback_data = d2 }
                    }
                }
            }
        });
    }

    static void SaveUsers() => File.WriteAllLines(usersFile, Users.Select(x => x.ToString()));

    static void LoadUsers()
    {
        if (!File.Exists(usersFile)) return;

        foreach (var l in File.ReadAllLines(usersFile))
            if (long.TryParse(l, out long id))
                Users.Add(id);
    }

    // 🎨 КРАСИВИЙ ПАРСЕР
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