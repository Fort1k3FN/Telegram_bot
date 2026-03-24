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
    static string usersFile = "users.txt";

    static string LastSnapshot = "";

    static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;

        LoadAdmins();
        LoadCommands();
        LoadUsers();

        string token = "8360154496:AAFZ85zfNtpF8yzrMzFvXfwqC9mAnp-iV8E";
        string url = "https://asu-srv.pnu.edu.ua/cgi-bin/timetable.cgi?n=700&group=-4975";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{token}/")
        };

        int offset = 0;

        Console.WriteLine("BOT STARTED");

        // 🔄 АВТО-ПЕРЕВІРКА ЗМІН
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
                        foreach (var u in Users)
                        {
                            await Send(http, u, "⚠️ Розклад змінено!");
                        }
                    }

                    LastSnapshot = snap;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("CHECK ERROR: " + ex.Message);
                }

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

                    // 🔘 CALLBACK КНОПКИ
                    if (upd.TryGetProperty("callback_query", out var cb))
                    {
                        var chatIdCb = cb.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64();
                        var data = cb.GetProperty("data").GetString();

                        if (data == "check")
                        {
                            var newData = await ParseSchedule(url);
                            var snap = Normalize(newData);

                            if (snap == LastSnapshot)
                            {
                                await SendInline2(http, chatIdCb,
                                    "✅ В розкладі немає змін\nПоказати ще раз?",
                                    "yes", "✔️ Так",
                                    "no", "❌ Ні");
                            }
                            else
                            {
                                await ShowSchedule(http, chatIdCb, url);
                                LastSnapshot = snap;
                            }
                        }

                        if (data == "yes")
                            await ShowSchedule(http, chatIdCb, url);

                        if (data == "no")
                            await Send(http, chatIdCb, "😊 Добре, повідомлю якщо щось зміниться");

                        continue;
                    }

                    if (!upd.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? "";
                    var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();

                    if (!Users.Contains(chatId))
                    {
                        Users.Add(chatId);
                        SaveUsers();
                    }

                    bool isAdmin = Admins.Contains(chatId);

                    // 🆔 ID
                    if (text == "/id")
                    {
                        await Send(http, chatId, $"🆔 Твій ID: {chatId}");
                        continue;
                    }

                    // 👑 АДМІН ПАНЕЛЬ
                    if (text == "/admin" && isAdmin)
                    {
                        await Send(http, chatId,
@"⚙️ Адмін панель

/add команда текст
/del команда
/send текст
/addadmin ID
/deladmin ID
/ahelp");
                        continue;
                    }

                    if (text == "/ahelp" && isAdmin)
                    {
                        await Send(http, chatId,
@"👑 Адмін команди:
/admin
/add
/del
/send
/addadmin
/deladmin
/id");
                        continue;
                    }

                    // ➕ АДМІН
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

                    // 📢 РОЗСИЛКА
                    if (text.StartsWith("/send") && isAdmin)
                    {
                        var message = text.Substring(5);

                        foreach (var u in Users)
                            await Send(http, u, $"📢 {message}");

                        continue;
                    }

                    // ➕ КОМАНДА
                    if (text.StartsWith("/add") && isAdmin)
                    {
                        var parts = text.Split(' ', 3);
                        Commands[parts[1]] = parts[2];
                        SaveCommands();
                        await Send(http, chatId, "✅ Додано");
                        continue;
                    }

                    if (text.StartsWith("/del") && isAdmin)
                    {
                        Commands.Remove(text.Split(' ')[1]);
                        SaveCommands();
                        await Send(http, chatId, "❌ Видалено");
                        continue;
                    }

                    if (Commands.ContainsKey(text))
                    {
                        await Send(http, chatId, Commands[text]);
                        continue;
                    }

                    // 📅 РОЗКЛАД
                    if (text == "/розклад" || text == "📅 Розклад")
                    {
                        await ShowSchedule(http, chatId, url);
                        continue;
                    }

                    if (text == "/start")
                    {
                        await http.PostAsJsonAsync("sendMessage", new
                        {
                            chat_id = chatId,
                            text = "👋 Привіт!",
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                await Task.Delay(3000);
            }
        }
    }

    static async Task ShowSchedule(HttpClient http, long chatId, string url)
    {
        await Send(http, chatId, "⏳ Завантажую розклад...");

        try
        {
            var days = await ParseSchedule(url);

            if (days.Count == 0)
            {
                await Send(http, chatId, "❌ Не вдалося знайти розклад");
                return;
            }

            foreach (var d in days)
            {
                await Send(http, chatId, d);
                await Task.Delay(300);
            }
        }
        catch (Exception ex)
        {
            await Send(http, chatId, "❌ Помилка: " + ex.Message);
        }
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
        await http.PostAsJsonAsync("sendMessage", new { chat_id = chatId, text });
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

    static void SaveAdmins() => File.WriteAllLines(adminsFile, Admins.Select(x => x.ToString()));
    static void SaveUsers() => File.WriteAllLines(usersFile, Users.Select(x => x.ToString()));
    static void SaveCommands() => File.WriteAllText(commandsFile, JsonSerializer.Serialize(Commands));

    static void LoadAdmins()
    {
        if (!File.Exists(adminsFile)) return;
        foreach (var l in File.ReadAllLines(adminsFile))
            if (long.TryParse(l, out long id))
                Admins.Add(id);
    }

    static void LoadUsers()
    {
        if (!File.Exists(usersFile)) return;
        foreach (var l in File.ReadAllLines(usersFile))
            if (long.TryParse(l, out long id))
                Users.Add(id);
    }

    static void LoadCommands()
    {
        if (!File.Exists(commandsFile)) return;
        Commands = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(commandsFile))
                   ?? new Dictionary<string, string>();
    }

    // 🔥 ФІКС ПАРСЕРА
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

        string[] days = { "Понеділок", "Вівторок", "Середа", "Четвер", "П'ятниця" };

        int i = 0;

        foreach (var table in tables)
        {
            var rows = table.SelectNodes(".//tr");
            if (rows == null) continue;

            var sb = new StringBuilder();

            var date = DateTime.Today.AddDays(i);

            var day = i < days.Length ? days[i] : "День";

            sb.AppendLine($"📅 {date:dd.MM.yy} - {day}");
            sb.AppendLine("━━━━━━━━━━━━━━");

            bool has = false;

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

                has = true;
            }

            if (!has)
                sb.AppendLine("😴 Пари немає");

            var text = sb.ToString().Trim();

            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);

            i++;
        }

        return result;
    }
}