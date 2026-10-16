// Подстрочник — приложение: окно без рамки, трей, WebView2 с интерфейсом внутри exe,
// связка интерфейса с логикой (перевод, чат, контекст, хоткеи, захват экрана).
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Podstrochnik;

internal static class Program
{
    internal const string AppTitle = "Подстрочник";
    internal static string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Podstrochnik");
    internal static string UiDir => Path.Combine(DataDir, "ui");
    internal static string LogPath => Path.Combine(DataDir, "log.txt");

    private static readonly object LogLock = new();

    internal static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch { /* логи не должны ронять приложение */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);

        // Podstrochnik.exe --setkey <ключ> — сохранить ключ OpenRouter (для сборки без вшитого ключа)
        if (args.Length >= 2 && args[0] == "--setkey")
        {
            new Cfg().SaveKey(args[1]);
            return;
        }

        using var mutex = new Mutex(true, @"Local\PodstrochnikWebSingle", out bool first);
        if (!first)
        {
            IntPtr h = FindWindow(null, AppTitle);            // уже запущено — показать окно
            if (h != IntPtr.Zero) { ShowWindow(h, 9 /*SW_RESTORE*/); SetForegroundWindow(h); }
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        File.WriteAllText(LogPath, "", new UTF8Encoding(false));
        Log($"--- запуск, каталог данных {DataDir}");

        try { Application.Run(new Host()); }
        catch (Exception ex) { Log("КРИТИЧЕСКАЯ ОШИБКА: " + ex); throw; }
    }
}

/// <summary>Окно без видимой рамки, но с возможностью менять размер: Windows ведёт изменение
/// размера сама, если у окна есть WS_THICKFRAME и запрошен один из HT-зон.</summary>
internal sealed class FrameForm : Form
{
    private const int WS_THICKFRAME = 0x00040000, WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024, WM_SIZE = 0x0005;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>Ширина полосы по краям, за которую окно тянется (в точках).</summary>
    public int Edge = 6;
    /// <summary>Ниже этого размера окно не сжимается.</summary>
    public Size MinTrack = new(600, 700);
    /// <summary>Вызывается после изменения размера окна.</summary>
    public Action? OnSized;
    /// <summary>Диагностика: последняя зона, которую вернул хит-тест.</summary>
    public Action<string>? LogHit;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_THICKFRAME;      // без этого Windows не станет менять размер
            return cp;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public Point Reserved, MaxSize, MaxPos, MinTrack, MaxTrack;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                m.Result = IntPtr.Zero;     // клиентская область = всё окно, рамки не видно
                return;

            case WM_NCHITTEST:
            {
                // Полоса по краям окна: сюда не достаёт веб-интерфейс, поэтому Windows
                // сама понимает, что за неё нужно тянуть, и меняет размер штатно.
                var p = PointToClient(new Point(
                    (short)((long)m.LParam & 0xFFFF),
                    (short)(((long)m.LParam >> 16) & 0xFFFF)));
                int w = ClientSize.Width, h = ClientSize.Height, e = Edge;
                bool left = p.X < e, right = p.X >= w - e, top = p.Y < e, bottom = p.Y >= h - e;
                int zone = 0;
                if (left && top) zone = HTTOPLEFT;
                else if (right && top) zone = HTTOPRIGHT;
                else if (left && bottom) zone = HTBOTTOMLEFT;
                else if (right && bottom) zone = HTBOTTOMRIGHT;
                else if (left) zone = HTLEFT;
                else if (right) zone = HTRIGHT;
                else if (top) zone = HTTOP;
                else if (bottom) zone = HTBOTTOM;
                if (zone != 0)
                {
                    LogHit?.Invoke($"хит-тест: зона изменения размера {zone} в точке {p.X},{p.Y}");
                    m.Result = (IntPtr)zone;
                    return;
                }
                break;
            }

            case WM_GETMINMAXINFO:
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                mmi.MinTrack = new Point(MinTrack.Width, MinTrack.Height);
                Marshal.StructureToPtr(mmi, m.LParam, false);
                return;
            }

            case WM_SIZE:
                base.WndProc(ref m);
                OnSized?.Invoke();
                return;
        }
        base.WndProc(ref m);
    }
}

internal sealed class ChatMsg
{
    public string Role = "user";
    public string Text = "";
    public DateTime At = DateTime.Now;
}

internal sealed class Host : ApplicationContext
{
    private const int JobTranslate = 0, JobChat = 1, JobVision = 2, JobOcr = 3;

