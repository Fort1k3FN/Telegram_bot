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
    static HashSet<long> Admins = new() { 828027108, 1034256806 };

    static Dictionary<string, string> Commands = new();
    static List<long> Users = new();

    static Dictionary<long, DateTime> LastSeen = new();
    static HashSet<long> SpyAdmins = new();
    static Dictionary<long, string> Usernames = new();

    // 🔥 НОВЕ
    static Dictionary<long, (long by, DateTime date, string reason)> BannedUsers = new();
    static readonly HashSet<long> SuperAdmins = new() { 828027108, 1034256806 };

    static string adminsFile = "admins.txt";
    static string commandsFile = "commands.json";
    static string usersFile = "users.txt";

    static string LastSnapshot = "";

    static readonly HashSet<string> ProtectedCommands = new()
    {
        "/admin", "/ahelp", "/розклад", "📅 Розклад", "/start"
    };

    static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;

        // 🌐 АНТІ-СОН
        _ = Task.Run(async () =>
        {
            var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://*:{port}/");
            listener.Start();

            Console.WriteLine($"Server running on {port}");

            while (true)
            {
                var ctx = await listener.GetContextAsync();
                var buffer = Encoding.UTF8.GetBytes("OK");
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.Close();
            }
        });

        // 🔄 SELF PING
        _ = Task.Run(async () =>
        {
            var urlPing = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
            if (string.IsNullOrEmpty(urlPing)) return;

            using var client = new HttpClient();

            while (true)
            {
                try { await client.GetAsync(urlPing); } catch { }
                await Task.Delay(300000);
            }
        });

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

        // 🔥 ПЕРЕВІРКА ЗМІН
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var data = await ParseSchedule(url);
                    var snap = string.Join("|||", data);

                    if (LastSnapshot != "" && snap != LastSnapshot)
                    {
                        var oldData = LastSnapshot.Split("|||").ToList();
                        var changes = GetDetailedChanges(oldData, data);

                        string message = changes.Count > 0
                            ? string.Join("\n", changes)
                            : "⚠️ Розклад оновлено";

                        foreach (var u in Users)
                            await Send(http, u, message);
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

                    if (!upd.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? "";
                    var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();

                    var username = msg.GetProperty("from").TryGetProperty("username", out var u)
                        ? "@" + u.GetString()
                        : "без username";

                    Usernames[chatId] = username;
                    LastSeen[chatId] = DateTime.Now;

                    // 🚫 БАН
                    if (BannedUsers.ContainsKey(chatId))
                        continue;

                    if (!Users.Contains(chatId))
                    {
                        Users.Add(chatId);
                        SaveUsers();
                    }

                    bool isAdmin = Admins.Contains(chatId);

                    // 🕵️ SPY
                    foreach (var admin in SpyAdmins)
                    {
                        if (admin == chatId) continue;

                        var name = msg.GetProperty("from").GetProperty("first_name").GetString();

                        await Send(http, admin,
$@"🕵️ Повідомлення

👤 {name} ({username})
🆔 {chatId}

💬 {text}");
                    }

                    // 🟢 ONLINE
                    if (text == "/online" && isAdmin)
                    {
                        var now = DateTime.Now;

                        var online = LastSeen
                            .Where(x => (now - x.Value).TotalMinutes < 5)
                            .Select(x => x.Key)
                            .ToList();

                        if (online.Count == 0)
                        {
                            await Send(http, chatId, "😴 Ніхто не онлайн");
                            continue;
                        }

                        var sb = new StringBuilder();
                        sb.AppendLine("🟢 Онлайн:\n");

                        foreach (var id in online)
                        {
                            var name = Usernames.ContainsKey(id) ? Usernames[id] : "";
                            sb.AppendLine($"👤 {id} {name}");
                        }

                        await Send(http, chatId, sb.ToString());
                        continue;
                    }

                    // 👑 АДМІНИ
                    if (text == "/admins" && isAdmin)
                    {
                        var sb = new StringBuilder("👑 Адміни:\n\n");

                        foreach (var id in Admins)
                        {
                            var name = Usernames.ContainsKey(id) ? Usernames[id] : "";
                            sb.AppendLine($"👤 {id} {name}");
                        }

                        await Send(http, chatId, sb.ToString());
                        continue;
                    }

                    if (text == "/spy on" && isAdmin)
                    {
                        SpyAdmins.Add(chatId);
                        await Send(http, chatId, "🕵️ Увімкнено");
                        continue;
                    }

                    if (text == "/spy off" && isAdmin)
                    {
                        SpyAdmins.Remove(chatId);
                        await Send(http, chatId, "❌ Вимкнено");
                        continue;
                    }

                    if (text == "/ahelp" && isAdmin)
                    {
                        await Send(http, chatId,
@"👑 Адмін команди:

/admin
/ahelp
/id

/add
/del
/send

/addadmin
/deladmin
/admins

/online

/spy on
/spy off

/ban
/bans");
                        continue;
                    }

                    // 🚫 BAN
                    if (text.StartsWith("/ban") && isAdmin)
                    {
                        var parts = text.Split(' ', 3);
                        if (parts.Length < 2)
                        {
                            await Send(http, chatId, "⚠️ /ban ID причина");
                            continue;
                        }

                        var id = long.Parse(parts[1]);
                        var reason = parts.Length >= 3 ? parts[2] : "без причини";

                        BannedUsers[id] = (chatId, DateTime.Now, reason);

                        await Send(http, id, "🚫 Вас заблоковано");
                        await Send(http, chatId, "✅ Заблоковано");
                        continue;
                    }

                    if (text == "/bans" && isAdmin)
                    {
                        var sb = new StringBuilder("🚫 Бани:\n\n");

                        foreach (var b in BannedUsers)
                        {
                            sb.AppendLine($"👤 {b.Key}");
                            sb.AppendLine($"Адмін: {b.Value.by}");
                            sb.AppendLine($"Дата: {b.Value.date}");
                            sb.AppendLine($"Причина: {b.Value.reason}\n");
                        }

                        await Send(http, chatId, sb.ToString());
                        continue;
                    }

                    if (text == "/id")
                    {
                        await Send(http, chatId, $"🆔 Твій ID: {chatId}");
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

    static List<string> GetDetailedChanges(List<string> oldData, List<string> newData)
    {
        var changes = new List<string>();

        for (int i = 0; i < Math.Min(oldData.Count, newData.Count); i++)
        {
            if (oldData[i] == newData[i]) continue;

            changes.Add("⚠️ ЗМІНИ В РОЗКЛАДІ\n");
            changes.Add(newData[i]);
            break;
        }

        return changes;
    }

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

    static async Task Send(HttpClient http, long chatId, string text)
    {
        await http.PostAsJsonAsync("sendMessage", new { chat_id = chatId, text });
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

            result.Add(sb.ToString());
            i++;
        }

        return result;
    }
}