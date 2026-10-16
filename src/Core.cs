// Подстрочник — рабочая логика: конфиг, запросы к модели, контекст, чат, хоткеи, захват экрана.
// Поведение перенесено из прежней версии на C без изменений.
using System.Drawing.Imaging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Podstrochnik;

/* ============================== конфиг ============================== */
internal sealed class Cfg
{
    public string ApiKey = "";
    public string Model = "deepseek/deepseek-v4.1-flash";
    public string Host = "https://openrouter.ai/api/v1/chat/completions";
    public bool ReasoningOff = true;
    public int CtxLimit = 50000;

    public static string IniPath => Path.Combine(Program.DataDir, "config.ini");

    public static Cfg Load()
    {
        var c = new Cfg();
        try
        {
            if (File.Exists(IniPath))
            {
                foreach (var line in File.ReadAllLines(IniPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.TrimStart().StartsWith("[")) continue;
                    string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
                    switch (k)
                    {
                        case "api_key": c.ApiKey = Unprotect(v); break;
                        case "model": if (v.Length > 0) c.Model = v; break;
                        case "reasoning_off": c.ReasoningOff = v != "0"; break;
                        case "ctx_limit": if (int.TryParse(v, out int lim) && lim >= 1000) c.CtxLimit = lim; break;
                    }
                }
            }
        }
        catch (Exception ex) { Program.Log("конфиг: ошибка чтения — " + ex.Message); }
        if (string.IsNullOrWhiteSpace(c.ApiKey)) c.ApiKey = EmbeddedKey.Value;
        Program.Log($"конфиг: model={c.Model} ctx={c.CtxLimit} ключ={(c.ApiKey.Length > 0 ? "есть" : "НЕТ")}");
        return c;
    }

    public void SaveKey(string key)
    {
        ApiKey = key;
        try
        {
            var lines = File.Exists(IniPath) ? File.ReadAllLines(IniPath, Encoding.UTF8).ToList() : new List<string>();
            int idx = lines.FindIndex(l => l.TrimStart().StartsWith("api_key"));
            string line = "api_key=" + Protect(key);
            if (idx >= 0) lines[idx] = line; else lines.Insert(0, line);
            File.WriteAllLines(IniPath, lines, Encoding.UTF8);
            Program.Log("конфиг: ключ сохранён");
        }
        catch (Exception ex) { Program.Log("конфиг: не удалось сохранить ключ — " + ex.Message); }
    }

    private static string Protect(string s)
    {
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
        catch { return s; }
    }
    private static string Unprotect(string s)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
        catch { return s; }
    }
}

/* ============================ образцы запросов ============================ */
internal static class Prompts
{
    public const string Translate =
        "Detect the language of the user's text and translate it into Russian. " +
        "Keep the original tone, em dashes and quotes. Output only the translation.";
    public const string Vision =
        "Read all text in the image, then translate it into Russian. Output the " +
        "original text first, then a line with three dashes, then the Russian " +
        "translation. Nothing else.";
    public const string Ocr =
        "Read all text in the image. Output only the text, keeping line breaks.";

    public const string ChatFmt =
        "You are my translator in a small window. The foreign text I work with is in " +
        "the same language as this sample: «{0}». When I write to you in Russian, " +
        "answer with: first, the ready-to-send message in THAT language, then a line of " +
        "three dashes, then the same message in Russian. Never write two Russian versions " +
        "- the first part must be in the sample's language. If you cannot tell the " +
        "language, use English. Short and natural, no explanations, no markdown.";
    public const string ChatPlain =
        "You are my translator in a small window. When I write to you in Russian, answer " +
        "with: first, the ready-to-send message in English, then a line of three " +
        "dashes, then the same message in Russian. Never write two Russian versions. " +
        "Short and natural, no explanations, no markdown.";
    public const string ChatFix =
        "Your previous answer was entirely in Russian, which is wrong: the first part MUST " +
        "be the ready-to-send message in the foreign language of the sample text, and only " +
        "after the three dashes the same message in Russian.";
}

