using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSCore;
using CSCore.DSP;
using CSCore.SoundIn;
using CSCore.Streams;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UsbHid;
using UsbHid.USB.Classes.Messaging;

namespace VisionStudio
{
    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private Panel stageOuterPanel;
        private Panel stageHeaderPanel;
        private Panel stageViewportHost;
        private Label stageInfoLabel;
        private WebView2 screenWebView;
        private WebView2 studioWebView;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private PopoutPreviewForm popoutForm;
        private Icon customAppIcon;

        private readonly string baseDir;
        private readonly string uiDir;
        private readonly string overlayJsContent;

        private System.Windows.Forms.Timer captureTimer;
        private System.Windows.Forms.Timer telemetryTimer;
        private bool isCapturingFrame = false;
        private DateTime lastCaptureStart = DateTime.MinValue;
        private bool isScreenNavigating = false;
        private DateTime lastNavigationStart = DateTime.MinValue;
        private bool isPushingTelemetry = false;
        private bool isEngineModeTarget = true;

        // Hardware & Stream settings (TL-M10 Vision sub=49 panel is mounted 180Â° inverted by factory, so 180Â° = Right-Side Up!)
        private int targetFps = 25;
        private int jpegQuality = 88;
        private int screenRotation = 180;
        private int screenBrightness = 100;
        private double userWebZoom = 1.0;
        private int lastValidStageWidth = 1280;
        private Button btnRotateScreen;
        private Button btnSaveScreen;

        // Reusable 1920x462 frame buffer
        private const int LCD_WIDTH = 1920;
        private const int LCD_HEIGHT = 462;
        private readonly Bitmap lcdBitmap = new Bitmap(LCD_WIDTH, LCD_HEIGHT, PixelFormat.Format24bppRgb);
        private readonly ImageCodecInfo jpegEncoder;
        private readonly EncoderParameters jpegParams;

        // Transports & Monitors
        private readonly ThermalrightUsbTransport usbTransport;
        private readonly HardwareTelemetryReader telemetryReader;
        private readonly WasapiSpectrumCapture audioCapture;
        private readonly LiveMarketDataService marketService;

        // State synchronization & persistence
        private JObject fullSavedSettings = new JObject();
        private JObject currentOverlayState = null;
        private JObject currentScreenEngineConfig = null;
        private int lastFrameKb = 45;
        private int measuredFps = 25;
        private int frameCounter = 0;
        private DateTime lastFpsTick = DateTime.UtcNow;

        // Virtual host counter for local media directories
        private readonly Dictionary<string, string> mappedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int mediaHostIndex = 1;

        // Off-screen background & tray mode tracking
        private bool isHiddenInTray = false;
        private readonly bool isAutoStartLaunch = false;
        private ToolStripMenuItem trayStartWithWindowsItem;
        private DateTime lastMinimizeTime = DateTime.MinValue;
        private Point savedWindowLocation = new Point(100, 60);
        private Size savedWindowSize = new Size(1460, 940);

        public MainForm(bool isAutoStart = false)
        {
            isAutoStartLaunch = isAutoStart;
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            uiDir = Path.GetFullPath(Path.Combine(baseDir, "..", "ui"));
            if (!Directory.Exists(uiDir))
            {
                uiDir = Path.Combine(baseDir, "ui");
            }

            string overlayPath = Path.Combine(uiDir, "overlay.js");
            overlayJsContent = File.Exists(overlayPath) ? File.ReadAllText(overlayPath, Encoding.UTF8) : "";

            customAppIcon = LoadCustomIcon();

            jpegEncoder = GetJpegEncoder();
            jpegParams = new EncoderParameters(1);
            jpegParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);

            usbTransport = new ThermalrightUsbTransport();
            telemetryReader = new HardwareTelemetryReader();
            audioCapture = new WasapiSpectrumCapture();
            marketService = new LiveMarketDataService();

            LoadFullSettings();
            InitializeLayout();
            InitializeTrayIcon();

