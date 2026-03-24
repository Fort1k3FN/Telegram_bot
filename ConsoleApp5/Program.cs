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
    static Dictionary<string, string> Commands = new();

    static List<long> Users = new();

    static string adminsFile = "admins.txt";
    static string commandsFile = "commands.json";

    static string LastSnapshot = "";

    static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;

        LoadAdmins();
        LoadCommands();

        string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";
        string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;

        Console.WriteLine("BOT STARTED");

        // 🔥 анти-сон + перевірка змін
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var data = await ParseSchedule(url);
                    var snap = string.Join("\n", data);

                    if (LastSnapshot != "" && snap != LastSnapshot)
                    {
                        foreach (var u in Users)
                            await Send(http, u, "⚠️ Розклад змінився!");
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

                    // CALLBACK
                    if (upd.TryGetProperty("callback_query", out var cb))
                    {
                        var chatId = cb.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64();
                        var data = cb.GetProperty("data").GetString();

                        if (!Admins.Contains(chatId)) continue;

                        if (data == "add")
                            await Send(http, chatId, "✏️ /add команда текст");

                        if (data == "del")
                            await Send(http, chatId, "❌ /del команда");

                        if (data == "send")
                            await Send(http, chatId, "📢 /send текст");

                        continue;
                    }

                    if (!upd.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? "";
                    var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();

                    if (!Users.Contains(chatId))
                        Users.Add(chatId);

                    bool isAdmin = Admins.Contains(chatId);

                    // ID
                    if (text == "/id")
                    {
                        await Send(http, chatId, $"🆔 Твій ID: {chatId}");
                        continue;
                    }

                    // АДМІН ПАНЕЛЬ
                    if (text == "/admin" && isAdmin)
                    {
                        var kb = new
                        {
                            inline_keyboard = new[]
                            {
                                new[]
                                {
                                    new { text = "➕ Команда", callback_data = "add" },
                                    new { text = "❌ Видалити", callback_data = "del" }
                                },
                                new[]
                                {
                                    new { text = "📢 Розсилка", callback_data = "send" }
                                }
                            }
                        };

                        await http.PostAsJsonAsync("sendMessage", new
                        {
                            chat_id = chatId,
                            text = "⚙️ Адмін панель",
                            reply_markup = kb
                        });

                        continue;
                    }

                    // HELP
                    if (text == "/ahelp" && isAdmin)
                    {
                        await Send(http, chatId,
@"👑 Адмін команди:

/admin - панель
/add cmd текст
/del cmd
/send текст
/addadmin ID
/deladmin ID
/id");
                        continue;
                    }

                    // ADMIN ADD
                    if (text.StartsWith("/addadmin") && isAdmin)
                    {
                        var id = long.Parse(text.Split(' ')[1]);
                        Admins.Add(id);
                        SaveAdmins();
                        await Send(http, chatId, "✅ Додано");
                        continue;
                    }

                    if (text.StartsWith("/deladmin") && isAdmin)
                    {
                        var id = long.Parse(text.Split(' ')[1]);
                        Admins.Remove(id);
                        SaveAdmins();
                        await Send(http, chatId, "❌ Видалено");
                        continue;
                    }

                    // COMMANDS SAVE
                    if (text.StartsWith("/add") && isAdmin)
                    {
                        var p = text.Split(' ', 3);
                        Commands[p[1]] = p[2];
                        SaveCommands();
                        await Send(http, chatId, "✅ Додано");
                        continue;
                    }

                    if (text.StartsWith("/del") && isAdmin)
                    {
                        var p = text.Split(' ');
                        Commands.Remove(p[1]);
                        SaveCommands();
                        await Send(http, chatId, "❌ Видалено");
                        continue;
                    }

                    // SEND ALL
                    if (text.StartsWith("/send") && isAdmin)
                    {
                        var msgText = text.Substring(5);

                        foreach (var u in Users)
                        {
                            try { await Send(http, u, $"📢 {msgText}"); }
                            catch { }
                        }

                        await Send(http, chatId, "✅ Розіслано");
                        continue;
                    }

                    // CUSTOM
                    if (Commands.ContainsKey(text))
                    {
                        await Send(http, chatId, Commands[text]);
                        continue;
                    }

                    // START
                    if (text == "/start")
                    {
                        await Send(http, chatId, "📚 Завантажую розклад...");
                        var days = await ParseSchedule(url);

                        foreach (var d in days)
                        {
                            await Send(http, chatId, d);
                            await Task.Delay(300);
                        }
                    }
                }
            }
            catch
            {
                await Task.Delay(3000);
            }
        }
    }

    static async Task Send(HttpClient http, long id, string text)
    {
        await http.PostAsJsonAsync("sendMessage", new { chat_id = id, text });
    }

    static void SaveAdmins() => File.WriteAllLines(adminsFile, Admins.Select(x => x.ToString()));
    static void LoadAdmins()
    {
        if (!File.Exists(adminsFile)) return;
        foreach (var l in File.ReadAllLines(adminsFile))
            if (long.TryParse(l, out long id)) Admins.Add(id);
    }

    static void SaveCommands()
    {
        File.WriteAllText(commandsFile, JsonSerializer.Serialize(Commands));
    }

    static void LoadCommands()
    {
        if (!File.Exists(commandsFile)) return;
        Commands = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(commandsFile));
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

        foreach (var table in tables)
        {
            var rows = table.SelectNodes(".//tr");
            if (rows == null) continue;

            var sb = new StringBuilder();

            sb.AppendLine("📅 Розклад дня\n");

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
                    subject = "😴 Пари немає";

                sb.AppendLine($"⏰ {time}");
                sb.AppendLine($"📖 {subject}\n");
            }

            result.Add(sb.ToString());
        }

        return result;
    }
}