/* =============================== контекст =============================== */
internal sealed class Msg
{
    public string Role = "user";
    public string Text = "";
}

internal sealed class Session
{
    public readonly List<Msg> Msgs = new();
    public int Tokens;
    public string Sample = "";          // образец языка собеседника
    public string? TrText, TrStats;
    public DateTime? TrAt;

    public static int EstTokens(string s) => s.Length / 3 + 1;

    public void SetSample(string t)
    {
        var sb = new StringBuilder();
        foreach (var ch in t)
        {
            if (sb.Length >= 200) break;
            sb.Append(ch is '\n' or '\r' or '\t' ? ' ' : ch);
        }
        Sample = sb.ToString();
    }

    public string ChatSystem(bool fix)
    {
        string s = Sample.Length > 0 ? string.Format(Prompts.ChatFmt, Sample) : Prompts.ChatPlain;
        return fix ? s + " " + Prompts.ChatFix : s;
    }

    /// <summary>Ответ целиком по-русски, хотя образец латинский?</summary>
    public bool ReplyWrongLanguage(string text)
    {
        bool sampLat = Sample.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
        if (!sampLat) return false;
        int lat = 0, cyr = 0;
        foreach (var c in text)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) lat++;
            else if (c >= 0x0400 && c <= 0x04FF) cyr++;
        }
        return lat < 8 && cyr > 5;
    }
}

/* ================================= API ================================= */
internal sealed class ApiResult
{
    public bool Ok;
    public string Text = "";
    public string Error = "";
    public int InTok, OutTok;
    public double Sec;
}

internal static class Api
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static string BuildBody(Cfg cfg, string system, IReadOnlyList<Msg> msgs, string? imgB64, int maxTokens)
    {
        var root = new JsonObject
        {
            ["model"] = cfg.Model,
        };
        if (cfg.ReasoningOff) root["reasoning"] = new JsonObject { ["enabled"] = false };
        root["max_tokens"] = maxTokens;
        var arr = new JsonArray();
        arr.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        for (int i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            bool withImg = imgB64 != null && i == msgs.Count - 1 && m.Role == "user";
            if (withImg)
            {
                var content = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = m.Text },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + imgB64 }
                    }
                };
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = content });
            }
            else
            {
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
            }
        }
        root["messages"] = arr;
        return root.ToJsonString();
    }

    public static ApiResult Call(Cfg cfg, string body)
    {
        var res = new ApiResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Host);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/ren3d");
            req.Headers.TryAddWithoutValidation("X-Title", "Podstrochnik");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = Http.Send(req);
            string raw = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            res.Sec = sw.Elapsed.TotalSeconds;
            if (!resp.IsSuccessStatusCode)
            {
                res.Error = $"HTTP {(int)resp.StatusCode}: {Trim(raw, 300)}";
                return res;
            }
            var node = JsonNode.Parse(raw);
            res.Text = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            res.InTok = node?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
            res.OutTok = node?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
            res.Ok = res.Text.Length > 0;
            if (!res.Ok) res.Error = "пустой ответ модели: " + Trim(raw, 200);
        }
        catch (Exception ex)
        {
            res.Sec = sw.Elapsed.TotalSeconds;
            res.Error = ex.Message;
        }
        return res;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public static string Money(int inTok, int outTok) =>
        "$" + ((inTok * 0.036 / 1e6) + (outTok * 0.684 / 1e6)).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);

    public static string Stats(double sec, int inTok, int outTok) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.00} с · токенов {1}→{2} · {3}", sec, inTok, outTok, Money(inTok, outTok));
}

