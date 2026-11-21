using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace DesktopMochi;

internal sealed class MochiSettings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Size { get; set; } = 340;
    public string Preset { get; set; } = "Sakura Mochi";
    public bool Roam { get; set; } = false;
    public string? RoamMode { get; set; }
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
    private const uint ABM_GETTASKBARPOS = 5;
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;
    private const int SW_RESTORE = 9;
    private const string MateCraftExePath = @"F:\RustProjects\My-MateCraft\target\release\matecraft.exe";
    private const string MateCraftWorkDir = @"F:\RustProjects\My-MateCraft";

    private static readonly (string Label, string Mode)[] RoamOptions =
    [
        ("Stay Put", "stay"),
        ("Roam Along Taskbar", "taskbar"),
        ("Roam Whole Desktop", "desktop"),
    ];

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
    private readonly string _appearancePath;
    private readonly string _distDir;
    private readonly string _repoDir;
    private readonly MochiSettings _settings;
    private readonly EventWaitHandle _wakeEvent;
    private readonly CancellationTokenSource _cts = new();

    private readonly WebView2 _webView;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Windows.Forms.Timer _appearanceDebounceTimer;
    private FileSystemWatcher? _appearanceWatcher;
    private DateTime _suppressAppearanceWatchUntil = DateTime.MinValue;
    private string? _lastAppliedAppearanceJson;

    private ToolStripMenuItem _movementMenu = null!;
    private ToolStripMenuItem _topMostItem = null!;
    private ToolStripMenuItem _gpuFurItem = null!;
    private ToolStripMenuItem _soundItem = null!;
    private ToolStripMenuItem _startupItem = null!;
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

    // Roaming state (Taskbar edge or Whole Desktop 2D)
    private readonly Random _rng = new();
    private string _roamState = "sit";
    private int _roamDir = -1;
    private int _roamTicksLeft = 90;
    private double _roamPosX;
    private double _roamPosY;
    private double _roamTargetX;
    private double _roamTargetY;
    private bool _hasDesktopTarget;
    private double _roamPhase;
    private double _crawlPhase;
    private bool _isCrawlingSent;
    private int _crawlSyncCounter;
    private bool _isSleeping;
    private int _stayNapTicksLeft = 2400;

    public MochiForm(EventWaitHandle wakeEvent)
    {
        _wakeEvent = wakeEvent;
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopMochi"
        );
        Directory.CreateDirectory(_appDataDir);
        _settingsPath = Path.Combine(_appDataDir, "settings.json");
        _appearancePath = Path.Combine(_appDataDir, "appearance.json");

        (_repoDir, _distDir) = ResolveProjectPaths();
        _settings = LoadSettings();

        Text = "Mochi-Puff";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = _settings.AlwaysOnTop;
        BackColor = Color.Black;
        int canvasPx = CanvasSizeFor(_settings.Size);
        Size = new Size(canvasPx, canvasPx);

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

        _appearanceDebounceTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _appearanceDebounceTimer.Tick += async (_, _) =>
        {
            _appearanceDebounceTimer.Stop();
            await ApplyAppearanceFileLiveAsync();
        };

        Load += OnFormLoad;
        FormClosing += OnFormClosing;

        StartAppearanceWatcher();
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
        string soundVal = _settings.Sound ? "true" : "false";
        await ExecuteScriptSafeAsync(
            $"window.__mochiDesktop && window.__mochiDesktop.setSoundEnabled({soundVal});"
        );

        string? savedAppearance = TryReadAppearanceJson();
        if (savedAppearance is not null)
        {
            _lastAppliedAppearanceJson = savedAppearance;
            SyncSettingsFromAppearanceJson(savedAppearance);
            await ExecuteScriptSafeAsync(
                $"window.__mochiDesktop && window.__mochiDesktop.applyAppearance({savedAppearance}, true);"
            );
            return;
        }

        string presetJson = JsonSerializer.Serialize(_settings.Preset);
        string gpuVal = _settings.GpuFur ? "true" : "false";
        await ExecuteScriptSafeAsync(
            $"window.__mochiDesktop && (window.__mochiDesktop.applyPreset({presetJson}, true), window.__mochiDesktop.setGPUFur({gpuVal}));"
        );
        await PersistCurrentAppearanceAsync();
    }

    private void StartAppearanceWatcher()
    {
        try
        {
            _appearanceWatcher = new FileSystemWatcher(_appDataDir, "appearance.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            FileSystemEventHandler onChanged = (_, _) => ScheduleAppearanceReload();
            RenamedEventHandler onRenamed = (_, e) =>
            {
                if (string.Equals(e.Name, "appearance.json", StringComparison.OrdinalIgnoreCase))
                {
                    ScheduleAppearanceReload();
                }
            };

            _appearanceWatcher.Changed += onChanged;
            _appearanceWatcher.Created += onChanged;
            _appearanceWatcher.Renamed += onRenamed;
        }
        catch
        {
            // Best-effort live watcher
        }
    }

    private void ScheduleAppearanceReload()
    {
        if (DateTime.UtcNow < _suppressAppearanceWatchUntil || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _appearanceDebounceTimer.Stop();
                _appearanceDebounceTimer.Start();
            });
        }
        catch
        {
            // Ignore during shutdown
        }
    }

    private async Task ApplyAppearanceFileLiveAsync()
    {
        if (!_webReady || IsDisposed || _webView.CoreWebView2 is null) return;
        if (DateTime.UtcNow < _suppressAppearanceWatchUntil) return;

        string? json = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            json = TryReadAppearanceJson();
            if (json is not null) break;
            await Task.Delay(45);
        }

        if (json is null || json == _lastAppliedAppearanceJson) return;
        _lastAppliedAppearanceJson = json;
        SyncSettingsFromAppearanceJson(json);

        await ExecuteScriptSafeAsync(
            $"window.__mochiDesktop && window.__mochiDesktop.applyAppearance({json}, false);"
        );
    }

    private string? TryReadAppearanceJson()
    {
        try
        {
            if (!File.Exists(_appearancePath)) return null;
            string raw = File.ReadAllText(_appearancePath).Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return raw;
        }
        catch
        {
            return null;
        }
    }

    private void SyncSettingsFromAppearanceJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool changed = false;

            if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                string nextName = nameProp.GetString() ?? "Custom";
                if (_settings.Preset != nextName)
                {
                    _settings.Preset = nextName;
                    changed = true;
                }
            }

            if (root.TryGetProperty("gpuFurEnabled", out var gpuProp) &&
                (gpuProp.ValueKind == JsonValueKind.True || gpuProp.ValueKind == JsonValueKind.False))
            {
                bool nextGpu = gpuProp.GetBoolean();
                if (_settings.GpuFur != nextGpu)
                {
                    _settings.GpuFur = nextGpu;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveSettings();
                SyncMenuChecks();
            }
        }
        catch
        {
            // Ignore malformed metadata
        }
    }

    private async Task PersistCurrentAppearanceAsync()
    {
        if (!_webReady || IsDisposed || _webView.CoreWebView2 is null) return;
        try
        {
            string rawResult = await _webView.CoreWebView2.ExecuteScriptAsync(
                "window.__mochiDesktop && window.__mochiDesktop.getAppearance ? JSON.stringify(window.__mochiDesktop.getAppearance()) : ''"
            );
            string? appearanceJson = JsonSerializer.Deserialize<string>(rawResult);
            if (string.IsNullOrWhiteSpace(appearanceJson)) return;

            // Validate & pretty-print so appearance.json stays clean and readable
            using var doc = JsonDocument.Parse(appearanceJson);
            string formatted = JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true }
            );

            _suppressAppearanceWatchUntil = DateTime.UtcNow.AddMilliseconds(650);
            _lastAppliedAppearanceJson = formatted;

            string tmpPath = _appearancePath + ".tmp";
            File.WriteAllText(tmpPath, formatted);
            File.Move(tmpPath, _appearancePath, overwrite: true);
        }
        catch
        {
            // Best-effort sync back to shared appearance file
        }
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
                SendCrawlState(false, 0, 0);
                SetSleeping(false);
            }
            else if (msgType == "contextMenu")
            {
                _contextMenu.Show(Cursor.Position);
            }
            else if (msgType == "openMateCraft")
            {
                SetSleeping(false);
                OpenMateCraft();
            }
            else if (msgType == "wokeUp")
            {
                _isSleeping = false;
                if (_roamState == "sleep")
                {
                    _roamState = "sit";
                    _roamTicksLeft = _rng.Next(180, 360);
                }
                _stayNapTicksLeft = _rng.Next(2200, 3800);
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
                _roamPosX = Left;
                _roamPosY = Top;
                _hasDesktopTarget = false;
                _roamState = "sit";
                _roamTicksLeft = _rng.Next(90, 200);
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

        // Roaming (Taskbar edge or Whole Desktop 2D) + automatic cozy naps
        string mode = _settings.RoamMode ?? "stay";
        if (mode != "stay" && !_menuOpen)
        {
            if (mode == "desktop")
            {
                StepDesktopRoaming();
            }
            else
            {
                StepTaskbarRoaming();
            }
        }
        else
        {
            if (_isCrawlingSent)
            {
                SendCrawlState(false, 0, 0);
            }
            if (mode == "stay" && !_menuOpen)
            {
                _stayNapTicksLeft--;
                if (_stayNapTicksLeft <= 0)
                {
                    if (_isSleeping)
                    {
                        SetSleeping(false);
                        _stayNapTicksLeft = _rng.Next(2400, 4200);
                    }
                    else
                    {
                        SetSleeping(true);
                        _stayNapTicksLeft = _rng.Next(1100, 2100);
                    }
                }
            }
        }

        // Hit-test circular/elliptical silhouette around Mochi so empty transparent headroom is click-through
        double cx = Left + Width * 0.5;
        double cy = Top + Height * 0.52;
        double rx = Width * 0.26;
        double ry = Height * 0.26;
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

    private (uint Edge, Rectangle Rect) GetTaskbarInfo()
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            var r = Rectangle.FromLTRB(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
            return (abd.uEdge, r);
        }
        return (ABE_BOTTOM, Rectangle.Empty);
    }

    private double AdvanceCrawlSurge()
    {
        _crawlPhase += 0.083;
        if (_crawlPhase > Math.PI * 2000.0) _crawlPhase -= Math.PI * 2000.0;
        // Peak forward push when sin(_crawlPhase) == -1 (body stretched forward), pause when == +1 (body scrunched)
        double push = 0.5 - 0.5 * Math.Sin(_crawlPhase);
        return 0.14 + 1.80 * Math.Pow(push, 1.85);
    }

    private void SetSleeping(bool sleeping)
    {
        if (_isSleeping == sleeping) return;
        _isSleeping = sleeping;
        if (sleeping)
        {
            SendCrawlState(false, 0, 0);
        }
        if (_webReady)
        {
            string val = sleeping ? "true" : "false";
            _ = ExecuteScriptSafeAsync($"window.__mochiDesktop && window.__mochiDesktop.setSleeping({val});");
        }
    }

    private void SendCrawlState(bool active, double dirX, double dirY, bool force = false)
    {
        if (!_webReady) return;
        if (!active)
        {
            if (!_isCrawlingSent && !force) return;
            _isCrawlingSent = false;
            _ = ExecuteScriptSafeAsync("window.__mochiDesktop && window.__mochiDesktop.setCrawlState(false, 0, 0);");
            return;
        }

        _crawlSyncCounter++;
        if (!_isCrawlingSent || force || _crawlSyncCounter % 3 == 0)
        {
            _isCrawlingSent = true;
            _ = ExecuteScriptSafeAsync(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "window.__mochiDesktop && window.__mochiDesktop.setCrawlState(true,{0:F3},{1:F3},{2:F3});",
                    dirX,
                    dirY,
                    _crawlPhase
                )
            );
        }
    }

    private void StepTaskbarRoaming()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        var (edge, tbRect) = GetTaskbarInfo();
        bool verticalTaskbar = edge == ABE_LEFT || edge == ABE_RIGHT;

        if (!verticalTaskbar)
        {
            // Top or Bottom taskbar: align snugly along the actual taskbar edge
            int targetY;
            if (edge == ABE_TOP)
            {
                // If taskbar is docked visible, wa.Top is already below it; if auto-hidden, wa.Top is screen top
                int topAnchor = wa.Top > 4 ? wa.Top : Math.Max(wa.Top, 0);
                targetY = topAnchor - (int)(Height * 0.16);
            }
            else
            {
                targetY = wa.Bottom - Height + (int)(Height * 0.16);
            }

            if (Math.Abs(Top - targetY) > 2)
            {
                double surge = AdvanceCrawlSurge();
                double step = Math.Min(3.2, Math.Max(0.7, Math.Abs(targetY - _roamPosY) * 0.05)) * surge;
                double dySign = _roamPosY < targetY ? -1.0 : 1.0; // +1 = up in 3D
                _roamPosY = _roamPosY < targetY
                    ? Math.Min(targetY, _roamPosY + step)
                    : Math.Max(targetY, _roamPosY - step);
                Top = (int)Math.Round(_roamPosY);
                SendCrawlState(true, 0, dySign);
                return;
            }

            _roamTicksLeft--;
            if (_roamTicksLeft <= 0)
            {
                if (_roamState == "walk")
                {
                    if (_rng.Next(10) < 4)
                    {
                        _roamState = "sleep";
                        _roamTicksLeft = _rng.Next(900, 1700);
                        SetSleeping(true);
                    }
                    else
                    {
                        _roamState = "sit";
                        _roamTicksLeft = _rng.Next(200, 460);
                        SetSleeping(false);
                    }
                }
                else
                {
                    SetSleeping(false);
                    _roamState = "walk";
                    _roamTicksLeft = _rng.Next(220, 500);
                    if (_rng.Next(2) == 0) _roamDir = -_roamDir;
                }
            }

            if (_roamState == "walk" && !_isSleeping)
            {
                double minX = wa.Left - Width * 0.15;
                double maxX = wa.Right - Width + Width * 0.15;
                double surge = AdvanceCrawlSurge();
                _roamPosX += _roamDir * 0.62 * surge;
                if (_roamPosX <= minX)
                {
                    _roamPosX = minX;
                    _roamDir = 1;
                }
                else if (_roamPosX >= maxX)
                {
                    _roamPosX = maxX;
                    _roamDir = -1;
                }
                int nextX = (int)Math.Round(_roamPosX);
                if (nextX != Left) Left = nextX;
                SendCrawlState(true, _roamDir, 0);
            }
            else
            {
                SendCrawlState(false, 0, 0);
            }
        }
        else
        {
            // Left or Right taskbar: crawl vertically along the taskbar edge
            int targetX = edge == ABE_LEFT
                ? wa.Left - (int)(Width * 0.16)
                : wa.Right - Width + (int)(Width * 0.16);

            if (Math.Abs(Left - targetX) > 2)
            {
                double surge = AdvanceCrawlSurge();
                double step = Math.Min(3.2, Math.Max(0.7, Math.Abs(targetX - _roamPosX) * 0.05)) * surge;
                double dxSign = _roamPosX < targetX ? 1.0 : -1.0;
                _roamPosX = _roamPosX < targetX
                    ? Math.Min(targetX, _roamPosX + step)
                    : Math.Max(targetX, _roamPosX - step);
                Left = (int)Math.Round(_roamPosX);
                SendCrawlState(true, dxSign, 0);
                return;
            }

            _roamTicksLeft--;
            if (_roamTicksLeft <= 0)
            {
                if (_roamState == "walk")
                {
                    if (_rng.Next(10) < 4)
                    {
                        _roamState = "sleep";
                        _roamTicksLeft = _rng.Next(900, 1700);
                        SetSleeping(true);
                    }
                    else
                    {
                        _roamState = "sit";
                        _roamTicksLeft = _rng.Next(200, 460);
                        SetSleeping(false);
                    }
                }
                else
                {
                    SetSleeping(false);
                    _roamState = "walk";
                    _roamTicksLeft = _rng.Next(220, 500);
                    if (_rng.Next(2) == 0) _roamDir = -_roamDir;
                }
            }

            if (_roamState == "walk" && !_isSleeping)
            {
                double minY = wa.Top - Height * 0.15;
                double maxY = wa.Bottom - Height + Height * 0.15;
                double surge = AdvanceCrawlSurge();
                _roamPosY += _roamDir * 0.62 * surge;
                if (_roamPosY <= minY)
                {
                    _roamPosY = minY;
                    _roamDir = 1;
                }
                else if (_roamPosY >= maxY)
                {
                    _roamPosY = maxY;
                    _roamDir = -1;
                }
                int nextY = (int)Math.Round(_roamPosY);
                if (nextY != Top) Top = nextY;
                SendCrawlState(true, 0, -_roamDir);
            }
            else
            {
                SendCrawlState(false, 0, 0);
            }
        }
    }

    private void StepDesktopRoaming()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int padX = (int)(Width * 0.14);
        int padY = (int)(Height * 0.14);
        int minX = wa.Left - padX;
        int maxX = Math.Max(minX, wa.Right - Width + padX);
        int minY = wa.Top - padY;
        int maxY = Math.Max(minY, wa.Bottom - Height + padY);

        _roamPhase += 0.028;

        if (_roamState == "sit" || _roamState == "sleep" || _isSleeping)
        {
            SendCrawlState(false, 0, 0);
            _roamTicksLeft--;
            if (_roamTicksLeft <= 0)
            {
                SetSleeping(false);
                _roamTargetX = _rng.Next(minX, maxX + 1);
                _roamTargetY = _rng.Next(minY, maxY + 1);
                _hasDesktopTarget = true;
                _roamState = "walk";
            }
            return;
        }

        if (!_hasDesktopTarget)
        {
            _roamTargetX = _rng.Next(minX, maxX + 1);
            _roamTargetY = _rng.Next(minY, maxY + 1);
            _hasDesktopTarget = true;
        }

        double dx = _roamTargetX - _roamPosX;
        double dy = _roamTargetY - _roamPosY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist < 10.0)
        {
            _hasDesktopTarget = false;
            SendCrawlState(false, 0, 0);
            if (_rng.Next(10) < 4)
            {
                _roamState = "sleep";
                _roamTicksLeft = _rng.Next(900, 1700);
                SetSleeping(true);
            }
            else
            {
                _roamState = "sit";
                _roamTicksLeft = _rng.Next(220, 480);
                SetSleeping(false);
            }
            return;
        }

        double dirX = dx / dist;
        double dirY = dy / dist;
        double surge = AdvanceCrawlSurge();
        double speed = Math.Min(0.78, Math.Max(0.32, dist * 0.0055)) * surge;
        double stepX = dirX * speed;
        double stepY = dirY * speed;

        _roamPosX = Math.Clamp(_roamPosX + stepX, minX, maxX);
        _roamPosY = Math.Clamp(_roamPosY + stepY, minY, maxY);

        int newLeft = (int)Math.Round(_roamPosX);
        int newTop = (int)Math.Round(_roamPosY);
        if (newLeft != Left || newTop != Top)
        {
            Location = new Point(newLeft, newTop);
            if (_webReady && _pointerTickCounter % 4 == 0)
            {
                double vx = Math.Clamp(stepX * 0.35, -2.0, 2.0);
                double vy = Math.Clamp(-stepY * 0.35, -2.0, 2.0);
                _ = ExecuteScriptSafeAsync(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "window.__mochiDesktop && window.__mochiDesktop.applyDragImpulse({0:F3},{1:F3});",
                        vx,
                        vy
                    )
                );
            }
        }
        SendCrawlState(true, dirX, -dirY);
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

        var sleepToggleItem = new ToolStripMenuItem("Sleep / Wake");
        sleepToggleItem.Click += (_, _) =>
        {
            if (_isSleeping)
            {
                SetSleeping(false);
                _roamState = "sit";
                _roamTicksLeft = _rng.Next(180, 320);
                _stayNapTicksLeft = _rng.Next(2400, 4200);
                _ = ExecuteScriptSafeAsync("window.__mochiDesktop && window.__mochiDesktop.boop();");
            }
            else
            {
                _roamState = "sleep";
                _roamTicksLeft = _rng.Next(1100, 2000);
                _stayNapTicksLeft = _rng.Next(1100, 2000);
                SetSleeping(true);
            }
        };
        menu.Items.Add(sleepToggleItem);

        var mateCraftItem = new ToolStripMenuItem("Open MateCraft");
        mateCraftItem.Click += (_, _) => OpenMateCraft();
        menu.Items.Add(mateCraftItem);

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
                await PersistCurrentAppearanceAsync();
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

        _movementMenu = new ToolStripMenuItem("Movement");
        foreach (var (label, modeKey) in RoamOptions)
        {
            string capturedMode = modeKey;
            var item = new ToolStripMenuItem(label) { Checked = (_settings.RoamMode ?? "stay") == capturedMode };
            item.Click += (_, _) =>
            {
                _settings.RoamMode = capturedMode;
                _settings.Roam = capturedMode != "stay";
                SetSleeping(false);
                _roamPosX = Left;
                _roamPosY = Top;
                _hasDesktopTarget = false;
                _roamState = "walk";
                _roamTicksLeft = _rng.Next(120, 280);
                _stayNapTicksLeft = _rng.Next(2200, 3800);
                SaveSettings();
                SyncMenuChecks();
            };
            _movementMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_movementMenu);

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
            await PersistCurrentAppearanceAsync();
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

        _startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartWithWindowsEnabled() };
        _startupItem.Click += (_, _) =>
        {
            SetStartWithWindows(!IsStartWithWindowsEnabled());
            SyncMenuChecks();
        };
        menu.Items.Add(_startupItem);

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
        string currentMode = _settings.RoamMode ?? "stay";
        for (int i = 0; i < RoamOptions.Length && i < _movementMenu.DropDownItems.Count; i++)
        {
            if (_movementMenu.DropDownItems[i] is ToolStripMenuItem mi)
            {
                mi.Checked = RoamOptions[i].Mode == currentMode;
            }
        }

        _topMostItem.Checked = _settings.AlwaysOnTop;
        _gpuFurItem.Checked = _settings.GpuFur;
        _soundItem.Checked = _settings.Sound;
        _startupItem.Checked = IsStartWithWindowsEnabled();

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

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string RunValueName = "DesktopMochi";

    private static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return runKey?.GetValue(RunValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enable)
            {
                string exePath = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "DesktopMochi.exe");
                runKey.SetValue(RunValueName, $"\"{exePath}\"", RegistryValueKind.String);

                // Ensure Task Manager StartupApproved does not mark it disabled
                using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
                approvedKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            else
            {
                runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            // Clean up legacy Startup folder shortcut so Windows doesn't duplicate entries
            string legacyShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Mochi-Puff.lnk"
            );
            if (File.Exists(legacyShortcut))
            {
                File.Delete(legacyShortcut);
            }
        }
        catch
        {
            // Best-effort registry persistence
        }
    }

    private static int CanvasSizeFor(int visualSize) => (int)Math.Round(visualSize * 1.32);

    private void ResizeKeepingCenter(int newSize)
    {
        int centerX = Left + Width / 2;
        int centerY = Top + Height / 2;
        _settings.Size = newSize;
        int canvasPx = CanvasSizeFor(newSize);
        Size = new Size(canvasPx, canvasPx);
        Location = new Point(centerX - canvasPx / 2, centerY - canvasPx / 2);
        _roamPosX = Left;
        _roamPosY = Top;
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
        _roamPosX = Left;
        _roamPosY = Top;
    }

    private void OpenMateCraft()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("matecraft"))
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        if (IsIconic(proc.MainWindowHandle))
                        {
                            ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                        }
                        SetForegroundWindow(proc.MainWindowHandle);
                        return;
                    }
                }
                finally
                {
                    proc.Dispose();
                }
            }

            if (File.Exists(MateCraftExePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = MateCraftExePath,
                    WorkingDirectory = Directory.Exists(MateCraftWorkDir)
                        ? MateCraftWorkDir
                        : Path.GetDirectoryName(MateCraftExePath) ?? "",
                    UseShellExecute = true,
                });
            }
            else
            {
                MessageBox.Show($"MateCraft not found at:\n{MateCraftExePath}", "Mochi-Puff");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open MateCraft: {ex.Message}", "Mochi-Puff");
        }
    }

    private async Task OpenFullStudioAsync()
    {
        try
        {
            if (!File.Exists(_appearancePath))
            {
                await PersistCurrentAppearanceAsync();
            }

            int targetPort = 3000;
            bool port3000Open = await IsPortOpenAsync(3000);
            if (port3000Open && !await HasMochiAppearanceApiAsync(3000))
            {
                targetPort = 3001;
            }

            if (!await HasMochiAppearanceApiAsync(targetPort))
            {
                string serverJs = Path.Combine(_repoDir, "server.js");
                if (File.Exists(serverJs))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = $"\"{serverJs}\"",
                        WorkingDirectory = _repoDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.Environment["PORT"] = targetPort.ToString(CultureInfo.InvariantCulture);
                    Process.Start(psi);
                    for (int i = 0; i < 15; i++)
                    {
                        await Task.Delay(120);
                        if (await HasMochiAppearanceApiAsync(targetPort)) break;
                    }
                }
            }
            Process.Start(new ProcessStartInfo($"http://localhost:{targetPort}/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open studio view: {ex.Message}", "Mochi-Puff");
        }
    }

    private static async Task<bool> HasMochiAppearanceApiAsync(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(350) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{port}/api/mochi-appearance");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
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
        _appearanceDebounceTimer.Stop();
        _appearanceWatcher?.Dispose();
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
                    if (string.IsNullOrWhiteSpace(parsed.RoamMode) ||
                        (parsed.RoamMode != "stay" && parsed.RoamMode != "taskbar" && parsed.RoamMode != "desktop"))
                    {
                        parsed.RoamMode = parsed.Roam ? "taskbar" : "stay";
                    }
                    return parsed;
                }
            }
        }
        catch
        {
            // Fall back to defaults
        }
        return new MochiSettings { RoamMode = "stay" };
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

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

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