    private readonly FrameForm _form = new();
    private WebView2? _web;
    private NotifyIcon? _tray;
    private Icon? _icoIdle, _icoBusy, _icoFail;
    private double _scale = 1.0;
    private bool _exiting, _busy;

    private readonly Cfg _cfg = Cfg.Load();
    private readonly Session _sess = new();
    private readonly List<ChatMsg> _chat = new();
    private Hotkeys? _hook;
    private ShotForm? _shot;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hrgn, bool redraw);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    internal Host()
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        _scale = Math.Clamp(work.Height * 0.92 / 960.0, 1.0, 1.75);
        int w = (int)(720 * _scale), h = (int)(960 * _scale);

        _form.FormBorderStyle = FormBorderStyle.None;
        _form.StartPosition = FormStartPosition.Manual;
        _form.BackColor = Color.FromArgb(0x1e, 0x1e, 0x2e);
        _form.Text = Program.AppTitle;
        _form.ShowInTaskbar = true;
        // ниже этого размера элементы вёрстки перестают помещаться
        _form.MinimumSize = new Size((int)(600 * _scale), (int)(700 * _scale));
        _form.Edge = (int)Math.Round(8 * _scale);
        _form.MinTrack = _form.MinimumSize;
        _form.LogHit = (msg) => Program.Log(msg);
        _form.OnSized = () =>
        {
            LayoutWeb();
            if (_regionCorners) RoundCorners();
            Program.Log($"размер окна: {_form.ClientSize.Width}x{_form.ClientSize.Height}");
        };
        _form.ClientSize = new Size(w, h);
        _form.Location = new Point(work.Left + (work.Width - w) / 2, work.Top + Math.Max(8, (work.Height - h) / 2));
        _form.FormClosing += OnClosing;
        _form.Shown += async (_, _) => await InitWebAsync();
        Program.Log($"окно {w}x{h}, масштаб интерфейса {_scale:0.00}");

        SetupTray();
        SetupHook();
        _form.Show();
    }

    /* ------------------------------ трей ------------------------------ */
    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать окно", null, (_, _) => ShowWindowUi());
        menu.Items.Add("Перевести из буфера", null, (_, _) => TranslateFromClipboard());
        menu.Items.Add("Сбросить контекст", null, (_, _) => ResetState(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Text = Program.AppTitle + " — перевод выделенного текста",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icoIdle = LoadIcon("app/icon_idle.ico");
        _icoBusy = LoadIcon("app/icon_busy.ico");
        _icoFail = LoadIcon("app/icon_error.ico");
        _tray.Icon = _icoIdle ?? SystemIcons.Application;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindowUi(); };   // один клик
        _tray.DoubleClick += (_, _) => ShowWindowUi();
        Program.Log("трей: добавлен (левый клик — показать окно)");
    }

    private static Icon? LoadIcon(string res)
    {
        try
        {
            using var s = typeof(Host).Assembly.GetManifestResourceStream(res);
            return s != null ? new Icon(s) : null;
        }
        catch (Exception ex) { Program.Log($"трей: иконка {res} не загрузилась — {ex.Message}"); return null; }
    }

    private void TrayIcon(int which)
    {
        var icon = which switch { 1 => _icoBusy, 2 => _icoFail, _ => _icoIdle };
        if (_tray != null && icon != null) _tray.Icon = icon;
    }

    private void ShowWindowUi()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
        Program.Log("окно показано");
    }

    private void HideWindowUi()
    {
        _form.Hide();
        Program.Log("окно скрыто в трей");
    }

    private void ExitApp()
    {
        Program.Log("--- выход");
        _exiting = true;
        _hook?.Dispose();
        if (_tray != null) _tray.Visible = false;
        _web?.Dispose();
        _form.Close();
        ExitThread();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting) return;
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall)
        {
            e.Cancel = true;              // крестик и Alt+F4 — в трей
            HideWindowUi();
        }
    }

    /* --------------------------- хоткеи --------------------------- */
    private void SetupHook()
    {
        _hook = new Hotkeys
        {
            OwnPid = (uint)Environment.ProcessId,
            OnOpen = () => Post(() => { ShowWindowUi(); TranslateFromClipboard(); }),
            OnShot = () => Post(() => StartShot(false)),
            OnEsc = () =>
            {
                if (_shot != null && _shot.Visible) { Post(() => _shot?.Close()); return true; }
                var h = GetForegroundWindow();
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) { Post(HideWindowUi); return true; }
                return false;
            }
        };
    }

    private void Post(Action a)
    {
        try { _form.BeginInvoke(a); } catch { /* окно уже закрыто */ }
    }

    /* --------------------------- интерфейс --------------------------- */
    private static void ExtractUi()
    {
        var asm = typeof(Host).Assembly;
        Directory.CreateDirectory(Program.UiDir);
        int n = 0;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("ui/", StringComparison.Ordinal)) continue;
            string rel = name[3..].Replace('/', Path.DirectorySeparatorChar);
            string dst = Path.Combine(Program.UiDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var outFs = File.Create(dst);
            src.CopyTo(outFs);
            n++;
        }
        Program.Log($"интерфейс распакован: {n} файлов");
    }

    private async Task InitWebAsync()
    {
        try
        {
            ExtractUi();
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDir, "webview"));
            _web = new WebView2
            {
                Dock = DockStyle.None,        // окно оставляет полосу по краям для изменения размера
                DefaultBackgroundColor = Color.FromArgb(0x1e, 0x1e, 0x2e)
            };
            _form.Controls.Add(_web);
            LayoutWeb();
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.SetVirtualHostNameToFolderMapping("podstrochnik.local", Program.UiDir,
                CoreWebView2HostResourceAccessKind.Allow);
            _web.ZoomFactor = _scale;
            core.Navigate("https://podstrochnik.local/index.html");
            Program.Log("WebView2 готов, интерфейс загружен");
        }
        catch (Exception ex)
        {
            Program.Log("ОШИБКА WebView2: " + ex);
            MessageBox.Show(
                "Не удалось запустить компонент WebView2 (движок Edge).\n\n" +
                "Обычно он уже есть в Windows 10/11 вместе с Edge. Если нет — установите " +
                "\"WebView2 Runtime\" с сайта Microsoft и запустите приложение снова.\n\n" + ex.Message,
                Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool _regionCorners;             // на Windows 10 скругление делается областью окна

    /// <summary>Веб-часть занимает окно минус полоса по краям (за неё тянется размер).</summary>
    private void LayoutWeb()
    {
        if (_web == null) return;
        int m = _form.Edge;
        var c = _form.ClientSize;
        _web.Bounds = new Rectangle(m, m, Math.Max(1, c.Width - 2 * m), Math.Max(1, c.Height - 2 * m));
    }

    private void ApplyRoundedCorners()
    {
        int noBorder = unchecked((int)0xFFFFFFFE);      // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_form.Handle, 34 /*DWMWA_BORDER_COLOR*/, ref noBorder, sizeof(int));
        int pref = DWMWCP_ROUND;
        _regionCorners = DwmSetWindowAttribute(_form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) != 0;
        if (_regionCorners)
        {
            Program.Log("скругление углов: через область окна (Win10)");
            RoundCorners();
        }
    }

    private void RoundCorners()
    {
        int r = (int)(14 * _scale);
        SetWindowRgn(_form.Handle, CreateRoundRectRgn(0, 0, _form.Width + 1, _form.Height + 1, r, r), true);
    }

    /* ---------------------------- мост ---------------------------- */
    private void Send(JsonObject msg) => _web?.CoreWebView2?.PostWebMessageAsJson(msg.ToJsonString());

    private void SendState()
    {
        var chatArr = new JsonArray();
        foreach (var m in _chat)
            chatArr.Add(new JsonObject { ["role"] = m.Role, ["text"] = m.Text, ["at"] = m.At.ToString("o") });

        var state = new JsonObject
        {
            ["ctxTokens"] = _sess.Tokens,
            ["ctxLimit"] = _cfg.CtxLimit,
            ["autostart"] = AutostartOn(),
            ["chat"] = chatArr,
            ["busy"] = _busy,
        };
        if (_sess.TrText != null)
            state["translation"] = new JsonObject
            {
                ["text"] = _sess.TrText,
                ["stats"] = _sess.TrStats,
                ["at"] = _sess.TrAt?.ToString("o")
            };
        Send(new JsonObject { ["t"] = "state", ["state"] = state });
    }

    private void SetStatus(string text, string kind = "ok") =>
        Send(new JsonObject { ["t"] = "status", ["text"] = text, ["kind"] = kind });

    private void SendText(string what, string text) =>
        Send(new JsonObject { ["t"] = what, ["text"] = text });

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = e.TryGetWebMessageAsString();
        JsonNode? node;
        try { node = JsonNode.Parse(raw); } catch { Program.Log("мост: не разобрал сообщение " + raw); return; }
        string cmd = node?["cmd"]?.GetValue<string>() ?? "";
        Program.Log("мост: " + raw);

        switch (cmd)
        {
            case "ready":
                ApplyRoundedCorners();
                SendState();
                if (_cfg.ApiKey.Length == 0)
                    SetStatus("Нет ключа OpenRouter — добавь его в config.ini или запусти --setkey", "err");
                break;
            case "win":
                switch (node?["act"]?.GetValue<string>())
                {
                    case "min" or "close": HideWindowUi(); return;
                    case "max":
                        _form.WindowState = _form.WindowState == FormWindowState.Maximized
                            ? FormWindowState.Normal : FormWindowState.Maximized;
                        return;
                }
                break;
            case "drag":
                ReleaseCapture();
                SendMessage(_form.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                break;
            // изменение размера делает сама Windows по полосе у краёв окна (см. FrameForm)
            case "hide": HideWindowUi(); break;
            case "autostart":
                SetAutostart(!AutostartOn());
                SetStatus(AutostartOn() ? "Автозапуск включён" : "Автозапуск выключен");
                SendState();
                break;
            case "reset": ResetState(true); break;
            case "copy":
                if (!string.IsNullOrEmpty(_sess.TrText))
                {
                    try { Clipboard.SetText(_sess.TrText); SetStatus("Перевод скопирован в буфер"); }
                    catch (Exception ex) { SetStatus("Буфер обмена недоступен: " + ex.Message, "err"); }
                }
                else SetStatus("Нечего копировать", "warn");
                break;
            case "clipRead":
                try { SendText("input", Clipboard.ContainsText() ? Clipboard.GetText() : ""); }
                catch (Exception ex) { SetStatus("Буфер обмена недоступен: " + ex.Message, "err"); }
                break;
            case "translateSource":
                TranslateText(node?["text"]?.GetValue<string>() ?? "");
                break;
            case "translateClip": TranslateFromClipboard(); break;
            case "chat": SendChat(node?["text"]?.GetValue<string>() ?? ""); break;
            case "screenToInput": StartShot(true); break;
            default: Program.Log("мост: неизвестная команда " + cmd); break;
        }
        await Task.CompletedTask;
    }

    /* --------------------------- логика --------------------------- */
    private void ResetState(bool notify)
    {
        _sess.Msgs.Clear();
        _sess.Tokens = 0;
        _sess.Sample = "";
        _sess.TrText = _sess.TrStats = null;
        _sess.TrAt = null;
        _chat.Clear();
        SendText("src", "");
        SendState();
        if (notify) SetStatus("Контекст и панели очищены");
        Program.Log("контекст сброшен");
    }

    private void TranslateFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) { SetStatus("Нечего переводить: в буфере нет текста", "warn"); return; }
            TranslateText(Clipboard.GetText());
        }
        catch (Exception ex) { SetStatus("Буфер обмена недоступен: " + ex.Message, "err"); }
    }

    private void TranslateText(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) { SetStatus("Пусто — нечего переводить", "warn"); return; }
        SendText("src", text);

        int cyr = 0, lat = 0;
        foreach (var c in text)
        {
            if (c is >= (char)0x0400 and <= (char)0x04FF) cyr++;
            else if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')) lat++;
        }
        if (cyr > lat)
        {
            _sess.TrText = text;
            _sess.TrAt = DateTime.Now;
            _sess.TrStats = "Текст уже на русском — оставлен как есть";
            SendState();
            SetStatus("Текст уже на русском");
            Program.Log("translate: уже русский, пропускаю");
            return;
        }
        _sess.SetSample(text);
        StartJob(JobTranslate, Prompts.Translate, text, null, 1500, addMsg: true);
    }

    private void SendChat(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        _chat.Add(new ChatMsg { Role = "user", Text = text, At = DateTime.Now });
        SendState();
        StartJob(JobChat, _sess.ChatSystem(false), text, null, 1500, addMsg: true);
    }

    private void StartShot(bool forInput)
    {
        if (_busy) { SetStatus("Уже идёт запрос — подожди секунду", "warn"); return; }
        var frozen = ScreenCapture.Freeze(out var bounds);
        if (frozen == null) { SetStatus("Не удалось снять экран", "err"); return; }
        bool wasVisible = _form.Visible;
        if (wasVisible) _form.Hide();
        Bitmap? crop = null;
        try
        {
            var shot = new ShotForm(frozen, bounds);
            _shot = shot;
            shot.ShowDialog();
            crop = shot.Crop;
            _shot = null;
        }
        finally
        {
            if (wasVisible) ShowWindowUi();
            frozen.Dispose();
        }
        if (crop == null) { SetStatus("Выделение отменено", "warn"); Program.Log("shot: отменено"); return; }

        int cw = crop.Width, ch = crop.Height;
        string? b64 = ScreenCapture.PngBase64(crop);
        crop.Dispose();
        if (b64 == null) { SetStatus("Не удалось закодировать картинку", "err"); return; }
        Program.Log($"shot: png {b64.Length} символов ({cw}x{ch})");

        if (forInput)
            StartJob(JobOcr, Prompts.Ocr, "[screenshot — прочитай текст на картинке]", b64, 1500, addMsg: false);
        else
            StartJob(JobVision, Prompts.Vision,
                     "[screenshot — прочитай текст на картинке и переведи его на русский]", b64, 2000, addMsg: true);
    }

    private void StartJob(int kind, string system, string userText, string? imgB64, int maxTokens, bool addMsg)
    {
        if (_cfg.ApiKey.Length == 0)
        {
            SetStatus("Нет ключа OpenRouter — добавь его в config.ini или запусти --setkey", "err");
            return;
        }
        if (addMsg && userText.Length > 0 && _sess.Tokens + Session.EstTokens(userText) > _cfg.CtxLimit)
        {
            SetStatus($"Контекст полон ({_sess.Tokens} из {_cfg.CtxLimit}) — нажми «Сбросить контекст»", "err");
            Program.Log($"blocked: контекст {_sess.Tokens}/{_cfg.CtxLimit}");
            return;
        }
        if (_busy) { SetStatus("Уже идёт запрос — подожди секунду", "warn"); return; }

        if (addMsg) _sess.Msgs.Add(new Msg { Role = "user", Text = userText });
        string body = Api.BuildBody(_cfg, system, _sess.Msgs, imgB64, maxTokens);
        _busy = true;
        TrayIcon(1);
        SetStatus("Отправлено, жду ответ…");
        SendState();
        Program.Log($"job {kind} sent, {body.Length} байт");

        Task.Run(() =>
        {
            var res = Api.Call(_cfg, body);
            Post(() => JobDone(kind, res, addMsg));
        });
    }

    private void JobDone(int kind, ApiResult res, bool addMsg)
    {
        _busy = false;
        TrayIcon(0);

        if (!res.Ok)
        {
            TrayIcon(2);
            SetStatus("Ошибка: " + res.Error, "err");
            Program.Log("job error: " + res.Error);
            SendState();
            return;
        }

        if (kind == JobChat && addMsg && _sess.ReplyWrongLanguage(res.Text))
        {
            Program.Log("chat: ответ целиком по-русски — повторяю запрос");
            SetStatus("Модель ответила не на том языке — повторяю…", "warn");
            StartJob(JobChat, _sess.ChatSystem(true), "", null, 1500, addMsg: false);
            return;
        }

        _sess.Tokens = res.InTok + res.OutTok;
        _sess.Msgs.Add(new Msg { Role = "assistant", Text = res.Text });

        if (kind == JobChat)
        {
            _chat.Add(new ChatMsg { Role = "bot", Text = res.Text, At = DateTime.Now });
            SetStatus(Api.Stats(res.Sec, res.InTok, res.OutTok));
        }
        else if (kind == JobOcr)
        {
            SendText("input", res.Text);
            SetStatus($"Текст распознан: {res.Text.Length} символов");
        }
        else
        {
            string text = res.Text;
            string stats = Api.Stats(res.Sec, res.InTok, res.OutTok);
            int sep = text.IndexOf("\n---", StringComparison.Ordinal);
            if (sep >= 0)
            {
                string srcPart = text[..sep];
                string dstPart = text[(sep + 4)..].TrimStart('-', '\n', '\r', ' ');
                SendText("src", srcPart);
                _sess.TrText = dstPart;
                if (kind == JobVision) _sess.SetSample(srcPart);
            }
            else
            {
                _sess.TrText = text;
            }
            _sess.TrAt = DateTime.Now;
            _sess.TrStats = stats;
            SetStatus(stats);
        }

        SendState();
        Program.Log($"job {kind} ok: {res.Sec:0.00}s, {res.InTok}->{res.OutTok} токенов");
    }

    /* -------------------------- автозапуск -------------------------- */
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Podstrochnik";

    private static bool AutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null;
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) { Program.Log("автозапуск: ключ реестра недоступен"); return; }
        if (on)
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            k.SetValue(RunValue, "\"" + exe + "\"");
            Program.Log("автозапуск включён: " + exe);
        }
        else
        {
            k.DeleteValue(RunValue, false);
            Program.Log("автозапуск выключен");
        }
    }
}
