using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DesktopMochi;

internal sealed class MochiSettings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Size { get; set; } = 340;
    public string Preset { get; set; } = "Sakura Mochi";
    public bool Roam { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public bool GpuFur { get; set; } = true;
    public bool Sound { get; set; } = true;
}

internal sealed class MochiForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_PAINT = 0x000F;
    private const int BLACK_BRUSH = 4;
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    private static readonly string[] Presets =
    [
        "Sakura Mochi",
        "Golden Fluff",
        "Midnight Velvet",
        "Matcha Puff",
    ];

    private static readonly (string Label, int Size)[] SizeOptions =
    [
        ("Small (240 px)", 240),
        ("Medium (340 px)", 340),
        ("Large (440 px)", 440),
        ("Extra Large (560 px)", 560),
    ];

    private readonly string _appDataDir;
    private readonly string _settingsPath;
    private readonly string _distDir;
    private readonly string _repoDir;
    private readonly MochiSettings _settings;
    private readonly EventWaitHandle _wakeEvent;
    private readonly CancellationTokenSource _cts = new();

    private readonly WebView2 _webView;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _tickTimer;

    private ToolStripMenuItem _roamItem = null!;
    private ToolStripMenuItem _topMostItem = null!;
    private ToolStripMenuItem _gpuFurItem = null!;
    private ToolStripMenuItem _soundItem = null!;
    private ToolStripMenuItem _presetMenu = null!;
    private ToolStripMenuItem _sizeMenu = null!;

    private bool _webReady;
    private bool _isClickThrough;
    private bool _menuOpen;
    private bool _dragging;
    private Point _dragStartCursor;
    private Point _dragStartFormLoc;
    private Point _lastDragCursor;
    private int _pointerTickCounter;

    // Optional Roam-along-taskbar state (off by default; toggleable in menu)
    private readonly Random _rng = new();
    private string _roamState = "sit";
    private int _roamDir = -1;
    private int _roamTicksLeft = 90;

    public MochiForm(EventWaitHandle wakeEvent)
    {
        _wakeEvent = wakeEvent;
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopMochi"
        );
        Directory.CreateDirectory(_appDataDir);
        _settingsPath = Path.Combine(_appDataDir, "settings.json");

        (_repoDir, _distDir) = ResolveProjectPaths();
        _settings = LoadSettings();

        Text = "Mochi-Puff";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = _settings.AlwaysOnTop;
        BackColor = Color.Black;
        Size = new Size(_settings.Size, _settings.Size);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "mochi.ico");
        if (File.Exists(iconPath))
        {
            Icon = new Icon(iconPath);
        }

        ApplyInitialLocation();

        _contextMenu = BuildContextMenu();
        _contextMenu.Opening += (_, _) =>
        {
            _menuOpen = true;
            SetClickThrough(false);
        };
        _contextMenu.Closed += (_, _) => _menuOpen = false;

        _trayIcon = new NotifyIcon
        {
            Text = "Mochi-Puff Desktop Pet",
            Icon = Icon ?? SystemIcons.Application,
            ContextMenuStrip = _contextMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => WakeAndBoop();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Transparent,
        };
        Controls.Add(_webView);

        _tickTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _tickTimer.Tick += OnTick;

        Load += OnFormLoad;
        FormClosing += OnFormClosing;

        StartWakeListener();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableDwmTransparency();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND)
        {
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WM_PAINT)
        {
            var hdc = BeginPaint(Handle, out var ps);
            try
            {
                FillRect(hdc, ref ps.rcPaint, GetStockObject(BLACK_BRUSH));
            }
            finally
            {
                EndPaint(Handle, ref ps);
            }
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    private void EnableDwmTransparency()
    {
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref margins);

        int ncrp = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(Handle, DWMWA_NCRENDERING_POLICY, ref ncrp, sizeof(int));

        int corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        try
        {
            if (!Directory.Exists(_distDir))
            {
                MessageBox.Show(
                    $"Could not find built web app folder:\n{_distDir}\n\nRun `vp build` in the project folder first.",
                    "Mochi-Puff",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                Close();
                return;
            }

            var userDataFolder = Path.Combine(_appDataDir, "WebView2");
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-features=msWebOOUI,msPdfOOUI --autoplay-policy=no-user-gesture-required"
            );
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            core.SetVirtualHostNameToFolderMapping(
                "mochi.app",
                _distDir,
                CoreWebView2HostResourceAccessKind.Allow
            );

            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess) return;
                _webReady = true;
                await ApplySavedVisualStateAsync();
            };

            string gpuParam = _settings.GpuFur ? "1" : "0";
            string soundParam = _settings.Sound ? "1" : "0";
            core.Navigate($"https://mochi.app/index.html?desktop=1&gpuFur={gpuParam}&sound={soundParam}");
            _tickTimer.Start();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(_appDataDir, "error.log"), ex.ToString());
            MessageBox.Show(
                $"Failed to start Mochi-Puff:\n{ex.Message}",
                "Mochi-Puff",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            Close();
        }
    }

    private async Task ApplySavedVisualStateAsync()
    {
        if (!_webReady || _webView.CoreWebView2 is null) return;
        string presetJson = JsonSerializer.Serialize(_settings.Preset);
        string gpuVal = _settings.GpuFur ? "true" : "false";
        string soundVal = _settings.Sound ? "true" : "false";
        await ExecuteScriptSafeAsync(
            $"window.__mochiDesktop && (window.__mochiDesktop.applyPreset({presetJson}, true), window.__mochiDesktop.setGPUFur({gpuVal}), window.__mochiDesktop.setSoundEnabled({soundVal}));"
        );
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
            string? msgType = typeProp.GetString();

            if (msgType == "startDrag")
            {
                _dragging = true;
                _dragStartCursor = Cursor.Position;
                _lastDragCursor = _dragStartCursor;
                _dragStartFormLoc = Location;
                SetClickThrough(false);
            }
            else if (msgType == "contextMenu")
            {
                _contextMenu.Show(Cursor.Position);
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (IsDisposed) return;

        var cursor = Cursor.Position;

        if (_dragging)
        {
            if ((Control.MouseButtons & MouseButtons.Left) == 0)
            {
                _dragging = false;
                _settings.X = Left;
                _settings.Y = Top;
                SaveSettings();
                _ = ExecuteScriptSafeAsync("window.__mochiDesktop && window.__mochiDesktop.endDrag();");
                return;
            }

            int nextX = _dragStartFormLoc.X + (cursor.X - _dragStartCursor.X);
            int nextY = _dragStartFormLoc.Y + (cursor.Y - _dragStartCursor.Y);

            var vs = SystemInformation.VirtualScreen;
            int margin = Math.Max(40, Width / 3);
            nextX = Math.Clamp(nextX, vs.Left - margin, vs.Right - Width + margin);
            nextY = Math.Clamp(nextY, vs.Top - margin, vs.Bottom - Height + margin);

            int dx = cursor.X - _lastDragCursor.X;
            int dy = cursor.Y - _lastDragCursor.Y;
            _lastDragCursor = cursor;

            Location = new Point(nextX, nextY);

            if (_webReady && (dx != 0 || dy != 0))
            {
                double vx = Math.Clamp(dx * 0.16, -5.0, 5.0);
                double vy = Math.Clamp(-dy * 0.16, -5.0, 5.0);
                _ = ExecuteScriptSafeAsync(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "window.__mochiDesktop && window.__mochiDesktop.applyDragImpulse({0:F3},{1:F3});",
                        vx,
                        vy
                    )
                );
            }
            return;
        }

        // Optional DesktopCat-style gentle roaming along the taskbar
        if (_settings.Roam && !_menuOpen)
        {
            StepRoaming();
        }

        // Hit-test circular/elliptical silhouette around Mochi so empty transparent corners are click-through
        double cx = Left + Width * 0.5;
        double cy = Top + Height * 0.51;
        double rx = Width * 0.34;
        double ry = Height * 0.34;
        double ndx = (cursor.X - cx) / rx;
        double ndy = (cursor.Y - cy) / ry;
        bool overMochi = (ndx * ndx + ndy * ndy) <= 1.0;

        bool shouldClickThrough = !overMochi && !_menuOpen;
        SetClickThrough(shouldClickThrough);

        // Gently track global cursor across the desktop at ~20 Hz when cursor is outside Mochi
        _pointerTickCounter++;
        if (_webReady && !overMochi && _pointerTickCounter % 3 == 0)
        {
            double lookX = Math.Clamp((cursor.X - cx) / 520.0, -1.0, 1.0);
            double lookY = Math.Clamp((cy - cursor.Y) / 420.0, -1.0, 1.0);
            _ = ExecuteScriptSafeAsync(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "window.__mochiDesktop && window.__mochiDesktop.setGlobalPointer({0:F3},{1:F3});",
                    lookX,
                    lookY
                )
            );
        }
    }

    private void StepRoaming()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int floorTop = wa.Bottom - Height + (int)(Height * 0.10);

        if (Math.Abs(Top - floorTop) > 2)
        {
            Top = Top < floorTop ? Math.Min(floorTop, Top + 14) : Math.Max(floorTop, Top - 14);
            return;
        }

        _roamTicksLeft--;
        if (_roamTicksLeft <= 0)
        {
            if (_roamState == "walk")
            {
                _roamState = "sit";
                _roamTicksLeft = _rng.Next(120, 320);
            }
            else
            {
                _roamState = "walk";
                _roamTicksLeft = _rng.Next(120, 360);
                if (_rng.Next(2) == 0) _roamDir = -_roamDir;
            }
        }

        if (_roamState == "walk")
        {
            int nextX = Left + _roamDir;
            int minX = wa.Left - (int)(Width * 0.08);
            int maxX = wa.Right - Width + (int)(Width * 0.08);
            if (nextX <= minX)
            {
                nextX = minX;
                _roamDir = 1;
            }
            else if (nextX >= maxX)
            {
                nextX = maxX;
                _roamDir = -1;
            }
            Left = nextX;
        }
    }

    private void SetClickThrough(bool enable)
    {
        if (_isClickThrough == enable || !IsHandleCreated) return;
        _isClickThrough = enable;
        int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
        if (enable)
        {
            exStyle |= WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }
        SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var boopItem = new ToolStripMenuItem("Boop Mochi!");
        boopItem.Click += (_, _) => _ = ExecuteScriptSafeAsync("window.__mochiDesktop && window.__mochiDesktop.boop();");
        menu.Items.Add(boopItem);

        menu.Items.Add(new ToolStripSeparator());

        _presetMenu = new ToolStripMenuItem("Fur Preset");
        foreach (string preset in Presets)
        {
            string captured = preset;
            var item = new ToolStripMenuItem(captured) { Checked = _settings.Preset == captured };
            item.Click += async (_, _) =>
            {
                _settings.Preset = captured;
                SaveSettings();
                SyncMenuChecks();
                string presetJson = JsonSerializer.Serialize(captured);
                await ExecuteScriptSafeAsync($"window.__mochiDesktop && window.__mochiDesktop.applyPreset({presetJson});");
            };
            _presetMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_presetMenu);

        _sizeMenu = new ToolStripMenuItem("Size");
        foreach (var (label, sizePx) in SizeOptions)
        {
            int capturedSize = sizePx;
            var item = new ToolStripMenuItem(label) { Checked = _settings.Size == capturedSize };
            item.Click += (_, _) => ResizeKeepingCenter(capturedSize);
            _sizeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_sizeMenu);

        menu.Items.Add(new ToolStripSeparator());

        _roamItem = new ToolStripMenuItem("Roam Along Taskbar") { Checked = _settings.Roam };
        _roamItem.Click += (_, _) =>
        {
            _settings.Roam = !_settings.Roam;
            SaveSettings();
            SyncMenuChecks();
        };
        menu.Items.Add(_roamItem);

        _topMostItem = new ToolStripMenuItem("Always on Top") { Checked = _settings.AlwaysOnTop };
        _topMostItem.Click += (_, _) =>
        {
            _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
            TopMost = _settings.AlwaysOnTop;
            SaveSettings();
            SyncMenuChecks();
        };
        menu.Items.Add(_topMostItem);

        _gpuFurItem = new ToolStripMenuItem("Full GPU Fur (60k Strands)") { Checked = _settings.GpuFur };
        _gpuFurItem.Click += async (_, _) =>
        {
            _settings.GpuFur = !_settings.GpuFur;
            SaveSettings();
            SyncMenuChecks();
            string flag = _settings.GpuFur ? "true" : "false";
            await ExecuteScriptSafeAsync($"window.__mochiDesktop && window.__mochiDesktop.setGPUFur({flag});");
        };
        menu.Items.Add(_gpuFurItem);

        _soundItem = new ToolStripMenuItem("Monster Sounds") { Checked = _settings.Sound };
        _soundItem.Click += async (_, _) =>
        {
            _settings.Sound = !_settings.Sound;
            SaveSettings();
            SyncMenuChecks();
            string flag = _settings.Sound ? "true" : "false";
            await ExecuteScriptSafeAsync($"window.__mochiDesktop && window.__mochiDesktop.setSoundEnabled({flag});");
            if (_settings.Sound)
            {
                await ExecuteScriptSafeAsync("window.__mochiDesktop && window.__mochiDesktop.boop();");
            }
        };
        menu.Items.Add(_soundItem);

        menu.Items.Add(new ToolStripSeparator());

        var resetItem = new ToolStripMenuItem("Reset Position & View");
        resetItem.Click += async (_, _) =>
        {
            _settings.X = null;
            _settings.Y = null;
            ApplyInitialLocation();
            SaveSettings();
            await ExecuteScriptSafeAsync("window.__mochiDesktop && (window.__mochiDesktop.resetView(), window.__mochiDesktop.boop());");
        };
        menu.Items.Add(resetItem);

        var openStudioItem = new ToolStripMenuItem("Open Full Studio in Browser");
        openStudioItem.Click += async (_, _) => await OpenFullStudioAsync();
        menu.Items.Add(openStudioItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit Mochi-Puff");
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void SyncMenuChecks()
    {
        _roamItem.Checked = _settings.Roam;
        _topMostItem.Checked = _settings.AlwaysOnTop;
        _gpuFurItem.Checked = _settings.GpuFur;
        _soundItem.Checked = _settings.Sound;

        foreach (ToolStripItem item in _presetMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem mi)
            {
                mi.Checked = mi.Text == _settings.Preset;
            }
        }

        for (int i = 0; i < SizeOptions.Length && i < _sizeMenu.DropDownItems.Count; i++)
        {
            if (_sizeMenu.DropDownItems[i] is ToolStripMenuItem mi)
            {
                mi.Checked = SizeOptions[i].Size == _settings.Size;
            }
        }
    }

    private void ResizeKeepingCenter(int newSize)
    {
        int centerX = Left + Width / 2;
        int centerY = Top + Height / 2;
        _settings.Size = newSize;
        Size = new Size(newSize, newSize);
        Location = new Point(centerX - newSize / 2, centerY - newSize / 2);
        _settings.X = Left;
        _settings.Y = Top;
        SaveSettings();
        SyncMenuChecks();
    }

    private void ApplyInitialLocation()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        if (_settings.X.HasValue && _settings.Y.HasValue)
        {
            var vs = SystemInformation.VirtualScreen;
            int x = Math.Clamp(_settings.X.Value, vs.Left - Width / 2, vs.Right - Width / 2);
            int y = Math.Clamp(_settings.Y.Value, vs.Top - Height / 2, vs.Bottom - Height / 2);
            Location = new Point(x, y);
        }
        else
        {
            // Default: cozy spot on the bottom-right right above the taskbar (away from DesktopCat on bottom-left)
            int x = wa.Right - Width - 36;
            int y = wa.Bottom - Height + (int)(Height * 0.08);
            Location = new Point(x, y);
        }
    }

    private async Task OpenFullStudioAsync()
    {
        try
        {
            bool portOpen = await IsPortOpenAsync(3000);
            if (!portOpen)
            {
                string serverJs = Path.Combine(_repoDir, "server.js");
                if (File.Exists(serverJs))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = $"\"{serverJs}\"",
                        WorkingDirectory = _repoDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    for (int i = 0; i < 15; i++)
                    {
                        await Task.Delay(120);
                        if (await IsPortOpenAsync(3000)) break;
                    }
                }
            }
            Process.Start(new ProcessStartInfo("http://localhost:3000/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open studio view: {ex.Message}", "Mochi-Puff");
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return await Task.WhenAny(task, Task.Delay(200)) == task && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void StartWakeListener()
    {
        Task.Run(() =>
        {
            var handles = new WaitHandle[] { _wakeEvent, _cts.Token.WaitHandle };
            while (!_cts.IsCancellationRequested)
            {
                int idx = WaitHandle.WaitAny(handles);
                if (idx == 0 && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(WakeAndBoop);
                    }
                    catch
                    {
                        // Ignore during shutdown
                    }
                }
                else
                {
                    break;
                }
            }
        });
    }

    private void WakeAndBoop()
    {
        if (IsDisposed) return;
        Visible = true;
        BringToFront();
        if (_settings.AlwaysOnTop)
        {
            TopMost = false;
            TopMost = true;
        }
        _ = ExecuteScriptSafeAsync("window.__mochiDesktop && window.__mochiDesktop.boop();");
    }

    private async Task ExecuteScriptSafeAsync(string js)
    {
        if (!_webReady || IsDisposed || _webView.CoreWebView2 is null) return;
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch
        {
            // Ignore transient script calls during unload
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cts.Cancel();
        _tickTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _settings.X = Left;
        _settings.Y = Top;
        SaveSettings();
    }

    private MochiSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var parsed = JsonSerializer.Deserialize<MochiSettings>(File.ReadAllText(_settingsPath));
                if (parsed is not null)
                {
                    parsed.Size = Math.Clamp(parsed.Size, 160, 600);
                    return parsed;
                }
            }
        }
        catch
        {
            // Fall back to defaults
        }
        return new MochiSettings();
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Best-effort persistence
        }
    }

    private static (string RepoDir, string DistDir) ResolveProjectPaths()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "dist");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
            {
                return (dir.FullName, candidate);
            }
            dir = dir.Parent;
        }

        const string fallbackRepo = @"f:\WebProjects\My-3d-gpu-fur-simulation";
        return (fallbackRepo, Path.Combine(fallbackRepo, "dist"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);
}
