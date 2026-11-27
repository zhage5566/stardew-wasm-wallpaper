using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Launcher
{
    private const string GamePage = "index.html";
#if DIRECT_WALLPAPER
    internal static readonly bool Direct = true;
#else
    internal static readonly bool Direct = false;
#endif
    internal static readonly string Here = AppDomain.CurrentDomain.BaseDirectory;
    internal static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StardewWasmWallpaper");

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--probe-desktop"))
        {
            try
            {
                using (var probe = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false })
                {
                    Rectangle monitor = Screen.PrimaryScreen.Bounds;
                    string method = DesktopLayer.Attach(probe.Handle, monitor);
                    if (!DesktopLayer.IsAttached(probe.Handle) || DesktopLayer.OuterBounds(probe.Handle) != monitor)
                        throw new InvalidOperationException("Desktop parent or geometry verification failed.");
                    DesktopLayer.Float(probe.Handle, monitor, false, false);
                    if (DesktopLayer.IsAttached(probe.Handle) || DesktopLayer.OuterBounds(probe.Handle) != monitor)
                        throw new InvalidOperationException("Fullscreen geometry verification failed.");
                    DesktopLayer.Float(probe.Handle, new Rectangle(100, 100, 640, 400), true, false);
                    if (DesktopLayer.IsAttached(probe.Handle)) throw new InvalidOperationException("Preview detach failed.");
                    Log("Desktop geometry probe passed: " + method + "; full=" + monitor + "; work=" + Screen.PrimaryScreen.WorkingArea);
                    return 0;
                }
            }
            catch (Exception error) { Log("Desktop probe failed: " + error); return 1; }
        }

        bool first;
        using (var mutex = new Mutex(true, @"Local\StardewWasmWallpaper", out first))
        {
            if (!first)
            {
                if (!Direct) MessageBox.Show("星露谷桌面版已在运行。请使用系统托盘图标。", "星露谷桌面版");
                Log("Another instance already owns the desktop and game profile.");
                return 0;
            }
            string root = FindGame(args);
            if (root == null)
            {
                if (Direct) Log("Bundled game missing. Fully extract the archive before starting.");
                else MessageBox.Show("未找到网页移植文件。请完整解压，再双击启动。", "星露谷桌面版",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            bool engine = Direct && (args.Contains("--wallpaper-engine") || String.Equals(
                Path.GetFileNameWithoutExtension(Application.ExecutablePath), "StardewWallpaperEngine",
                StringComparison.OrdinalIgnoreCase));
            using (var form = new WallpaperForm(root, args.Contains("--wallpaper"), Direct, engine))
                Application.Run(form);
            mutex.ReleaseMutex();
        }
        return 0;
    }

    private static string FindGame(string[] args)
    {
        int flag = Array.IndexOf(args, "--game");
        if (flag >= 0 && flag + 1 < args.Length)
        {
            string requested = Path.GetFullPath(args[flag + 1]);
            return File.Exists(Path.Combine(requested, GamePage)) ? requested : null;
        }
        foreach (string candidate in new[] { Path.Combine(Here, "game"), Path.Combine(Here, "stardew-wasm"),
            Path.GetFullPath(Path.Combine(Here, @"..\..\stardew-wasm")) })
        {
            if (File.Exists(Path.Combine(candidate, GamePage)) &&
                File.Exists(Path.Combine(candidate, @"_framework\dotnet.js")) &&
                File.Exists(Path.Combine(candidate, "Content.tar.count"))) return candidate;
        }
        return null;
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Data);
            File.AppendAllText(Path.Combine(Data, "desktop.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class WallpaperForm : Form
{
    private readonly WebView2 web;
    private readonly FlowLayoutPanel toolbar;
    private readonly Label status;
    private readonly NotifyIcon tray;
    private readonly Timer desktopCheck;
    private readonly LocalGameContent content;
    private readonly DesktopInput desktopInput;
    private readonly CheckBox dualLayer;
    private readonly ToolStripMenuItem dualTray;
    private readonly ComboBox monitors;
    private Rectangle appliedScreen;
    private double viewportWidth, viewportHeight;
    private bool gameReady;
    private Task inputQueue = Task.FromResult(0);
    private readonly bool wallpaperOnStart;
    private readonly bool direct;
    private readonly bool hostedByEngine;
    private readonly WallpaperSettings settings;
    private string selectedDisplay;
    private bool favorite;
    private bool startupScheduled, desktopAttached;
    private Mode mode;
    private enum Mode { Preview, Wallpaper, Play }

    internal WallpaperForm(string gameRoot, bool wallpaperOnStart, bool direct = false, bool hostedByEngine = false)
    {
        this.wallpaperOnStart = wallpaperOnStart;
        this.direct = direct;
        this.hostedByEngine = hostedByEngine;
        settings = new WallpaperSettings(Launcher.Data);
        if (direct && !hostedByEngine)
        {
            try { favorite = settings.TryGetFavorite(out selectedDisplay); }
            catch (Exception error) { Launcher.Log("Wallpaper preference could not be read: " + error.Message); }
        }
        content = new LocalGameContent(gameRoot, direct);
        Text = "星露谷 WASM 桌面版";
        BackColor = Color.FromArgb(17, 28, 34);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1040, 680);
        MinimumSize = new Size(530, 340);
        Font = new Font("Microsoft YaHei UI", 10);
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        toolbar = new FlowLayoutPanel {
            Dock = DockStyle.Top, Height = 45, Padding = new Padding(8, 6, 0, 1),
            BackColor = Color.FromArgb(238, 245, 234), WrapContents = false
        };
        toolbar.Controls.Add(Button("设为壁纸", ToWallpaper));
        toolbar.Controls.Add(Button("全屏试玩", ToPlay));
        dualLayer = new CheckBox { AutoSize = true, Text = "双层点击", Margin = new Padding(7, 7, 5, 0) };
        toolbar.Controls.Add(dualLayer);
        monitors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(6, 3, 2, 0), AccessibleName = "显示器" };
        RefreshMonitors();
        toolbar.Controls.Add(monitors);
        status = new Label { AutoSize = true, Margin = new Padding(9, 8, 0, 0), Text = "正在加载网页移植…  Ctrl+Alt+F8 切换" };
        toolbar.Controls.Add(status);
        Controls.Add(web);
        Controls.Add(toolbar);

        var menu = new ContextMenuStrip();
        if (!hostedByEngine) menu.Items.Add("设为桌面壁纸", null, delegate { ToWallpaper(); });
        if (direct)
        {
            ShowInTaskbar = hostedByEngine;
            FormBorderStyle = FormBorderStyle.None;
            toolbar.Visible = false;
            mode = Mode.Wallpaper;
        }
        if (direct && !hostedByEngine)
        {
            var displayMenu = new ToolStripMenuItem("选择显示器");
            displayMenu.DropDownOpening += delegate
            {
                RefreshMonitors();
                displayMenu.DropDownItems.Clear();
                var automatic = new ToolStripMenuItem("自动跟随主显示器") { Checked = selectedDisplay == null };
                automatic.Click += delegate { SelectDisplay(null); };
                displayMenu.DropDownItems.Add(automatic);
                foreach (MonitorChoice choice in monitors.Items)
                {
                    var item = new ToolStripMenuItem(choice.ToString()) {
                        Checked = choice.Device == selectedDisplay
                    };
                    item.Click += delegate { SelectDisplay(choice.Device); };
                    displayMenu.DropDownItems.Add(item);
                }
            };
            menu.Items.Add(displayMenu);
            var favoriteItem = new ToolStripMenuItem("设为常用壁纸") { Checked = favorite };
            favoriteItem.Click += delegate
            {
                try
                {
                    settings.SetFavorite(!favorite, selectedDisplay);
                    favoriteItem.Checked = favorite = !favorite;
                }
                catch (Exception error) { SettingError(error); }
            };
            menu.Items.Add(favoriteItem);
            var startupItem = new ToolStripMenuItem("开机自启动");
            menu.Opening += delegate
            {
                try { startupItem.Checked = settings.StartsWithWindows(Application.ExecutablePath); }
                catch (Exception error) { Launcher.Log("Startup status could not be read: " + error.Message); }
            };
            startupItem.Click += delegate
            {
                try
                {
                    settings.SetStartup(!startupItem.Checked, Application.ExecutablePath);
                    startupItem.Checked = !startupItem.Checked;
                }
                catch (Exception error) { SettingError(error); }
            };
            menu.Items.Add(startupItem);
        }
        else if (!direct)
        {
            menu.Items.Add("切换试玩／壁纸   Ctrl+Alt+F8", null, delegate { Toggle(); });
            menu.Items.Add("窗口预览   Ctrl+Alt+F9", null, delegate { ToPreview(); });
        }
        dualTray = new ToolStripMenuItem(direct ? "游戏控制（键盘随鼠标）   Ctrl+Alt+F10" :
            "双层点击：图标给 Windows，空白给游戏") { CheckOnClick = true };
        menu.Items.Add(dualTray);
        dualLayer.CheckedChanged += delegate { dualTray.Checked = dualLayer.Checked; UpdateDesktopInput(); };
        dualTray.CheckedChanged += delegate { dualLayer.Checked = dualTray.Checked; };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关于 · zhouyang", null, delegate
        {
            tray.ShowBalloonTip(5000, "星露谷纯壁纸版", "桌面版制作：zhouyang\r\n原作：ConcernedApe", ToolTipIcon.Info);
        });
        menu.Items.Add("退出", null, delegate { Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "星露谷壁纸 · zhouyang", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { Toggle(); };
        desktopInput = new DesktopInput(this, QueueDesktopPointer, delegate(Point point) { return appliedScreen.Contains(point); },
            direct ? (Action<DesktopKey>)QueueDesktopKey : null);
        if (direct) dualLayer.Checked = true;
        desktopCheck = new Timer { Interval = 750 };
        desktopCheck.Tick += delegate { RefreshWallpaperGeometry(); };
    }

    private void SelectDisplay(string device)
    {
        if (favorite)
        {
            try { settings.SetFavorite(true, device); }
            catch (Exception error) { SettingError(error); }
        }
        selectedDisplay = device;
        ToWallpaper();
    }

    private void SettingError(Exception error)
    {
        Launcher.Log("Desktop setting failed: " + error);
        tray.ShowBalloonTip(6000, "星露谷壁纸", error.Message, ToolTipIcon.Error);
    }

    private void RefreshWallpaperGeometry()
    {
        if (!IsHandleCreated || IsDisposed || Disposing) return;
        if (hostedByEngine)
        {
            try { appliedScreen = DesktopLayer.FitExternalWallpaper(Handle); }
            catch (Win32Exception error) { Launcher.Log("Engine wallpaper resize: " + error.Message); }
            return;
        }
        try
        {
            Rectangle current = SelectedScreen().Bounds;
            if (mode == Mode.Wallpaper && ((direct && !desktopAttached) ||
                !DesktopLayer.IsAttached(Handle) || appliedScreen != current || DesktopLayer.OuterBounds(Handle) != current))
                ToWallpaper();
            else if (mode == Mode.Play && appliedScreen != current)
                ToPlay();
        }
        catch (Win32Exception error) { Launcher.Log("Desktop geometry changed during inspection: " + error.Message); }
    }

    protected override bool ShowWithoutActivation { get { return direct || base.ShowWithoutActivation; } }

    protected override void SetVisibleCore(bool value)
    {
        if (value && direct && !hostedByEngine && !desktopAttached)
        {
            if (!startupScheduled)
            {
                startupScheduled = true;
                if (!IsHandleCreated) CreateHandle();
                BeginInvoke((Action)delegate
                {
                    desktopCheck.Start();
                    ToWallpaper();
                });
            }
            return;
        }
        base.SetVisibleCore(value);
    }

    private static Button Button(string text, Action action)
    {
        var control = new Button { Text = text, AutoSize = true, MinimumSize = new Size(83, 30), Margin = new Padding(0, 0, 4, 0) };
        control.Click += delegate { action(); };
        return control;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        const uint modifiers = 0x4000 | 0x0002 | 0x0001;
        if (!DesktopLayer.RegisterHotKey(Handle, 1, modifiers, (uint)(direct ? Keys.F10 : Keys.F8)) ||
            (!direct && !DesktopLayer.RegisterHotKey(Handle, 2, modifiers, (uint)Keys.F9)))
            Launcher.Log("A shortcut is already in use; the tray menu remains available.");
        desktopCheck.Start();
        if (hostedByEngine) RefreshWallpaperGeometry();
        try
        {
            var runtime = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Launcher.Data, "WebView2"));
            if (IsDisposed || Disposing) return;
            await web.EnsureCoreWebView2Async(runtime);
            if (IsDisposed || Disposing) return;
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += delegate (object sender, CoreWebView2NewWindowRequestedEventArgs args) { args.Handled = true; };
            core.NavigationStarting += delegate (object sender, CoreWebView2NavigationStartingEventArgs args)
            {
                if (!LocalGameContent.IsOrigin(args.Uri)) args.Cancel = true;
            };
            core.WebMessageReceived += delegate (object sender, CoreWebView2WebMessageReceivedEventArgs args)
            {
                if (!LocalGameContent.IsOrigin(args.Source)) return;
                string message;
                try { message = args.TryGetWebMessageAsString(); }
                catch (ArgumentException) { return; }
                if (message.StartsWith("viewport:", StringComparison.Ordinal))
                {
                    string[] parts = message.Split(':');
                    if (parts.Length == 3)
                    {
                        double width, height;
                        if (Double.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out width) &&
                            Double.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out height) &&
                            width > 0 && height > 0)
                        {
                            viewportWidth = width;
                            viewportHeight = height;
                            Launcher.Log("Browser viewport: " + width + " x " + height);
                        }
                    }
                }
                else if (message == "game-loading-complete")
                {
                    gameReady = true;
                    status.Text = "游戏已就绪";
                    Launcher.Log("Web runtime loaded the game content.");
                    UpdateDesktopInput();
                }
                else if (message.StartsWith("port-error:", StringComparison.Ordinal))
                {
                    status.Text = "游戏加载失败。详细信息见日志。";
                    Launcher.Log(message.Length > 500 ? message.Substring(0, 500) : message);
                }
            };
            content.Install(core, runtime);
            if (direct)
                await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
            string directSetup = direct ?
                "const style = document.createElement('style'); style.textContent = " +
                "'html, body, canvas { cursor: default !important; } #music-choice { display:none !important; }';" +
                "document.head.appendChild(style); const auto = setInterval(() => {" +
                "if (loading.classList.contains('hidden')) clearInterval(auto);" +
                "else if (yes && typeof yes.onclick === 'function') { clearInterval(auto); yes.click(); } }, 100);" : "";
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "(() => { const send = m => window.chrome.webview.postMessage(m);" +
                "const size = () => send('viewport:' + innerWidth + ':' + innerHeight);" +
                "addEventListener('resize', size); addEventListener('DOMContentLoaded', size);" +
                "addEventListener('unhandledrejection', e => {" +
                "const str = String(e.reason && e.reason.message || e.reason); if(str !== 'unwind') send('port-error: ' + str); });" +
                "addEventListener('error', e => { if (e.message) send('port-error: ' + e.message); });" +
                "addEventListener('DOMContentLoaded', () => { const loading = document.getElementById('loading');" +
                "const no = document.getElementById('btn-no-music'), yes = document.getElementById('btn-with-music');" +
                "if (no) no.textContent = '快速开始（无背景音乐）'; if (yes) yes.textContent = '开始游戏（含背景音乐）';" +
                "if (!loading) return;" + directSetup + "const watch = new MutationObserver(() => {" +
                "if (loading.classList.contains('hidden')) { watch.disconnect(); send('game-loading-complete'); } });" +
                "watch.observe(loading, {attributes:true, attributeFilter:['class']}); }); })();");
            core.Navigate(LocalGameContent.Origin + "index.html");
            if (wallpaperOnStart && !direct) ToWallpaper();
            else if (!direct) web.Focus();
        }
        catch (Exception error)
        {
            if (IsDisposed || Disposing) return;
            status.Text = "无法启动 WebView2。请查看日志或安装 WebView2 Runtime。";
            Launcher.Log("WebView2 startup failed: " + error);
            if (direct) tray.ShowBalloonTip(6000, "星露谷壁纸", "启动失败。请检查 WebView2 Runtime 和应用日志。", ToolTipIcon.Error);
        }
    }

    private void Toggle()
    {
        if (direct) dualLayer.Checked = !dualLayer.Checked;
        else if (mode == Mode.Play) ToWallpaper();
        else ToPlay();
    }

    private void ToWallpaper()
    {
        if (IsDisposed || Disposing || hostedByEngine) return;
        try
        {
            toolbar.Visible = false;
            Rectangle screen = SelectedScreen().Bounds;
            string layer = DesktopLayer.Attach(Handle, screen, !direct || Visible);
            appliedScreen = screen;
            mode = Mode.Wallpaper;
            if (direct)
            {
                desktopAttached = true;
                if (!Visible) Show();
            }
            UpdateDesktopInput();
            Launcher.Log("Wallpaper attached: " + layer + "; screen=" + screen);
        }
        catch (Exception error)
        {
            Launcher.Log("Desktop attachment failed: " + error);
            if (direct)
            {
                desktopAttached = false;
                desktopInput.Stop();
                if (Visible) Hide();
            }
            else
            {
                ToPreview();
                status.Text = "桌面层暂不可用，已保留窗口预览。";
            }
        }
    }

    private void ToPlay()
    {
        if (IsDisposed || Disposing) return;
        desktopInput.Stop();
        try
        {
            toolbar.Visible = false;
            Rectangle screen = SelectedScreen().Bounds;
            DesktopLayer.Float(Handle, screen, false, true);
            appliedScreen = screen;
            mode = Mode.Play;
            web.Focus();
            Launcher.Log("Fullscreen play mode: " + screen);
        }
        catch (Exception error)
        {
            Launcher.Log("Fullscreen transition failed: " + error);
            ToPreview();
        }
    }

    private void ToPreview()
    {
        if (IsDisposed || Disposing) return;
        desktopInput.Stop();
        RefreshMonitors();
        Rectangle work = SelectedScreen().WorkingArea;
        int width = Math.Min(1040, work.Width);
        int height = Math.Min(700, work.Height);
        toolbar.Visible = true;
        DesktopLayer.Float(Handle,
            new Rectangle(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height),
            true, true);
        mode = Mode.Preview;
        web.Focus();
        Launcher.Log("Window preview mode.");
    }

    private void RefreshMonitors()
    {
        var selected = monitors.SelectedItem as MonitorChoice;
        string name = direct ? selectedDisplay : selected == null ? Screen.PrimaryScreen.DeviceName : selected.Device;
        monitors.Items.Clear();
        int index = 0;
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.DeviceName == name) index = monitors.Items.Count;
            monitors.Items.Add(new MonitorChoice(screen));
        }
        if (monitors.Items.Count > 0) monitors.SelectedIndex = index;
    }

    private Screen SelectedScreen()
    {
        var selected = monitors.SelectedItem as MonitorChoice;
        string device = direct ? selectedDisplay : selected == null ? null : selected.Device;
        return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device) ?? Screen.PrimaryScreen;
    }

    private void UpdateDesktopInput()
    {
        if (mode == Mode.Wallpaper && gameReady && dualLayer.Checked)
        {
            if (!desktopInput.Start())
            {
                Launcher.Log("Dual-layer mouse input could not be enabled.");
                dualLayer.Checked = false;
            }
        }
        else if (desktopInput.Active)
            desktopInput.Stop();
    }

    private void QueueDesktopPointer(DesktopInput.Pointer item)
    {
        if (web.IsDisposed || web.CoreWebView2 == null || viewportWidth <= 0 || viewportHeight <= 0 ||
            web.ClientSize.Width <= 0 || web.ClientSize.Height <= 0) return;
        Point p = web.PointToClient(item.At);
        if (item.Type != "mouseReleased" && !web.ClientRectangle.Contains(p)) return;
        double x = Math.Max(0, Math.Min(viewportWidth - 0.01, p.X * viewportWidth / web.ClientSize.Width));
        double y = Math.Max(0, Math.Min(viewportHeight - 0.01, p.Y * viewportHeight / web.ClientSize.Height));
        string json = String.Format(CultureInfo.InvariantCulture,
            @"{{""type"":""{0}"",""button"":""{1}"",""buttons"":{2},""x"":{3},""y"":{4},""clickCount"":{5},""deltaX"":0,""deltaY"":{6}}}",
            item.Type, item.Button, item.Buttons, x, y, item.Clicks, item.Delta);
        inputQueue = ForwardInput(inputQueue, "Input.dispatchMouseEvent", json, item.Type == "mousePressed");
    }

    private void QueueDesktopKey(DesktopKey item)
    {
        if (web.IsDisposed || web.CoreWebView2 == null) return;
        inputQueue = ForwardInput(inputQueue, "Input.dispatchKeyEvent", item.Json, false);
    }

    private async Task ForwardInput(Task previous, string method, string json, bool logClick)
    {
        await previous;
        if (web.IsDisposed || web.CoreWebView2 == null) return;
        try
        {
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json);
            if (logClick) Launcher.Log("Desktop empty-area click forwarded to game.");
        }
        catch (Exception error) { Launcher.Log("Desktop input was not accepted: " + error.Message); }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (mode == Mode.Play) DesktopLayer.KeepPlayVisible(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (mode == Mode.Play && IsHandleCreated) DesktopLayer.ReleaseTopmost(Handle);
    }

    private sealed class MonitorChoice
    {
        internal readonly string Device;
        private readonly string label;
        internal MonitorChoice(Screen screen)
        {
            Device = screen.DeviceName;
            label = (screen.Primary ? "主屏 " : "显示器 ") + screen.Bounds.Width + "×" + screen.Bounds.Height;
        }
        public override string ToString() { return label; }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            if (m.WParam.ToInt32() == 1) Toggle();
            else if (m.WParam.ToInt32() == 2 && !direct) ToPreview();
            return;
        }
        base.WndProc(ref m);
        if ((m.Msg == 0x007E || m.Msg == 0x02E0 || m.Msg == 0x02E3) && IsHandleCreated && desktopCheck != null)
            BeginInvoke((Action)RefreshWallpaperGeometry);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (IsHandleCreated)
        {
            DesktopLayer.UnregisterHotKey(Handle, 1);
            DesktopLayer.UnregisterHotKey(Handle, 2);
        }
        desktopInput.Stop();
        desktopCheck.Stop();
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (desktopInput != null) desktopInput.Dispose();
            if (desktopCheck != null) desktopCheck.Dispose();
            if (tray != null) { tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
        }
        base.Dispose(disposing);
    }
}

