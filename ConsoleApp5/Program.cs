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
    static HashSet<long> Admins = new() { 828027108 }; // ТИ ГОЛОВНИЙ АДМІН

    static Dictionary<string, string> CustomCommands = new();

    static string LastSnapshot = "";
    static List<long> Subscribers = new();

    static string adminsFile = "admins.txt";

    static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;

        LoadAdmins();

        string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";
        string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;

        Console.WriteLine("BOT STARTED");

        // 🔥 авто перевірка змін
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var days = await ParseSchedule(url);
                    var snapshot = string.Join("\n", days);

                    if (LastSnapshot != "" && snapshot != LastSnapshot)
                    {
                        foreach (var user in Subscribers)
                        {
                            await Send(http, user, "⚠️ Розклад змінився!");
                        }
                    }

                    LastSnapshot = snapshot;
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

                    var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();

                    if (!Subscribers.Contains(chatId))
                        Subscribers.Add(chatId);

                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? "";

                    Console.WriteLine($"MSG: {text}");

                    bool isAdmin = Admins.Contains(chatId);

                    // 🔐 АДМІН ПАНЕЛЬ
                    if (text == "@admin" && isAdmin)
                    {
                        await SendAdminPanel(http, chatId);
                        continue;
                    }

                    // ➕ ДОДАТИ АДМІНА
                    if (text.StartsWith("/addadmin") && isAdmin)
                    {
                        var parts = text.Split(' ');

                        if (parts.Length < 2)
                        {
                            await Send(http, chatId, "❌ Формат: /addadmin ID");
                            continue;
                        }

                        if (long.TryParse(parts[1], out long newAdmin))
                        {
                            Admins.Add(newAdmin);
                            SaveAdmins();
                            await Send(http, chatId, "✅ Адміна додано");
                        }

                        continue;
                    }

                    // ❌ ВИДАЛИТИ АДМІНА
                    if (text.StartsWith("/deladmin") && isAdmin)
                    {
                        var parts = text.Split(' ');

                        if (parts.Length < 2) continue;

                        if (long.TryParse(parts[1], out long delAdmin))
                        {
                            Admins.Remove(delAdmin);
                            SaveAdmins();
                            await Send(http, chatId, "🗑 Адміна видалено");
                        }

                        continue;
                    }

                    // ➕ ДОДАТИ КОМАНДУ
                    if (text.StartsWith("/add") && isAdmin)
                    {
                        var parts = text.Split(' ', 3);

                        if (parts.Length < 3)
                        {
                            await Send(http, chatId, "❌ Формат: /add команда текст");
                            continue;
                        }

                        CustomCommands[parts[1]] = parts[2];
                        await Send(http, chatId, "✅ Додано");
                        continue;
                    }

                    // ❌ ВИДАЛИТИ КОМАНДУ
                    if (text.StartsWith("/del") && isAdmin)
                    {
                        var parts = text.Split(' ', 2);

                        if (parts.Length < 2) continue;

                        CustomCommands.Remove(parts[1]);
                        await Send(http, chatId, "🗑 Видалено");
                        continue;
                    }

                    // 🤖 КАСТОМ КОМАНДИ
                    if (CustomCommands.ContainsKey(text))
                    {
                        await Send(http, chatId, CustomCommands[text]);
                        continue;
                    }

                    // 📅 РОЗКЛАД
                    if (text.StartsWith("/start"))
                    {
                        await Send(http, chatId, "📚 Завантажую розклад...");

                        var days = await ParseSchedule(url);

                        await Send(http, chatId, "📅 Розклад на наступний тиждень:");

                        foreach (var d in days)
                        {
                            await Send(http, chatId, d);
                            await Task.Delay(300);
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

    static void LoadAdmins()
    {
        if (!File.Exists(adminsFile)) return;

        var lines = File.ReadAllLines(adminsFile);

        foreach (var l in lines)
            if (long.TryParse(l, out long id))
                Admins.Add(id);
    }

    static void SaveAdmins()
    {
        File.WriteAllLines(adminsFile, Admins.Select(x => x.ToString()));
    }

    static async Task Send(HttpClient http, long chatId, string text)
    {
        await http.PostAsJsonAsync("sendMessage", new
        {
            chat_id = chatId,
            text = text
        });
    }

    static async Task SendAdminPanel(HttpClient http, long chatId)
    {
        var keyboard = new
        {
            inline_keyboard = new[]
            {
                new[]
                {
                    new { text = "➕ Команда", callback_data = "cmd" },
                    new { text = "👤 Адміни", callback_data = "admins" }
                }
            }
        };

        await http.PostAsJsonAsync("sendMessage", new
        {
            chat_id = chatId,
            text = "⚙️ Адмін панель\n\n/addadmin ID\n/deladmin ID",
            reply_markup = keyboard
        });
    }

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

            foreach (var row in rows)
            {
                var cols = row.SelectNodes(".//td");
                if (cols == null || cols.Count < 3) continue;

                var timeRaw = cols[1].InnerText.Trim();
                var subject = cols[2].InnerText.Trim();

                var times = Regex.Matches(timeRaw, @"\d{2}:\d{2}")
                                 .Select(x => x.Value)
                                 .ToArray();

                string time = times.Length >= 2
                    ? $"{times[0]} - {times[1]}"
                    : timeRaw;

                if (string.IsNullOrWhiteSpace(subject))
                    subject = "Пари немає";

                sb.AppendLine($"{time} - {subject}");
            }

            var text = sb.ToString().Trim();

            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);
        }

        return result;
    }
}