            Shown += async (s, e) =>
            {
                SaveCurrentValidWindowBounds();
                await InitializeStudioAsync();
            };
            FormClosing += OnMainFormClosing;
            Move += (s, e) => SaveCurrentValidWindowBounds();
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    MinimizeToBackground(keepInTaskbar: true);
                    return;
                }
                SaveCurrentValidWindowBounds();
                // Ignore resize changes caused by minimize or fullscreen games altering desktop resolution
                if (isHiddenInTray || ClientSize.Width < 700)
                    return;
                UpdateStageAspectLayout();
            };
        }

        private void SaveCurrentValidWindowBounds()
        {
            if (!isHiddenInTray && WindowState == FormWindowState.Normal && Location.X > -10000 && Location.Y > -10000 && Width >= 800 && Height >= 500)
            {
                savedWindowLocation = Location;
                savedWindowSize = Size;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MINIMIZE = 0xF020;
            const int SC_RESTORE = 0xF120;
            const int WM_ACTIVATE = 0x0006;

            if (m.Msg == WM_SYSCOMMAND)
            {
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MINIMIZE)
                {
                    if (!isHiddenInTray)
                    {
                        MinimizeToBackground(keepInTaskbar: true);
                        return;
                    }
                    else if ((DateTime.UtcNow - lastMinimizeTime).TotalMilliseconds > 250)
                    {
                        // User clicked the taskbar icon while hidden off-screen -> restore!
                        RestoreFromTray();
                        return;
                    }
                }
                else if (cmd == SC_RESTORE)
                {
                    RestoreFromTray();
                    return;
                }
            }
            else if (m.Msg == WM_ACTIVATE)
            {
                int wa = m.WParam.ToInt32() & 0xFFFF;
                if (wa != 0 && isHiddenInTray && (DateTime.UtcNow - lastMinimizeTime).TotalMilliseconds > 250)
                {
                    BeginInvoke(new Action(RestoreFromTray));
                }
            }

            base.WndProc(ref m);
        }

        private Icon LoadCustomIcon()
        {
            try
            {
                string icoPath = Path.Combine(baseDir, "VisionStudio.ico");
                if (File.Exists(icoPath))
                {
                    return new Icon(icoPath);
                }
                Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) return exeIcon;
            }
            catch { }
            return SystemIcons.Application;
        }

        private void LoadFullSettings()
        {
            try
            {
                string p = Path.Combine(baseDir, "settings.json");
                if (File.Exists(p))
                {
                    fullSavedSettings = JObject.Parse(File.ReadAllText(p, Encoding.UTF8));
                    screenRotation = fullSavedSettings.Value<int?>("rotation") ?? 180;
                    screenBrightness = fullSavedSettings.Value<int?>("brightness") ?? 100;
                    targetFps = fullSavedSettings.Value<int?>("fps") ?? 25;
                    jpegQuality = fullSavedSettings.Value<int?>("quality") ?? 88;
                    double wz = fullSavedSettings.Value<double?>("webZoom") ?? 100.0;
                    userWebZoom = wz > 5.0 ? (wz / 100.0) : wz;

                    if (fullSavedSettings["savedScreenConfig"] is JObject sc)
                        currentScreenEngineConfig = sc;
                    if (fullSavedSettings["savedOverlayState"] is JObject os)
                        currentOverlayState = os;

                    string psxSyms = fullSavedSettings.Value<string>("psxSymbols");
                    string usSyms = fullSavedSettings.Value<string>("usSymbols");
                    string cryptoSyms = fullSavedSettings.Value<string>("cryptoSymbols");
                    double? pkrRate = fullSavedSettings.Value<double?>("usdToPkr");
                    marketService.UpdateSymbols(psxSyms, usSyms, cryptoSyms, pkrRate);
                }
                if (fullSavedSettings == null) fullSavedSettings = new JObject();
                fullSavedSettings["startWithWindows"] = IsStartWithWindowsEnabled();
            }
            catch { }
        }

        private bool IsStartWithWindowsEnabled()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (rk != null)
                    {
                        string val = rk.GetValue("TLM10VisionStudio") as string;
                        return !string.IsNullOrEmpty(val);
                    }
                }
            }
            catch { }
            return false;
        }

        private void SetStartWithWindows(bool enable, bool startMinimized = true)
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk != null)
                    {
                        if (enable)
                        {
                            string exePath = @"C:\TL-M10-Vision-Studio\bin\VisionStudio.exe";
                            if (!File.Exists(exePath)) exePath = Application.ExecutablePath;
                            string arg = startMinimized ? " --autostart" : "";
                            rk.SetValue("TLM10VisionStudio", $"\"{exePath}\"{arg}");
                        }
                        else
                        {
                            rk.DeleteValue("TLM10VisionStudio", false);
                        }
                    }
                }
                if (fullSavedSettings == null) fullSavedSettings = new JObject();
                fullSavedSettings["startWithWindows"] = enable;
                fullSavedSettings["startMinimizedOnBoot"] = startMinimized;
                SaveFullSettingsToDisk();
                if (trayStartWithWindowsItem != null)
                {
                    trayStartWithWindowsItem.Checked = enable;
                }
                SendEventToStudio(new { type = "startWithWindowsChanged", enabled = enable });
            }
            catch (Exception ex)
            {
                Program.LogError("SetStartWithWindows", ex);
            }
        }

        private void SaveFullSettingsToDisk()
        {
            try
            {
                if (fullSavedSettings == null) fullSavedSettings = new JObject();
                fullSavedSettings["rotation"] = screenRotation;
                fullSavedSettings["brightness"] = screenBrightness;
                fullSavedSettings["fps"] = targetFps;
                fullSavedSettings["quality"] = jpegQuality;
                if (currentScreenEngineConfig != null)
                    fullSavedSettings["savedScreenConfig"] = currentScreenEngineConfig;
                if (currentOverlayState != null)
                    fullSavedSettings["savedOverlayState"] = currentOverlayState;

                var foldersObj = new JObject();
                foreach (var kv in mappedFolders)
                {
                    foldersObj[kv.Key] = kv.Value;
                }
                fullSavedSettings["mappedFolders"] = foldersObj;

                string p = Path.Combine(baseDir, "settings.json");
                File.WriteAllText(p, fullSavedSettings.ToString(Formatting.Indented), Encoding.UTF8);
            }
            catch { }
        }

        private void InitializeLayout()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "Thermalright TL-M10 Vision Studio (1920 Ã— 462 Direct USB Controller)";
            Size = new Size(1460, 940);
            MinimumSize = new Size(1060, 740);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(6, 9, 19);
            ForeColor = Color.FromArgb(240, 244, 255);
            Icon = customAppIcon;

            // Top Panel holding the 1920x462 live interactive screen stage
            stageOuterPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 350,
                BackColor = Color.FromArgb(4, 6, 14),
                Padding = new Padding(12, 6, 12, 10)
            };

            stageHeaderPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.Transparent
            };

            stageInfoLabel = new Label
            {
                Text = "ðŸ–¥ï¸  LIVE 1920 Ã— 462 SCREEN STAGE  Â·  Interactive Viewport (Click, Scroll & Type Directly Inside Screen)",
                ForeColor = Color.FromArgb(0, 242, 254),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(4, 5)
            };

            btnSaveScreen = CreateStageHeaderButton("ðŸ’¾ Save to Screen", 140);
            btnSaveScreen.BackColor = Color.FromArgb(12, 110, 78);
            btnSaveScreen.FlatAppearance.BorderColor = Color.FromArgb(16, 185, 129);
            btnSaveScreen.Click += (s, e) =>
            {
                SaveFullSettingsToDisk();
                btnSaveScreen.Text = "âœ… Saved!";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (s2, e2) => { btnSaveScreen.Text = "ðŸ’¾ Save to Screen"; t.Stop(); t.Dispose(); };
                t.Start();
            };

            var btnUploadMedia = CreateStageHeaderButton("ðŸ“¤ Upload Video / GIF / Image", 195);
            btnUploadMedia.BackColor = Color.FromArgb(0, 115, 165);
            btnUploadMedia.Click += (s, e) =>
            {
                bool autoConv = fullSavedSettings?.Value<bool?>("autoConvertUpload") ?? true;
                string convMode = fullSavedSettings?.Value<string>("convertMode") ?? "stretch";
                PickLocalMediaFiles(autoConv, convMode);
            };

            btnRotateScreen = CreateStageHeaderButton(
                screenRotation == 180 ? "ðŸ”„ Flip Screen (180Â°)" : $"ðŸ”„ Flip Screen ({screenRotation}Â°)", 155);
            btnRotateScreen.Click += (s, e) =>
            {
                screenRotation = (screenRotation == 180) ? 0 : 180;
                btnRotateScreen.Text = screenRotation == 180 ? "ðŸ”„ Flip Screen (180Â°)" : "ðŸ”„ Flip Screen (0Â°)";
                SaveFullSettingsToDisk();
                SendEventToStudio(new { type = "rotationChanged", rotation = screenRotation });
            };

            var btnBackToAnim = CreateStageHeaderButton("âœ¨ Built-In Themes", 138);
            btnBackToAnim.Click += (s, e) => NavigateToScreenEngine();

            var btnPopout = CreateStageHeaderButton("ðŸ—— 1:1 Overlay", 118);
            btnPopout.Click += (s, e) => TogglePopoutPreview();

            var btnTray = CreateStageHeaderButton("ðŸ“¥ Send to Tray", 125);
            btnTray.BackColor = Color.FromArgb(88, 28, 135);
            btnTray.FlatAppearance.BorderColor = Color.FromArgb(168, 85, 247);
            btnTray.Click += (s, e) => MinimizeToTray();

            stageHeaderPanel.Controls.Add(stageInfoLabel);
            stageHeaderPanel.Controls.Add(btnSaveScreen);
            stageHeaderPanel.Controls.Add(btnUploadMedia);
            stageHeaderPanel.Controls.Add(btnRotateScreen);
            stageHeaderPanel.Controls.Add(btnBackToAnim);
            stageHeaderPanel.Controls.Add(btnPopout);
            stageHeaderPanel.Controls.Add(btnTray);
            stageHeaderPanel.Resize += (s, e) =>
            {
                btnTray.Location = new Point(stageHeaderPanel.Width - btnTray.Width - 4, 2);
                btnPopout.Location = new Point(btnTray.Left - btnPopout.Width - 6, 2);
                btnBackToAnim.Location = new Point(btnPopout.Left - btnBackToAnim.Width - 6, 2);
                btnRotateScreen.Location = new Point(btnBackToAnim.Left - btnRotateScreen.Width - 6, 2);
                btnUploadMedia.Location = new Point(btnRotateScreen.Left - btnUploadMedia.Width - 6, 2);
                btnSaveScreen.Location = new Point(btnUploadMedia.Left - btnSaveScreen.Width - 6, 2);
            };

            AllowDrop = true;
            DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    bool autoConv = fullSavedSettings?.Value<bool?>("autoConvertUpload") ?? true;
                    string convMode = fullSavedSettings?.Value<string>("convertMode") ?? "stretch";
                    AddMediaFilesToScreen(files, autoConv, convMode);
                }
            };

            stageViewportHost = new Panel
            {
                BackColor = Color.Black,
                Location = new Point(12, 38),
                Size = new Size(1280, 308)
            };

            screenWebView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.Black
            };
            stageViewportHost.Controls.Add(screenWebView);

            stageOuterPanel.Controls.Add(stageViewportHost);
            stageOuterPanel.Controls.Add(stageHeaderPanel);

            // Thin glowing divider line
            var divider = new Panel
            {
                Dock = DockStyle.Top,
                Height = 2,
                BackColor = Color.FromArgb(30, 58, 95)
            };

            // Bottom Control Deck WebView2
            studioWebView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(6, 9, 19)
            };

            Controls.Add(studioWebView);
            Controls.Add(divider);
            Controls.Add(stageOuterPanel);

            UpdateStageAspectLayout();
        }

        private Button CreateStageHeaderButton(string text, int width)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(width, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 26, 48),
                ForeColor = Color.FromArgb(225, 238, 255),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(0, 180, 216);
            btn.FlatAppearance.BorderSize = 1;
            return btn;
        }

        private void UpdateStageAspectLayout()
        {
            if (stageOuterPanel == null || stageViewportHost == null) return;
            if (isHiddenInTray || WindowState == FormWindowState.Minimized) return;

            int outerW = stageOuterPanel.ClientSize.Width;
            int outerH = stageOuterPanel.ClientSize.Height;

            // Immunity against fullscreen games / display mode switches shrinking desktop bounds
            if (outerW < 640 || outerH < 120)
            {
                ApplyScreenWebViewZoom(lastValidStageWidth);
                return;
            }

            int availW = Math.Max(640, outerW - 24);
            int availH = Math.Max(154, outerH - 44);

            double targetRatio = (double)LCD_WIDTH / LCD_HEIGHT; // 4.155844
            int boxW = availW;
            int boxH = (int)Math.Round(boxW / targetRatio);

            if (boxH > availH)
            {
                boxH = availH;
                boxW = (int)Math.Round(boxH * targetRatio);
            }

            int x = (outerW - boxW) / 2;
            int y = 36 + (availH - boxH) / 2;

            lastValidStageWidth = boxW;
            stageViewportHost.Bounds = new Rectangle(x, y, boxW, boxH);
            ApplyScreenWebViewZoom(boxW);
        }

        private void ApplyScreenWebViewZoom(int boxW)
        {
            try
            {
                if (screenWebView != null && screenWebView.CoreWebView2 != null)
                {
                    double baseScale = (double)Math.Max(640, boxW) / LCD_WIDTH;
                    double effectiveScale = (isEngineModeTarget || IsOnBuiltInScreenEngine())
                        ? baseScale
                        : (baseScale * Math.Max(0.25, Math.Min(2.5, userWebZoom)));
                    screenWebView.ZoomFactor = Math.Max(0.15, Math.Min(3.0, effectiveScale));
                }
            }
            catch { }
        }

        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open TL-M10 Vision Studio", null, (s, e) => RestoreFromTray());
            trayMenu.Items.Add("Save Current Settings to Screen", null, (s, e) => SaveFullSettingsToDisk());
            trayMenu.Items.Add("Toggle 1:1 1920Ã—462 Desktop Overlay", null, (s, e) => TogglePopoutPreview());
            trayMenu.Items.Add("Reconnect USB LCD Driver", null, (s, e) => usbTransport.EnsureConnected(true));
            trayMenu.Items.Add(new ToolStripSeparator());

            trayStartWithWindowsItem = new ToolStripMenuItem("ðŸš€ Start with Windows")
            {
                Checked = IsStartWithWindowsEnabled(),
                CheckOnClick = true
            };
            trayStartWithWindowsItem.Click += (s, e) =>
            {
                bool startMin = fullSavedSettings?.Value<bool?>("startMinimizedOnBoot") ?? true;
                SetStartWithWindows(trayStartWithWindowsItem.Checked, startMin);
            };
            trayMenu.Items.Add(trayStartWithWindowsItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit Vision Studio", null, (s, e) =>
            {
                isHiddenInTray = false;
                Application.Exit();
            });

            trayIcon = new NotifyIcon
            {
                Text = "TL-M10 Vision Studio (1920Ã—462)",
                Icon = customAppIcon,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
            };
            trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private async Task InitializeStudioAsync()
        {
            try
            {
                TopMost = true;
                Activate();
                BringToFront();
                TopMost = false;

                // 1. Start USB Transport, HWiNFO service, Audio Loopback & Live Market Data Service immediately
                usbTransport.EnsureConnected(true);
                telemetryReader.StartBackgroundHelperIfNeeded();
                audioCapture.Start();
                marketService.OnMarketDataUpdated += async (marketJsonObj) =>
                {
                    await PushMarketDataToScreenAndStudioAsync(marketJsonObj);
                };
                marketService.Start();

                // 2. Start Frame Capture & Telemetry Timers
                captureTimer = new System.Windows.Forms.Timer { Interval = Math.Max(16, 1000 / Math.Max(5, targetFps)) };
                captureTimer.Tick += async (s, e) => await CaptureAndStreamFrameAsync();
                captureTimer.Start();

                telemetryTimer = new System.Windows.Forms.Timer { Interval = 55 };
                telemetryTimer.Tick += async (s, e) => await PushLiveTelemetryAndAudioAsync();
                telemetryTimer.Start();

                // 3. Initialize both WebView2 controls using separate UserDataFolders and autoplay policy
                string screenUdf = Path.Combine(baseDir, "WV2_Screen");
                string studioUdf = Path.Combine(baseDir, "WV2_Studio");

                var envOpts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                var screenEnv = await CoreWebView2Environment.CreateAsync(null, screenUdf, envOpts);
                var studioEnv = await CoreWebView2Environment.CreateAsync(null, studioUdf, envOpts);

                await screenWebView.EnsureCoreWebView2Async(screenEnv);
                await studioWebView.EnsureCoreWebView2Async(studioEnv);

                // Configure Screen Stage WebView2 (1920x462)
                ConfigureWebViewSettings(screenWebView.CoreWebView2);
                screenWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "vision.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);

                // Restore any previously mapped local media folders so saved playlists work after restart
                if (fullSavedSettings?["mappedFolders"] is JObject savedFolders)
                {
                    foreach (var prop in savedFolders.Properties())
                    {
                        string dir = prop.Name;
                        string hostName = prop.Value.ToString();
                        if (Directory.Exists(dir) && !string.IsNullOrEmpty(hostName))
                        {
                            mappedFolders[dir] = hostName;
                            screenWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                                hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
                            mediaHostIndex++;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(overlayJsContent))
                {
                    await screenWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(overlayJsContent);
                }

                screenWebView.CoreWebView2.NavigationStarting += (s, e) =>
                {
                    isScreenNavigating = true;
                    lastNavigationStart = DateTime.UtcNow;
                    if (!string.IsNullOrEmpty(e.Uri))
                    {
                        isEngineModeTarget = e.Uri.IndexOf("vision.local/screen.html", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                };

                screenWebView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    isScreenNavigating = false;
                    isEngineModeTarget = IsOnBuiltInScreenEngine();
                    ApplyScreenWebViewZoom(lastValidStageWidth);
                    await PushCurrentStateToScreenAsync();
                    if (marketService.LatestMarketData != null)
                    {
                        await PushMarketDataToScreenAndStudioAsync(marketService.LatestMarketData);
                    }
                    SendEventToStudio(new
                    {
                        type = "urlChanged",
                        url = screenWebView.Source != null ? screenWebView.Source.ToString() : ""
                    });
                };

                // Configure Studio Control Deck WebView2
                ConfigureWebViewSettings(studioWebView.CoreWebView2);
                studioWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "vision.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);
                studioWebView.CoreWebView2.WebMessageReceived += OnStudioWebMessageReceived;

                studioWebView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    try
                    {
                        if (fullSavedSettings != null && fullSavedSettings.Count > 0)
                        {
                            string savedJson = fullSavedSettings.ToString(Formatting.None);
                            await studioWebView.CoreWebView2.ExecuteScriptAsync(
                                $"if(window.__LOAD_SETTINGS__) window.__LOAD_SETTINGS__({savedJson});");
                        }
                        if (marketService.LatestMarketData != null)
                        {
                            await PushMarketDataToScreenAndStudioAsync(marketService.LatestMarketData);
                        }
                    }
                    catch { }
                };

                // Load initial pages (restore saved mode if web URL was active)
                string initialMode = fullSavedSettings?.Value<string>("activeScreenMode") ?? "animation";
                string savedWebUrl = fullSavedSettings?.Value<string>("webUrl") ?? "";
                if (initialMode == "web" && !string.IsNullOrWhiteSpace(savedWebUrl) && !savedWebUrl.Contains("vision.local/screen.html"))
                {
                    isEngineModeTarget = false;
                    screenWebView.CoreWebView2.Navigate(savedWebUrl);
                    stageInfoLabel.Text = "ðŸŒ  LIVE WEB CAST (1920 Ã— 462)  Â·  " + savedWebUrl;
                }
                else
                {
                    isEngineModeTarget = true;
                    screenWebView.CoreWebView2.Navigate("https://vision.local/screen.html");
                }

                studioWebView.CoreWebView2.Navigate("https://vision.local/studio.html");
                UpdateStageAspectLayout();

                if (isAutoStartLaunch)
                {
                    bool startMin = fullSavedSettings?.Value<bool?>("startMinimizedOnBoot") ?? true;
                    if (startMin)
                    {
                        MinimizeToTray();
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogError("InitializeStudioAsync", ex);
                stageInfoLabel.Text = "âš ï¸ Init Notice: " + ex.Message;
            }
        }

        private async Task PushMarketDataToScreenAndStudioAsync(JObject marketData)
        {
            if (marketData == null) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(async () => await PushMarketDataToScreenAndStudioAsync(marketData)));
                    return;
                }

                string json = marketData.ToString(Formatting.None);
                if (screenWebView?.CoreWebView2 != null && IsOnBuiltInScreenEngine())
                {
                    await screenWebView.CoreWebView2.ExecuteScriptAsync(
                        $"if(window.__TLM10_MARKET_DATA__) window.__TLM10_MARKET_DATA__({json});");
                }
                SendEventToStudio(new
                {
                    type = "marketData",
                    data = marketData
                });
            }
            catch { }
        }

        private static void ConfigureWebViewSettings(CoreWebView2 core)
        {
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
        }

        private async void OnStudioWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;

                JObject msg = JObject.Parse(raw);
                string cmd = msg.Value<string>("cmd") ?? "";

                switch (cmd)
                {
                    case "saveSettings":
                        {
                            if (msg["settings"] is JObject stObj)
                            {
                                foreach (var prop in stObj.Properties())
                                {
                                    fullSavedSettings[prop.Name] = prop.Value;
                                }
                                SaveFullSettingsToDisk();
                            }
                            break;
                        }

                    case "updateMarketSymbols":
                        {
                            string psx = msg.Value<string>("psxSymbols");
                            string us = msg.Value<string>("usSymbols");
                            string crypto = msg.Value<string>("cryptoSymbols");
                            double? pkr = msg.Value<double?>("usdToPkr");
                            marketService.UpdateSymbols(psx, us, crypto, pkr);
                            _ = Task.Run(() => marketService.FetchAllNowAsync());
                            break;
                        }

                    case "refreshMarketData":
                        {
                            _ = Task.Run(() => marketService.FetchAllNowAsync());
                            break;
                        }

                    case "navigateWeb":
                        {
                            string url = msg.Value<string>("url");
                            double? wz = msg.Value<double?>("webZoom");
                            if (wz.HasValue && wz.Value > 0.1) userWebZoom = wz.Value;
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                isEngineModeTarget = url.IndexOf("vision.local/screen.html", StringComparison.OrdinalIgnoreCase) >= 0;
                                isScreenNavigating = true;
                                lastNavigationStart = DateTime.UtcNow;
                                screenWebView.CoreWebView2.Navigate(url);
                                stageInfoLabel.Text = "ðŸŒ  LIVE WEB CAST (1920 Ã— 462)  Â·  " + url;
                                ApplyScreenWebViewZoom(lastValidStageWidth);
                                SaveFullSettingsToDisk();
                            }
                            break;
                        }

                    case "webBack":
                        if (screenWebView.CanGoBack) screenWebView.GoBack();
                        break;

                    case "webForward":
                        if (screenWebView.CanGoForward) screenWebView.GoForward();
                        break;

                    case "webReload":
                        screenWebView.Reload();
                        break;

                    case "switchToScreenEngine":
                        NavigateToScreenEngine();
                        break;

                    case "updateScreenEngine":
                        {
                            bool ensureLoaded = msg.Value<bool?>("ensureEngineLoaded") ?? false;
                            if (msg["config"] is JObject cfg)
                            {
                                currentScreenEngineConfig = cfg;
                            }
                            if (ensureLoaded)
                            {
                                isEngineModeTarget = true;
                            }
                            if (ensureLoaded && !IsOnBuiltInScreenEngine())
                            {
                                NavigateToScreenEngine();
                            }
                            else
                            {
                                ApplyScreenWebViewZoom(lastValidStageWidth);
                                await PushCurrentStateToScreenAsync();
                            }
                            SaveFullSettingsToDisk();
                            break;
                        }

                    case "updateOverlayState":
                        {
                            if (msg["state"] is JObject st)
                            {
                                currentOverlayState = st;
                                double? wz = st.Value<double?>("webZoom");
                                if (wz.HasValue && wz.Value > 0.1)
                                {
                                    userWebZoom = wz.Value;
                                    ApplyScreenWebViewZoom(lastValidStageWidth);
                                }
                                await PushCurrentStateToScreenAsync();
                            }
                            break;
                        }

                    case "updateHardwareConfig":
                        {
                            screenRotation = msg.Value<int?>("rotation") ?? 180;
                            screenBrightness = msg.Value<int?>("brightness") ?? 100;
                            int newFps = msg.Value<int?>("fps") ?? 25;
                            int newQuality = msg.Value<int?>("quality") ?? 88;

                            if (btnRotateScreen != null)
                            {
                                btnRotateScreen.Text = screenRotation == 180 ? "ðŸ”„ Flip Screen (180Â°)" : $"ðŸ”„ Flip Screen ({screenRotation}Â°)";
                            }

                            if (newFps != targetFps && newFps >= 5 && newFps <= 60)
                            {
                                targetFps = newFps;
                                if (captureTimer != null)
                                    captureTimer.Interval = Math.Max(16, 1000 / targetFps);
                            }

                            if (newQuality != jpegQuality && newQuality >= 30 && newQuality <= 100)
                            {
                                jpegQuality = newQuality;
                                jpegParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);
                            }
                            SaveFullSettingsToDisk();
                            break;
                        }

                    case "pickMediaFiles":
                        {
                            bool autoConvert = msg.Value<bool?>("autoConvert") ?? true;
                            string convMode = msg.Value<string>("convertMode") ?? "stretch";
                            PickLocalMediaFiles(autoConvert, convMode);
                            break;
                        }

                    case "convertExistingMedia":
                        {
                            int? replaceIdx = msg.Value<int?>("playlistIndex");
                            string fp = msg.Value<string>("filePath") ?? "";
                            string mUrl = msg.Value<string>("mediaUrl") ?? "";
                            string convMode = msg.Value<string>("convertMode") ?? "stretch";
                            await ConvertExistingMediaAsync(fp, mUrl, convMode, replaceIdx);
                            break;
                        }

                    case "convertBatchMedia":
                        {
                            string convMode = msg.Value<string>("convertMode") ?? "stretch";
                            var itemsArr = msg["items"] as JArray;
                            await ConvertBatchMediaAsync(itemsArr, convMode);
                            break;
                        }

                    case "setStartWithWindows":
                        {
                            bool enabled = msg.Value<bool?>("enabled") ?? false;
                            bool startMin = msg.Value<bool?>("startMinimized") ?? true;
                            SetStartWithWindows(enabled, startMin);
                            break;
                        }

                    case "restartUsbDriver":
                        usbTransport.EnsureConnected(true);
                        break;

                    case "togglePopoutPreview":
                        TogglePopoutPreview();
                        break;

                    case "minimizeToTray":
                        MinimizeToTray();
                        break;
                }
            }
            catch { }
        }

        private bool IsOnBuiltInScreenEngine()
        {
            if (screenWebView?.Source == null) return false;
            return screenWebView.Source.ToString().IndexOf("vision.local/screen.html", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void NavigateToScreenEngine()
        {
            if (screenWebView?.CoreWebView2 == null) return;
            isEngineModeTarget = true;
            isScreenNavigating = true;
            lastNavigationStart = DateTime.UtcNow;
            screenWebView.CoreWebView2.Navigate("https://vision.local/screen.html");
            ApplyScreenWebViewZoom(lastValidStageWidth);
            stageInfoLabel.Text = "âœ¨  BUILT-IN 60 FPS SCREEN ENGINE (1920 Ã— 462)  Â·  Live Market Themes, Animations & Media";
        }

        private async Task PushCurrentStateToScreenAsync()
        {
            if (screenWebView?.CoreWebView2 == null) return;
            try
            {
                if (currentScreenEngineConfig != null && IsOnBuiltInScreenEngine())
                {
                    string cfgJson = currentScreenEngineConfig.ToString(Formatting.None);
                    var t1 = screenWebView.CoreWebView2.ExecuteScriptAsync(
                        $"if(window.__TLM10_SCREEN_CONFIG__) window.__TLM10_SCREEN_CONFIG__({cfgJson});");
                    await Task.WhenAny(t1, Task.Delay(500));
                }

                if (currentOverlayState != null)
                {
                    var payload = new JObject
                    {
                        ["state"] = currentOverlayState
                    };
                    string json = payload.ToString(Formatting.None);
                    var t2 = screenWebView.CoreWebView2.ExecuteScriptAsync(
                        $"if(window.__TLM10_UPDATE__) window.__TLM10_UPDATE__({json});");
                    await Task.WhenAny(t2, Task.Delay(500));
                }
            }
            catch { }
        }

        private int uiTelemetryCounter = 0;

        private async Task PushLiveTelemetryAndAudioAsync()
        {
            if (isPushingTelemetry || screenWebView?.CoreWebView2 == null) return;
            if (isScreenNavigating && (DateTime.UtcNow - lastNavigationStart).TotalMilliseconds < 1500) return;

            isPushingTelemetry = true;
            try
            {
                TelemetryData telem = telemetryReader.ReadLatest();
                double[] spectrum = audioCapture.GetSpectrum64();

                var screenPayload = new
                {
                    telemetry = telem,
                    spectrum = spectrum
                };
                string screenJson = JsonConvert.SerializeObject(screenPayload);
                var execTask = screenWebView.CoreWebView2.ExecuteScriptAsync(
                    $"if(window.__TLM10_UPDATE__) window.__TLM10_UPDATE__({screenJson});");
                await Task.WhenAny(execTask, Task.Delay(400));

                uiTelemetryCounter++;
                if (uiTelemetryCounter % 5 == 0)
                {
                    SendEventToStudio(new
                    {
                        type = "telemetry",
                        telemetry = telem,
                        fps = measuredFps,
                        kb = lastFrameKb,
                        usbConnected = usbTransport.IsActive,
                        usbStatus = usbTransport.StatusText
                    });
                }
            }
            catch { }
            finally
            {
                isPushingTelemetry = false;
            }
        }

        private void SendEventToStudio(object ev)
        {
            if (studioWebView?.CoreWebView2 == null) return;
            try
            {
                string json = JsonConvert.SerializeObject(ev);
                studioWebView.CoreWebView2.ExecuteScriptAsync(
                    $"if(window.__HOST_EVENT__) window.__HOST_EVENT__({json});");
            }
            catch { }
        }

        private void PickLocalMediaFiles(bool autoConvert = true, string convertMode = "stretch")
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select Videos, Animated GIFs, or Images for TL-M10 Vision (Auto-Resize to 1920 Ã— 462)";
                dlg.Filter = "All Supported Media (*.mp4;*.webm;*.mov;*.mkv;*.avi;*.gif;*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.gif;*.png;*.jpg;*.jpeg;*.webp;*.bmp|Video & GIF Files (*.mp4;*.webm;*.mov;*.mkv;*.avi;*.gif)|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.gif|Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp";
                dlg.Multiselect = true;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    AddMediaFilesToScreen(dlg.FileNames, autoConvert, convertMode);
                }
            }
        }

        private string FindFfmpegPath()
        {
            string localFfmpeg = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(localFfmpeg)) return localFfmpeg;

            string parentFfmpeg = Path.GetFullPath(Path.Combine(baseDir, "..", "bin", "ffmpeg.exe"));
            if (File.Exists(parentFfmpeg)) return parentFfmpeg;

            string trccFfmpeg = @"C:\Program Files\TRCCCAP\ffmpeg.exe";
            if (File.Exists(trccFfmpeg)) return trccFfmpeg;

            return null;
        }

        private async Task<string> ConvertMediaFileTo1920x462Async(string inputPath, string convertMode)
        {
            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath)) return inputPath;

            try
            {
                string convertedDir = Path.Combine(baseDir, "ConvertedMedia");
                Directory.CreateDirectory(convertedDir);

                string ext = Path.GetExtension(inputPath).ToLowerInvariant();
                string baseName = Path.GetFileNameWithoutExtension(inputPath);
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    baseName = baseName.Replace(c, '_');
                }
                string safeMode = string.IsNullOrEmpty(convertMode) ? "stretch" : convertMode.ToLowerInvariant();

                bool isStaticImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
                if (isStaticImage)
                {
                    string outImgPath = Path.Combine(convertedDir, $"{baseName}_1920x462_{safeMode}.jpg");
                    SendEventToStudio(new { type = "converterStatus", message = $"â³ Resizing image \"{Path.GetFileName(inputPath)}\" to 1920Ã—462 ({safeMode})..." });

                    await Task.Run(() =>
                    {
                        using (var srcImg = Image.FromFile(inputPath))
                        using (var destBmp = new Bitmap(LCD_WIDTH, LCD_HEIGHT, PixelFormat.Format24bppRgb))
                        using (var g = Graphics.FromImage(destBmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.Clear(Color.Black);

                            if (safeMode == "blur-fill")
                            {
                                // Draw darkened background fill + sharp centered aspect-correct foreground
                                using (var smallBmp = new Bitmap(96, 24))
                                using (var gs = Graphics.FromImage(smallBmp))
                                {
                                    gs.InterpolationMode = InterpolationMode.Bilinear;
                                    gs.DrawImage(srcImg, new Rectangle(0, 0, 96, 24));
                                    var cm = new ColorMatrix(new float[][]
                                    {
                                        new float[] { 0.45f, 0, 0, 0, 0 },
                                        new float[] { 0, 0.45f, 0, 0, 0 },
                                        new float[] { 0, 0, 0.45f, 0, 0 },
                                        new float[] { 0, 0, 0, 1, 0 },
                                        new float[] { 0, 0, 0, 0, 1 }
                                    });
                                    using (var ia = new ImageAttributes())
                                    {
                                        ia.SetColorMatrix(cm);
                                        g.DrawImage(smallBmp, new Rectangle(0, 0, LCD_WIDTH, LCD_HEIGHT), 0, 0, 96, 24, GraphicsUnit.Pixel, ia);
                                    }
                                }
                                double scale = Math.Min((double)LCD_WIDTH / srcImg.Width, (double)LCD_HEIGHT / srcImg.Height);
                                int fgW = (int)Math.Round(srcImg.Width * scale);
                                int fgH = (int)Math.Round(srcImg.Height * scale);
                                int fgX = (LCD_WIDTH - fgW) / 2;
                                int fgY = (LCD_HEIGHT - fgH) / 2;
                                g.DrawImage(srcImg, new Rectangle(fgX, fgY, fgW, fgH));
                            }
                            else if (safeMode == "triple")
                            {
                                int tileW = LCD_WIDTH / 3;
                                for (int i = 0; i < 3; i++)
                                {
                                    g.DrawImage(srcImg, new Rectangle(i * tileW, 0, tileW, LCD_HEIGHT));
                                }
                            }
                            else if (safeMode == "cover")
                            {
                                double scale = Math.Max((double)LCD_WIDTH / srcImg.Width, (double)LCD_HEIGHT / srcImg.Height);
                                int crW = (int)Math.Round(srcImg.Width * scale);
                                int crH = (int)Math.Round(srcImg.Height * scale);
                                int crX = (LCD_WIDTH - crW) / 2;
                                int crY = (LCD_HEIGHT - crH) / 2;
                                g.DrawImage(srcImg, new Rectangle(crX, crY, crW, crH));
                            }
                            else
                            {
                                // Default "stretch": Exact 1920x462 full-screen resize so nothing is cropped!
                                g.DrawImage(srcImg, new Rectangle(0, 0, LCD_WIDTH, LCD_HEIGHT));
                            }

                            var qParams = new EncoderParameters(1);
                            qParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                            destBmp.Save(outImgPath, jpegEncoder, qParams);
                        }
                    });

                    SendEventToStudio(new { type = "converterStatus", message = $"âœ… Converted & Resized Image to 1920Ã—462: {Path.GetFileName(outImgPath)}" });
                    return outImgPath;
                }

                // Video, Animated GIF, or WebP -> Convert to native 1920x462 MP4 or JPG via bundled FFmpeg
                string ffmpegExe = FindFfmpegPath();
                if (!string.IsNullOrEmpty(ffmpegExe))
                {
                    bool isWebp = ext == ".webp";
                    string outExt = isWebp ? ".jpg" : ".mp4";
                    string outMediaPath = Path.Combine(convertedDir, $"{baseName}_1920x462_{safeMode}{outExt}");

                    SendEventToStudio(new
                    {
                        type = "converterStatus",
                        message = $"â³ Converting \"{Path.GetFileName(inputPath)}\" to native 1920Ã—462 ({safeMode}) via built-in FFmpeg..."
                    });

                    string filterArg;
                    if (safeMode == "blur-fill")
                    {
                        filterArg = "-filter_complex \"[0:v]split=2[bg][fg];[bg]scale=1920:462,boxblur=24:4,eq=brightness=-0.16[b];[fg]scale=1920:462:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2[f];[b][f]overlay=(W-w)/2:(H-h)/2,setsar=1:1,format=yuv420p\"";
                    }
                    else if (safeMode == "triple")
                    {
                        filterArg = "-filter_complex \"[0:v]scale=640:462,setsar=1:1,split=3[a][b][c];[a][b][c]hstack=inputs=3,format=yuv420p\"";
                    }
                    else if (safeMode == "cover")
                    {
                        filterArg = "-vf \"scale=1920:462:force_original_aspect_ratio=increase,crop=1920:462,setsar=1:1,format=yuv420p\"";
                    }
                    else
                    {
                        filterArg = "-vf \"scale=1920:462,setsar=1:1,format=yuv420p\"";
                    }

                    string args = isWebp
                        ? $"-nostdin -loglevel error -y -i \"{inputPath}\" {filterArg} -frames:v 1 -q:v 2 \"{outMediaPath}\""
                        : $"-nostdin -loglevel error -y -i \"{inputPath}\" {filterArg} -c:v libx264 -preset ultrafast -crf 20 -an -movflags +faststart \"{outMediaPath}\"";

                    bool ok = await Task.Run(() =>
                    {
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = ffmpegExe,
                                Arguments = args,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardError = false,
                                RedirectStandardOutput = false
                            };
                            using (var proc = Process.Start(psi))
                            {
                                if (proc == null) return false;
                                if (!proc.WaitForExit(120000))
                                {
                                    try { proc.Kill(); } catch { }
                                    return false;
                                }
                                return proc.ExitCode == 0 && File.Exists(outMediaPath) && new FileInfo(outMediaPath).Length > 256;
                            }
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (ok)
                    {
                        SendEventToStudio(new
                        {
                            type = "converterStatus",
                            message = $"âœ… Converted & Resized to 1920Ã—462: {Path.GetFileName(outMediaPath)}"
                        });
                        return outMediaPath;
                    }
                    else
                    {
                        SendEventToStudio(new
                        {
                            type = "converterStatus",
                            message = $"âš ï¸ FFmpeg could not convert {Path.GetFileName(inputPath)} â€” using live hardware scaler."
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogError("ConvertMediaFileTo1920x462Async", ex);
            }

            return inputPath;
        }

        private string ResolveLocalPathFromMediaUrl(string filePath, string mediaUrl)
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) return filePath;
            if (!string.IsNullOrEmpty(mediaUrl))
            {
                try
                {
                    var uri = new Uri(mediaUrl);
                    string host = uri.Host;
                    string relName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                    foreach (var kv in mappedFolders)
                    {
                        if (string.Equals(kv.Value, host, StringComparison.OrdinalIgnoreCase))
                        {
                            string candidate = Path.Combine(kv.Key, relName);
                            if (File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private async Task ConvertExistingMediaAsync(string filePath, string mediaUrl, string convertMode, int? replaceIndex = null)
        {
            string targetFile = ResolveLocalPathFromMediaUrl(filePath, mediaUrl);

            if (!string.IsNullOrEmpty(targetFile) && File.Exists(targetFile))
            {
                AddMediaFilesToScreen(new[] { targetFile }, true, convertMode, replaceIndex);
            }
            else
            {
                PickLocalMediaFiles(true, convertMode);
            }
            await Task.CompletedTask;
        }

        private async Task ConvertBatchMediaAsync(JArray itemsArr, string convertMode)
        {
            if (itemsArr == null || itemsArr.Count == 0) return;
            int count = 0;
            foreach (var token in itemsArr)
            {
                if (token is JObject obj)
                {
                    int? idx = obj.Value<int?>("playlistIndex");
                    string fp = obj.Value<string>("filePath") ?? "";
                    string mUrl = obj.Value<string>("mediaUrl") ?? "";
                    string resolved = ResolveLocalPathFromMediaUrl(fp, mUrl);
                    if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                    {
                        count++;
                        AddMediaFilesToScreen(new[] { resolved }, true, convertMode, idx);
                        await Task.Delay(150);
                    }
                }
            }
            SendEventToStudio(new
            {
                type = "converterStatus",
                message = $"âœ… Batch Converted {count} playlist item(s) to native 1920Ã—462 ({convertMode})!"
            });
        }

        private async void AddMediaFilesToScreen(string[] fileNames, bool autoConvert = true, string convertMode = "stretch", int? replaceIndex = null)
        {
            if (fileNames == null || fileNames.Length == 0 || screenWebView?.CoreWebView2 == null) return;

            var added = new List<object>();
            string lastMediaUrl = null;
            string lastMediaType = "image";
            string lastFileName = "";
            string safeMode = string.IsNullOrEmpty(convertMode) ? "stretch" : convertMode;

            foreach (string rawPath in fileNames)
            {
                if (!File.Exists(rawPath)) continue;

                string finalPath = autoConvert
                    ? await ConvertMediaFileTo1920x462Async(rawPath, safeMode)
                    : rawPath;

                string dir = Path.GetDirectoryName(finalPath);
                string fileName = Path.GetFileName(finalPath);
                string ext = Path.GetExtension(finalPath).ToLowerInvariant();

                if (!mappedFolders.TryGetValue(dir, out string hostName))
                {
                    hostName = $"media{mediaHostIndex++}.local";
                    mappedFolders[dir] = hostName;
                    screenWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
                }

                string mediaUrl = $"https://{hostName}/{Uri.EscapeDataString(fileName)}?v={DateTime.UtcNow.Ticks}";
                bool isVideo = ext == ".mp4" || ext == ".webm" || ext == ".mov" || ext == ".mkv" || ext == ".avi";

                lastMediaUrl = mediaUrl;
                lastMediaType = isVideo ? "video" : "image";
                lastFileName = fileName;

                added.Add(new
                {
                    name = fileName,
                    url = mediaUrl,
                    localPath = rawPath,
                    type = lastMediaType
                });
            }

            if (added.Count > 0)
            {
                // When converted to 1920x462, "stretch" displays the exact 1920x462 output 1:1 edge-to-edge
                string activeFit = autoConvert ? "stretch" : (safeMode == "triple" ? "stretch" : safeMode);
                currentScreenEngineConfig = JObject.FromObject(new
                {
                    screenMode = "media",
                    mediaUrl = lastMediaUrl,
                    mediaType = lastMediaType,
                    mediaFit = activeFit,
                    mediaZoom = 1.0,
                    mediaPanX = 0,
                    mediaPanY = 0
                });

                stageInfoLabel.Text = "ðŸŽ¬  PLAYING 1920 Ã— 462 MEDIA  Â·  " + lastFileName;

                if (!IsOnBuiltInScreenEngine())
                {
                    NavigateToScreenEngine();
                }
                else
                {
                    await PushCurrentStateToScreenAsync();
                }

                SaveFullSettingsToDisk();

                SendEventToStudio(new
                {
                    type = "mediaAdded",
                    files = added,
                    fitMode = activeFit,
                    replaceIndex = replaceIndex
                });
            }
        }

        private async Task CaptureAndStreamFrameAsync()
        {
            if (screenWebView?.CoreWebView2 == null) return;

            // Watchdog: if a previous CapturePreviewAsync got orphaned during a cross-origin navigation, unstick it after 850ms
            if (isCapturingFrame)
            {
                if ((DateTime.UtcNow - lastCaptureStart).TotalMilliseconds > 850)
                {
                    isCapturingFrame = false;
                }
                else
                {
                    return;
                }
            }

            // Avoid calling CapturePreviewAsync in the middle of a cross-origin renderer process swap
            if (isScreenNavigating && (DateTime.UtcNow - lastNavigationStart).TotalMilliseconds < 900)
            {
                return;
            }

            isCapturingFrame = true;
            lastCaptureStart = DateTime.UtcNow;

            try
            {
                using (var previewStream = new MemoryStream())
                {
                    var captureTask = screenWebView.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Jpeg, previewStream);
                    var completed = await Task.WhenAny(captureTask, Task.Delay(700));
                    if (completed != captureTask)
                    {
                        return;
                    }
                    await captureTask;

                    if (previewStream.Length == 0) return;
                    previewStream.Position = 0;

                    using (var capturedImg = Image.FromStream(previewStream))
                    {
                        using (Graphics g = Graphics.FromImage(lcdBitmap))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.InterpolationMode = InterpolationMode.Bilinear;
                            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;

                            if (screenBrightness < 100)
                            {
                                float b = Math.Max(0.1f, screenBrightness / 100f);
                                var cm = new ColorMatrix(new float[][]
                                {
                                    new float[] { b, 0, 0, 0, 0 },
                                    new float[] { 0, b, 0, 0, 0 },
                                    new float[] { 0, 0, b, 0, 0 },
                                    new float[] { 0, 0, 0, 1, 0 },
                                    new float[] { 0, 0, 0, 0, 1 }
                                });
                                using (var ia = new ImageAttributes())
                                {
                                    ia.SetColorMatrix(cm);
                                    g.DrawImage(
                                        capturedImg,
                                        new Rectangle(0, 0, LCD_WIDTH, LCD_HEIGHT),
                                        0, 0, capturedImg.Width, capturedImg.Height,
                                        GraphicsUnit.Pixel, ia);
                                }
                            }
                            else
                            {
                                g.DrawImage(capturedImg, 0, 0, LCD_WIDTH, LCD_HEIGHT);
                            }
                        }

                        if (screenRotation == 180)
                        {
                            lcdBitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        }
                        else if (screenRotation == 360)
                        {
                            lcdBitmap.RotateFlip(RotateFlipType.Rotate180FlipX);
                        }
                        else if (screenRotation == 540)
                        {
                            lcdBitmap.RotateFlip(RotateFlipType.Rotate180FlipY);
                        }
                        else if (screenRotation == 90 || screenRotation == 270)
                        {
                            using (var tempCopy = (Bitmap)lcdBitmap.Clone())
                            using (Graphics gr = Graphics.FromImage(lcdBitmap))
                            {
                                gr.Clear(Color.Black);
                                gr.TranslateTransform(LCD_WIDTH / 2f, LCD_HEIGHT / 2f);
                                gr.RotateTransform(screenRotation);
                                gr.DrawImage(tempCopy, -LCD_HEIGHT / 2f, -LCD_HEIGHT / 2f, LCD_HEIGHT, LCD_HEIGHT);
                            }
                        }
                    }

                    // Update 1:1 Desktop Overlay Window if open
                    if (popoutForm != null && !popoutForm.IsDisposed && popoutForm.Visible)
                    {
                        popoutForm.UpdateFrame(lcdBitmap);
                    }

                    // Encode 1920x462 JPEG and send to Thermalright USB Screen
                    using (var jpgOut = new MemoryStream(131072))
                    {
                        lcdBitmap.Save(jpgOut, jpegEncoder, jpegParams);
                        byte[] jpgBytes = jpgOut.ToArray();
                        lastFrameKb = jpgBytes.Length / 1024;
                        usbTransport.Send1920x462Frame(jpgBytes);
                    }

                    frameCounter++;
                    double elapsedSec = (DateTime.UtcNow - lastFpsTick).TotalSeconds;
                    if (elapsedSec >= 1.0)
                    {
                        measuredFps = (int)Math.Round(frameCounter / elapsedSec);
                        frameCounter = 0;
                        lastFpsTick = DateTime.UtcNow;
                    }
                }
            }
            catch { }
            finally
            {
                isCapturingFrame = false;
            }
        }

        private void TogglePopoutPreview()
        {
            if (popoutForm == null || popoutForm.IsDisposed)
            {
                popoutForm = new PopoutPreviewForm(customAppIcon);
                popoutForm.Show();
            }
            else
            {
                popoutForm.Visible = !popoutForm.Visible;
            }
        }

        private void MinimizeToTray()
        {
            MinimizeToBackground(keepInTaskbar: false);
        }

        private void MinimizeToBackground(bool keepInTaskbar)
        {
            SaveCurrentValidWindowBounds();
            isHiddenInTray = true;
            lastMinimizeTime = DateTime.UtcNow;

            // Keep WindowState = Normal off-screen so WebView2 continues rendering & streaming 1920x462 at full FPS!
            WindowState = FormWindowState.Normal;
            Location = new Point(-32000, -32000);
            ShowInTaskbar = keepInTaskbar;

            // Deactivate our window so clicking the Windows Taskbar icon sends WM_ACTIVATE to restore it immediately
            try
            {
                IntPtr shellHwnd = GetShellWindow();
                if (shellHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(shellHwnd);
                }
            }
            catch { }

            if (!keepInTaskbar && trayIcon != null)
            {
                trayIcon.ShowBalloonTip(
                    2000,
                    "TL-M10 Vision Studio Active",
                    "Streaming 1920Ã—462 in the background. Click the tray icon to reopen.",
                    ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            isHiddenInTray = false;
            ShowInTaskbar = true;

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            // Ensure savedWindowLocation and savedWindowSize are valid and visible on at least one monitor
            int w = (savedWindowSize.Width >= 800) ? savedWindowSize.Width : 1460;
            int h = (savedWindowSize.Height >= 500) ? savedWindowSize.Height : 940;
            Point loc = savedWindowLocation;

            bool isVisibleOnAnyScreen = false;
            if (loc.X > -5000 && loc.Y > -5000)
            {
                var testRect = new Rectangle(loc.X + 80, loc.Y + 40, Math.Min(w, 400), Math.Min(h, 300));
                foreach (var scr in Screen.AllScreens)
                {
                    if (scr.WorkingArea.IntersectsWith(testRect))
                    {
                        isVisibleOnAnyScreen = true;
                        break;
                    }
                }
            }

            if (!isVisibleOnAnyScreen)
            {
                var wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1920, 1080);
                w = Math.Min(w, wa.Width - 40);
                h = Math.Min(h, wa.Height - 40);
                loc = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
                savedWindowLocation = loc;
                savedWindowSize = new Size(w, h);
            }

            Bounds = new Rectangle(loc, new Size(w, h));
            Show();
            TopMost = true;
            BringToFront();
            Activate();
            TopMost = false;
            UpdateStageAspectLayout();
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            SaveFullSettingsToDisk();
            captureTimer?.Stop();
            telemetryTimer?.Stop();
            marketService?.Dispose();
            audioCapture?.Dispose();
            usbTransport?.Dispose();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                    return codec;
            }
            return null;
        }
    }
}