internal sealed class LocalGameContent
{
    internal const string Origin = "https://stardew.desktop/";
    private readonly string directory;
    private readonly bool unifiedCursor;

    internal LocalGameContent(string directory, bool unifiedCursor)
    {
        this.directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        this.unifiedCursor = unifiedCursor;
    }

    internal static bool IsOrigin(string uri)
    {
        Uri url;
        return Uri.TryCreate(uri, UriKind.Absolute, out url) && url.Scheme == "https" &&
            url.Host.Equals("stardew.desktop", StringComparison.OrdinalIgnoreCase) && url.IsDefaultPort;
    }

    internal void Install(CoreWebView2 web, CoreWebView2Environment runtime)
    {
        if (!unifiedCursor)
            web.SetVirtualHostNameToFolderMapping("stardew.desktop", directory, CoreWebView2HostResourceAccessKind.DenyCors);
        web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        web.WebResourceRequested += delegate (object sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            string url = args.Request.Uri;
            if (!IsOrigin(url))
            {
                if (url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                    args.Response = runtime.CreateWebResourceResponse(null, 403, "Local game only", "");
                return;
            }
            try
            {
                string part = Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimStart('/'));
                if (part.Length == 0) part = "index.html";
                string path = Path.GetFullPath(Path.Combine(directory, part.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    args.Response = runtime.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                string headers = "Content-Type: " + Mime(path) + "\r\n" +
                    "Cross-Origin-Opener-Policy: same-origin\r\n" +
                    "Cross-Origin-Embedder-Policy: require-corp\r\n" +
                    "Cross-Origin-Resource-Policy: same-origin\r\n" +
                    "X-Content-Type-Options: nosniff";
                Stream body = OpenResource(part, path);
                try { args.Response = runtime.CreateWebResourceResponse(body, 200, "OK", headers); }
                catch { body.Dispose(); throw; }
            }
            catch (ArgumentException) { args.Response = runtime.CreateWebResourceResponse(null, 400, "Bad Request", ""); }
            catch (IOException) { args.Response = runtime.CreateWebResourceResponse(null, 500, "Read Error", ""); }
            catch (UnauthorizedAccessException) { args.Response = runtime.CreateWebResourceResponse(null, 403, "Forbidden", ""); }
        };
    }

    private Stream OpenResource(string part, string path)
    {
        if (unifiedCursor)
        {
            if (part == UnifiedCursor.Resource) return UnifiedCursor.Open(path);
            if (part == "game.js")
            {
                // A DPI transition can change devicePixelRatio independently of
                // the CSS box. Keep the game's own Resize callback for both.
                string script = File.ReadAllText(path).Replace("new ResizeObserver(() => {",
                        "let pixelWidth = 0, pixelHeight = 0;\nconst resizeGame = () => {")
                    .Replace("if (nw > 0 && nh > 0) try { exports.WasmBootstrap.Resize(nw, nh); } catch {}",
                        "if (nw > 0 && nh > 0 && (nw !== pixelWidth || nh !== pixelHeight)) try {" +
                        " exports.WasmBootstrap.Resize(nw, nh); pixelWidth = nw; pixelHeight = nh; } catch {}")
                    .Replace("}).observe(canvas);", "};\nnew ResizeObserver(resizeGame).observe(canvas);\n" +
                        "addEventListener('resize', resizeGame);\n" +
                        "let lastDpr = devicePixelRatio;\nsetInterval(() => { " +
                        "if (lastDpr !== devicePixelRatio) { lastDpr = devicePixelRatio; resizeGame(); } }, 250);");
                return new MemoryStream(Encoding.UTF8.GetBytes(script), false);
            }
            if (part == "_framework/blazor.boot.json")
            {
                // The original runtime can be cached by the windowed edition
                // at the same origin. Keep the shared OPFS saves and bypass
                // only the .NET boot resource cache for the direct edition.
                string config = File.ReadAllText(path).Replace(
                    "\"cacheBootResources\": true", "\"cacheBootResources\": false");
                return new MemoryStream(Encoding.UTF8.GetBytes(config), false);
            }
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static string Mime(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".html": return "text/html; charset=utf-8";
            case ".js":
            case ".mjs": return "text/javascript; charset=utf-8";
            case ".css": return "text/css; charset=utf-8";
            case ".json": return "application/json";
            case ".wasm": return "application/wasm";
            case ".png": return "image/png";
            case ".svg": return "image/svg+xml";
            case ".count": return "text/plain; charset=utf-8";
            default: return "application/octet-stream";
        }
    }
}