/* ============================ захват области экрана ============================ */
internal sealed class ShotForm : Form
{
    private readonly Bitmap _frozen;
    private Point _p0, _p1;
    private bool _drag, _done;
    public Bitmap? Crop;

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    public ShotForm(Bitmap frozen, Rectangle bounds)
    {
        _frozen = frozen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;      // координаты выделения = физические пиксели
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Close(); return; }
        _drag = true; _p0 = _p1 = e.Location; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_drag) return;
        _p1 = e.Location; Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_drag) return;
        _drag = false; _p1 = e.Location;
        var r = SelectionRect();
        if (r.Width < 4 || r.Height < 4) { Close(); return; }
        Crop = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(Crop)) g.DrawImage(_frozen, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        _done = true;
        Close();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_frozen, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillRectangle(dim, new Rectangle(0, 0, Width, Height));
        var r = SelectionRect();
        if (r.Width > 0 && r.Height > 0)
        {
            g.DrawImage(_frozen, r, r, GraphicsUnit.Pixel);         // выделение — без затемнения
            using var pen = new Pen(Color.FromArgb(0x89, 0xb4, 0xfa), 2);
            g.DrawRectangle(pen, r);
            string label = $"{r.Width} × {r.Height}";
            using var font = new Font("Segoe UI", 10);
            var size = g.MeasureString(label, font);
            var box = new Rectangle(r.X, Math.Max(0, r.Y - (int)size.Height - 6), (int)size.Width + 12, (int)size.Height + 4);
            using var bg = new SolidBrush(Color.FromArgb(0x1e, 0x1e, 0x2e));
            g.FillRectangle(bg, box);
            g.DrawString(label, font, Brushes.White, box.X + 6, box.Y + 2);
        }
    }
    private Rectangle SelectionRect()
    {
        int x = Math.Min(_p0.X, _p1.X), y = Math.Min(_p0.Y, _p1.Y);
        int w = Math.Abs(_p0.X - _p1.X), h = Math.Abs(_p0.Y - _p1.Y);
        return new Rectangle(x, y, w, h);
    }
    public bool Completed => _done;
}

internal static class ScreenCapture
{
    public static Bitmap? Freeze(out Rectangle bounds)
    {
        bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static string? PngBase64(Bitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex) { Program.Log("png: ошибка — " + ex.Message); return null; }
    }
}

/* ============================ глобальные хоткеи ============================ */
internal sealed class Hotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int nCode, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private readonly HookProc _proc;          // держим ссылку, иначе сборщик мусора её съест
    private IntPtr _hook;
    private readonly bool[] _down = new bool[256];
    private long _lastCtrlC;
    private const int DoubleMs = 600;

    public Action? OnOpen;                    // двойной Ctrl+C
    public Action? OnShot;                    // Ctrl+Alt+S
    public Func<bool>? OnEsc;                 // Esc: true — клавиша «съедена»
    public uint OwnPid;

    public Hotkeys()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        Program.Log(_hook != IntPtr.Zero ? "хук клавиатуры: установлен" : "хук клавиатуры: НЕ УСТАНОВЛЕН");
    }

    private static int Mods() =>
        ((GetAsyncKeyState(0x11) & 0x8000) != 0 ? 1 : 0) |
        ((GetAsyncKeyState(0x12) & 0x8000) != 0 ? 2 : 0) |
        ((GetAsyncKeyState(0x10) & 0x8000) != 0 ? 4 : 0) |
        (((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0 ? 8 : 0);

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
        int vk = (int)k.vkCode;

        if (vk is >= 0 and < 256)
        {
            if (!down) { _down[vk] = false; return CallNextHookEx(_hook, nCode, wParam, lParam); }
            if (_down[vk]) return CallNextHookEx(_hook, nCode, wParam, lParam);
            _down[vk] = true;
        }
        try
        {
            int m = Mods() & 0xF;
            if (vk == ('S' & 0xFFFF) && m == 3)
            {
                Array.Clear(_down);
                OnShot?.Invoke();
            }
            else if (vk == ('C' & 0xFFFF) && m == 1)
            {
                long now = Environment.TickCount64;
                if (_lastCtrlC != 0 && now - _lastCtrlC <= DoubleMs)
                {
                    _lastCtrlC = 0;
                    Array.Clear(_down);
                    OnOpen?.Invoke();
                }
                else _lastCtrlC = now;
            }
            else if (vk == 0x1B)                                  // Esc
            {
                if (OnEsc?.Invoke() == true) return 1;
            }
        }
        catch (Exception ex) { Program.Log("хук: ошибка обработки — " + ex.Message); }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
