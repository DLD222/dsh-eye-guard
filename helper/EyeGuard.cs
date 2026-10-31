using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DshEyeGuard
{
    public static class Program
    {
        [STAThread]
        public static void Run()
        {
            var app = new EyeGuardApplication();
            app.Run();
        }

        [STAThread]
        private static void Main()
        {
            Run();
        }
    }

    internal sealed class EyeGuardApplication : Application
    {
        private Controller controller;

        public EyeGuardApplication()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            controller = new Controller();
            controller.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (controller != null)
            {
                controller.Dispose();
            }
            base.OnExit(e);
        }
    }

    internal sealed class Config
    {
        public int IntervalMinutes = 20;
        public int BreakSeconds = 20;
        public int WarningSeconds = 30;
        public int IdleResetMinutes = 5;
        public bool HighContrast;
        public bool ReduceMotion;
    }

    internal sealed class Stats
    {
        public string Date = "";
        public int Completed;
        public int Streak;
        public string LastCompletedDate = "";
    }

    internal sealed class Controller : IDisposable
    {
        private readonly DispatcherTimer timer;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly Config config = new Config();
        private readonly Stats stats = new Stats();
        private readonly List<OverlayWindow> overlays = new List<OverlayWindow>();
        private readonly LowLevelKeyboardHook keyboard = new LowLevelKeyboardHook();

        private StreamWriter output;
        private WinForms.NotifyIcon tray;
        private WinForms.ContextMenuStrip menu;
        private WinForms.ToolStripMenuItem headerItem;
        private WinForms.ToolStripMenuItem statsItem;
        private WinForms.ToolStripMenuItem intervalMenu;
        private WinForms.ToolStripMenuItem breakMenu;
        private WinForms.ToolStripMenuItem idleMenu;
        private WinForms.ToolStripMenuItem highContrastItem;
        private WinForms.ToolStripMenuItem reduceMotionItem;
        private WarningWindow warning;

        private DateTime nextBreakAt;
        private DateTime breakEndsAt;
        private DateTime lastStateSent = DateTime.MinValue;
        private DateTime lastTopmost = DateTime.MinValue;
        private DateTime lastFullscreenCheck = DateTime.MinValue;
        private DateTime escDownAt = DateTime.MinValue;

        private bool breakActive;
        private bool warningActive;
        private bool postponed;
        private bool fullscreen;
        private bool escDown;
        private bool idleResetActive;
        private bool disposed;
        private bool readySent;
        private string breakId = "";
        private string lastIconLabel = "";
        private string lastTrayText = "";

        private static readonly HashSet<string> WorkProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Code",
            "Code - Insiders",
            "CodeBuddy",
            "devenv",
            "idea64",
            "pycharm64",
            "webstorm64",
            "goland64",
            "clion64",
            "rider64",
            "sublime_text",
            "notepad++",
            "WindowsTerminal",
            "powershell",
            "pwsh",
            "cmd",
            "WINWORD",
            "EXCEL",
            "OUTLOOK",
            "Acrobat",
            "AcroRd32",
            "SumatraPDF",
            "foxitreader",
        };

        public Controller()
        {
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += OnTick;
        }

        public Config Config
        {
            get { return config; }
        }

        public void Start()
        {
            try
            {
                output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                output.AutoFlush = true;
            }
            catch
            {
                output = null;
            }

            CreateTray();
            keyboard.KeyChanged += OnGlobalKeyChanged;
            keyboard.ShouldBlock = ShouldBlockKey;
            keyboard.Install();

            nextBreakAt = DateTime.Now.AddMinutes(config.IntervalMinutes);
            timer.Start();
            StartInputLoop();
            SendReady();
            SendState();
            Log("Eye Guard helper started.");
        }

        private void StartInputLoop()
        {
            Task.Run(delegate
            {
                try
                {
                    var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string message = line.Trim();
                        if (message.Length == 0)
                        {
                            continue;
                        }
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { HandleMessage(message); }));
                    }
                }
                catch
                {
                    // The parent closed the pipe.
                }
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { Shutdown(); }));
            });
        }

        private void HandleMessage(string line)
        {
            Dictionary<string, object> message;
            try
            {
                message = serializer.Deserialize<Dictionary<string, object>>(line);
            }
            catch
            {
                Log("Ignoring malformed Host message.");
                return;
            }
            if (message == null)
            {
                return;
            }
            string type = GetString(message, "type", "");
            if (type == "init")
            {
                ApplyConfig(GetObject(message, "config"), true);
                ApplyStats(GetObject(message, "stats"));
                SendReady();
                SendState();
            }
            else if (type == "config")
            {
                ApplyConfig(GetObject(message, "config"), GetBool(message, "resetClock", false));
            }
            else if (type == "stats")
            {
                ApplyStats(GetObject(message, "stats"));
                UpdateTray(DateTime.Now);
                SendState();
            }
            else if (type == "break-now")
            {
                if (!breakActive)
                {
                    if (IsFullscreenWindow())
                    {
                        postponed = true;
                        fullscreen = true;
                        HideWarning();
                        SendState();
                    }
                    else
                    {
                        StartBreak();
                    }
                }
            }
            else if (type == "shutdown")
            {
                Shutdown();
            }
        }

        private void ApplyConfig(Dictionary<string, object> values, bool resetClock)
        {
            if (values == null)
            {
                return;
            }
            config.IntervalMinutes = Clamp(GetInt(values, "intervalMinutes", config.IntervalMinutes), 1, 120);
            config.BreakSeconds = Clamp(GetInt(values, "breakSeconds", config.BreakSeconds), 5, 300);
            config.WarningSeconds = Clamp(GetInt(values, "warningSeconds", config.WarningSeconds), 0, 120);
            config.IdleResetMinutes = Clamp(GetInt(values, "idleResetMinutes", config.IdleResetMinutes), 1, 60);
            config.HighContrast = GetBool(values, "highContrast", config.HighContrast);
            config.ReduceMotion = GetBool(values, "reduceMotion", config.ReduceMotion);
            UpdateMenuChecks();
            if (resetClock && !breakActive)
            {
                ResetFocus(DateTime.Now);
            }
            if (breakActive)
            {
                foreach (OverlayWindow overlay in overlays)
                {
                    overlay.ApplyConfig(config);
                }
            }
        }

        private void ApplyStats(Dictionary<string, object> values)
        {
            if (values == null)
            {
                return;
            }
            stats.Date = GetString(values, "date", stats.Date);
            stats.Completed = GetInt(values, "completed", stats.Completed);
            stats.Streak = GetInt(values, "streak", stats.Streak);
            stats.LastCompletedDate = GetString(values, "lastCompletedDate", stats.LastCompletedDate);
            if (stats.Completed < 0)
            {
                stats.Completed = 0;
            }
            if (stats.Streak < 0)
            {
                stats.Streak = 0;
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (disposed)
            {
                return;
            }
            DateTime now = DateTime.Now;
            if (breakActive)
            {
                TickBreak(now);
                return;
            }

            if (postponed)
            {
                if ((now - lastFullscreenCheck).TotalSeconds >= 1)
                {
                    lastFullscreenCheck = now;
                    fullscreen = IsFullscreenWindow();
                }
                if (!fullscreen)
                {
                    postponed = false;
                    StartBreak();
                    return;
                }
            }

            if ((now - lastFullscreenCheck).TotalSeconds >= 1)
            {
                lastFullscreenCheck = now;
                fullscreen = IsFullscreenWindow();
            }

            if (config.IdleResetMinutes > 0)
            {
                uint idle = IdleMilliseconds();
                if (idle >= (uint)(config.IdleResetMinutes * 60000))
                {
                    if (!idleResetActive)
                    {
                        idleResetActive = true;
                        ResetFocus(now);
                        Log("Idle detected; focus timer reset.");
                    }
                    UpdateTray(now);
                    return;
                }
                if (idleResetActive)
                {
                    idleResetActive = false;
                    ResetFocus(now);
                }
            }

            TimeSpan remaining = nextBreakAt - now;
            if (remaining <= TimeSpan.Zero)
            {
                HideWarning();
                if (IsFullscreenWindow())
                {
                    postponed = true;
                    fullscreen = true;
                    SendState();
                }
                else
                {
                    StartBreak();
                }
                return;
            }

            if (warningActive && fullscreen)
            {
                HideWarning();
            }
            else if (config.WarningSeconds > 0 && remaining.TotalSeconds <= config.WarningSeconds)
            {
                ShowWarning(remaining);
            }
            else if (warningActive)
            {
                HideWarning();
            }

            UpdateTray(now);
            if ((now - lastStateSent).TotalSeconds >= 5)
            {
                SendState();
            }
        }

        private void TickBreak(DateTime now)
        {
            TimeSpan remaining = breakEndsAt - now;
            if (remaining <= TimeSpan.Zero)
            {
                EndBreak(true);
                return;
            }

            double held = escDown ? (now - escDownAt).TotalSeconds : 0;
            foreach (OverlayWindow overlay in overlays)
            {
                overlay.UpdateBreak(remaining, config.BreakSeconds, escDown, held, config);
            }

            if (escDown && held >= 3)
            {
                EndBreak(false);
                return;
            }

            if ((now - lastTopmost).TotalSeconds >= 2)
            {
                lastTopmost = now;
                foreach (OverlayWindow overlay in overlays)
                {
                    overlay.ReassertTopmost();
                }
            }

            if ((now - lastStateSent).TotalSeconds >= 1)
            {
                SendState();
            }
            UpdateTray(now);
        }

        private void ResetFocus(DateTime now)
        {
            nextBreakAt = now.AddMinutes(config.IntervalMinutes);
            postponed = false;
            HideWarning();
            SendState();
        }

        private void StartBreak()
        {
            if (breakActive)
            {
                return;
            }
            if (IsFullscreenWindow())
            {
                postponed = true;
                fullscreen = true;
                HideWarning();
                SendState();
                return;
            }

            HideWarning();
            breakActive = true;
            postponed = false;
            fullscreen = false;
            idleResetActive = false;
            escDown = false;
            escDownAt = DateTime.MinValue;
            breakEndsAt = DateTime.Now.AddSeconds(config.BreakSeconds);
            breakId = Guid.NewGuid().ToString("N");
            lastTopmost = DateTime.MinValue;

            foreach (WinForms.Screen screen in WinForms.Screen.AllScreens)
            {
                var overlay = new OverlayWindow(screen, config, OnGlobalKeyChanged);
                overlays.Add(overlay);
                overlay.Show();
            }
            foreach (OverlayWindow overlay in overlays)
            {
                overlay.ReassertTopmost();
            }
            foreach (OverlayWindow overlay in overlays)
            {
                if (overlay.IsPrimary)
                {
                    overlay.TakeKeyboardFocus();
                    break;
                }
            }

            PlayDing();
            SendState();
            UpdateTray(DateTime.Now);
            Log("Break started for " + config.BreakSeconds + " seconds.");
        }

        private void EndBreak(bool completed)
        {
            if (!breakActive)
            {
                return;
            }
            breakActive = false;
            escDown = false;
            CloseOverlays();
            DateTime now = DateTime.Now;
            nextBreakAt = now.AddMinutes(config.IntervalMinutes);
            if (completed)
            {
                var message = new Dictionary<string, object>();
                message["type"] = "break-complete";
                message["breakId"] = breakId;
                message["at"] = ToEpochMilliseconds(now);
                message["durationSeconds"] = config.BreakSeconds;
                Send(message);
                Log("Break completed.");
            }
            else
            {
                var message = new Dictionary<string, object>();
                message["type"] = "break-skipped";
                message["breakId"] = breakId;
                message["reason"] = "emergency-escape";
                message["at"] = ToEpochMilliseconds(now);
                Send(message);
                Log("Break ended by emergency escape.");
            }
            SendState();
            UpdateTray(now);
        }

        private void ShowWarning(TimeSpan remaining)
        {
            if (warning == null)
            {
                warning = new WarningWindow();
                warning.Show();
            }
            warning.Update(remaining, config.WarningSeconds, config);
            warningActive = true;
        }

        private void HideWarning()
        {
            if (warning != null)
            {
                warning.CloseForced();
                warning = null;
            }
            warningActive = false;
        }

        private void CloseOverlays()
        {
            foreach (OverlayWindow overlay in overlays)
            {
                overlay.CloseForced();
            }
            overlays.Clear();
        }

        private bool ShouldBlockKey(int virtualKey)
        {
            if (!breakActive)
            {
                return false;
            }
            bool alt = (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
            if (virtualKey == 0x73 && alt)
            {
                return true; // Alt+F4
            }
            if (virtualKey == 0x09 && alt)
            {
                return true; // Alt+Tab
            }
            if (virtualKey == 0x5B || virtualKey == 0x5C || virtualKey == 0x5D)
            {
                return true; // Windows keys and the context-menu key
            }
            return false;
        }

        private void OnGlobalKeyChanged(int virtualKey, bool down)
        {
            if (virtualKey != 0x1B)
            {
                return;
            }
            if (down)
            {
                if (!escDown)
                {
                    escDown = true;
                    escDownAt = DateTime.Now;
                }
            }
            else
            {
                escDown = false;
                escDownAt = DateTime.MinValue;
            }
        }

        private void CreateTray()
        {
            tray = new WinForms.NotifyIcon();
            tray.Icon = MakeTrayIcon("20");
            tray.Text = "Eye Guard — next break";
            tray.Visible = true;
            tray.DoubleClick += delegate { if (!breakActive) StartBreak(); };

            menu = new WinForms.ContextMenuStrip();
            headerItem = new WinForms.ToolStripMenuItem("Next break in 20:00");
            headerItem.Enabled = false;
            statsItem = new WinForms.ToolStripMenuItem("Today: 0 breaks · Streak: 0 days");
            statsItem.Enabled = false;
            menu.Items.Add(headerItem);
            menu.Items.Add(statsItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var startItem = new WinForms.ToolStripMenuItem("Start a break now");
            startItem.Click += delegate { if (!breakActive) StartBreak(); };
            menu.Items.Add(startItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            intervalMenu = new WinForms.ToolStripMenuItem("Focus interval");
            AddChoice(intervalMenu, new[] { 15, 20, 25, 30, 45, 60 }, delegate(int value)
            {
                config.IntervalMinutes = value;
                ResetFocus(DateTime.Now);
                SendConfigChanged();
            });
            menu.Items.Add(intervalMenu);

            breakMenu = new WinForms.ToolStripMenuItem("Break length");
            AddChoice(breakMenu, new[] { 20, 30, 60 }, delegate(int value)
            {
                config.BreakSeconds = value;
                SendConfigChanged();
            });
            menu.Items.Add(breakMenu);

            idleMenu = new WinForms.ToolStripMenuItem("Idle reset");
            AddChoice(idleMenu, new[] { 5, 10, 15 }, delegate(int value)
            {
                config.IdleResetMinutes = value;
                SendConfigChanged();
            });
            menu.Items.Add(idleMenu);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            highContrastItem = new WinForms.ToolStripMenuItem("High-contrast overlay");
            highContrastItem.Click += delegate
            {
                config.HighContrast = !config.HighContrast;
                UpdateMenuChecks();
                SendConfigChanged();
            };
            menu.Items.Add(highContrastItem);

            reduceMotionItem = new WinForms.ToolStripMenuItem("Reduce motion");
            reduceMotionItem.Click += delegate
            {
                config.ReduceMotion = !config.ReduceMotion;
                UpdateMenuChecks();
                SendConfigChanged();
            };
            menu.Items.Add(reduceMotionItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            var aboutItem = new WinForms.ToolStripMenuItem("About Eye Guard");
            aboutItem.Click += delegate
            {
                System.Windows.MessageBox.Show(
                    "Eye Guard\n\nA strict 20-20-20 eye-break overlay above every Windows app.\n\n"
                    + "The break cannot be closed before the timer ends. Hold Esc for 3 seconds for an emergency exit.\n\n"
                    + "Disable the Eye Guard row in DSH to stop the helper.",
                    "About Eye Guard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            };
            menu.Items.Add(aboutItem);
            tray.ContextMenuStrip = menu;
            UpdateMenuChecks();
        }

        private void AddChoice(WinForms.ToolStripMenuItem parent, int[] values, Action<int> apply)
        {
            foreach (int value in values)
            {
                int captured = value;
                var item = new WinForms.ToolStripMenuItem(value.ToString() + (parent.Text == "Break length" ? " seconds" : " minutes"));
                item.Tag = value;
                item.Click += delegate { apply(captured); };
                parent.DropDownItems.Add(item);
            }
        }

        private void UpdateMenuChecks()
        {
            if (intervalMenu == null)
            {
                return;
            }
            CheckChoice(intervalMenu, config.IntervalMinutes);
            CheckChoice(breakMenu, config.BreakSeconds);
            CheckChoice(idleMenu, config.IdleResetMinutes);
            if (highContrastItem != null)
            {
                highContrastItem.Checked = config.HighContrast;
            }
            if (reduceMotionItem != null)
            {
                reduceMotionItem.Checked = config.ReduceMotion;
            }
        }

        private void CheckChoice(WinForms.ToolStripMenuItem parent, int selected)
        {
            foreach (WinForms.ToolStripItem item in parent.DropDownItems)
            {
                var choice = item as WinForms.ToolStripMenuItem;
                if (choice != null && choice.Tag is int)
                {
                    choice.Checked = (int)choice.Tag == selected;
                }
            }
        }

        private void UpdateTray(DateTime now)
        {
            if (tray == null)
            {
                return;
            }
            TimeSpan remaining;
            if (breakActive)
            {
                remaining = breakEndsAt - now;
            }
            else if (postponed)
            {
                remaining = TimeSpan.Zero;
            }
            else
            {
                remaining = nextBreakAt - now;
            }
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }
            string clock = FormatClock(remaining);
            string text = breakActive
                ? "Eye Guard — resting " + clock
                : postponed
                    ? "Eye Guard — break delayed"
                    : "Eye Guard — next break " + clock;
            if (text.Length > 62)
            {
                text = text.Substring(0, 62);
            }
            if (text != lastTrayText)
            {
                tray.Text = text;
                lastTrayText = text;
            }
            if (headerItem != null)
            {
                headerItem.Text = breakActive
                    ? "Resting — " + clock + " remaining"
                    : postponed
                        ? "Break delayed until full screen ends"
                        : "Next break in " + clock;
            }
            if (statsItem != null)
            {
                int today = TodayCount();
                statsItem.Text = "Today: " + today + (today == 1 ? " break" : " breaks") + " · Streak: " + stats.Streak + (stats.Streak == 1 ? " day" : " days");
            }

            string iconLabel = breakActive ? "•" : postponed ? "!" : remaining.TotalSeconds <= 0 ? "!" : Math.Ceiling(remaining.TotalMinutes).ToString();
            if (iconLabel != lastIconLabel)
            {
                lastIconLabel = iconLabel;
                var old = tray.Icon;
                tray.Icon = MakeTrayIcon(iconLabel);
                if (old != null)
                {
                    old.Dispose();
                }
            }
        }

        private int TodayCount()
        {
            if (stats.Date != TodayKey())
            {
                return 0;
            }
            return stats.Completed;
        }

        private void SendConfigChanged()
        {
            var message = new Dictionary<string, object>();
            message["type"] = "settings-changed";
            message["config"] = ConfigDictionary();
            Send(message);
            UpdateTray(DateTime.Now);
        }

        private Dictionary<string, object> ConfigDictionary()
        {
            var values = new Dictionary<string, object>();
            values["intervalMinutes"] = config.IntervalMinutes;
            values["breakSeconds"] = config.BreakSeconds;
            values["warningSeconds"] = config.WarningSeconds;
            values["idleResetMinutes"] = config.IdleResetMinutes;
            values["highContrast"] = config.HighContrast;
            values["reduceMotion"] = config.ReduceMotion;
            return values;
        }

        private void SendReady()
        {
            if (readySent)
            {
                return;
            }
            readySent = true;
            var message = new Dictionary<string, object>();
            message["type"] = "ready";
            Send(message);
        }

        private void SendState()
        {
            try
            {
                DateTime now = DateTime.Now;
                var message = new Dictionary<string, object>();
                message["type"] = "state";
                message["phase"] = breakActive ? "break" : postponed ? "postponed" : warningActive ? "warning" : "focus";
                message["nextBreakAt"] = ToEpochMilliseconds(breakActive ? breakEndsAt : nextBreakAt);
                message["breakEndsAt"] = breakActive ? (object)ToEpochMilliseconds(breakEndsAt) : null;
                message["remainingSeconds"] = breakActive
                    ? Math.Max(0, (int)Math.Ceiling((breakEndsAt - now).TotalSeconds))
                    : Math.Max(0, (int)Math.Ceiling((nextBreakAt - now).TotalSeconds));
                message["today"] = TodayCount();
                message["streak"] = stats.Streak;
                message["fullscreen"] = fullscreen;
                message["postponed"] = postponed;
                Send(message);
                lastStateSent = now;
            }
            catch
            {
                // A status message must never break the timer.
            }
        }

        private void Send(Dictionary<string, object> message)
        {
            if (output == null)
            {
                return;
            }
            try
            {
                output.WriteLine(serializer.Serialize(message));
                output.Flush();
            }
            catch
            {
                // The Host may have closed the pipe.
            }
        }

        private void Log(string message)
        {
            var payload = new Dictionary<string, object>();
            payload["type"] = "log";
            payload["message"] = message;
            Send(payload);
        }

        private void Shutdown()
        {
            if (disposed)
            {
                return;
            }
            Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            timer.Stop();
            CloseOverlays();
            HideWarning();
            keyboard.Dispose();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            if (output != null)
            {
                try
                {
                    output.Flush();
                }
                catch
                {
                }
            }
        }

        private static void PlayDing()
        {
            try
            {
                byte[] wav = BuildDing();
                Task.Run(delegate
                {
                    try
                    {
                        using (var stream = new MemoryStream(wav))
                        using (var player = new SoundPlayer(stream))
                        {
                            player.PlaySync();
                        }
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
                try
                {
                    SystemSounds.Asterisk.Play();
                }
                catch
                {
                }
            }
        }

        private static byte[] BuildDing()
        {
            const int sampleRate = 44100;
            const double duration = 0.24;
            int samples = (int)(sampleRate * duration);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(samples * 2);
                for (int i = 0; i < samples; i++)
                {
                    double t = (double)i / sampleRate;
                    double envelope = Math.Exp(-9.5 * t);
                    double value = Math.Sin(2 * Math.PI * 880 * t) * 0.33 * envelope
                        + Math.Sin(2 * Math.PI * 1320 * t) * 0.10 * envelope;
                    writer.Write((short)(value * short.MaxValue));
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static Drawing.Icon MakeTrayIcon(string label)
        {
            using (var bitmap = new Drawing.Bitmap(32, 32))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Drawing.Color.Transparent);

                using (var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 10, 18, 31)))
                {
                    graphics.FillEllipse(background, 1, 1, 30, 30);
                }
                using (var ring = new Drawing.Pen(Drawing.Color.FromArgb(255, 139, 211, 255), 2.0f))
                {
                    graphics.DrawEllipse(ring, 2, 2, 28, 28);
                }
                using (var accent = new Drawing.Pen(Drawing.Color.FromArgb(255, 255, 212, 0), 1.6f))
                {
                    graphics.DrawLine(accent, 16, 4, 16, 7);
                    graphics.DrawLine(accent, 16, 25, 16, 28);
                    graphics.DrawLine(accent, 4, 16, 7, 16);
                    graphics.DrawLine(accent, 25, 16, 28, 16);
                }
                using (var font = new Drawing.Font("Segoe UI", label.Length > 1 ? 11.0f : 14.0f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel))
                using (var format = new Drawing.StringFormat())
                using (var text = new Drawing.SolidBrush(Drawing.Color.White))
                {
                    format.Alignment = Drawing.StringAlignment.Center;
                    format.LineAlignment = Drawing.StringAlignment.Center;
                    var bounds = new Drawing.RectangleF(0, 0, 32, 32);
                    graphics.DrawString(label, font, text, bounds, format);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (var icon = Drawing.Icon.FromHandle(handle))
                    {
                        return (Drawing.Icon)icon.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }

        private static string FormatClock(TimeSpan value)
        {
            int total = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static long ToEpochMilliseconds(DateTime value)
        {
            return (long)(value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static string TodayKey()
        {
            return DateTime.Now.ToString("yyyy-MM-dd");
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> values, string key)
        {
            object value;
            if (values != null && values.TryGetValue(key, out value))
            {
                return value as Dictionary<string, object>;
            }
            return null;
        }

        private static string GetString(Dictionary<string, object> values, string key, string fallback)
        {
            object value;
            if (values != null && values.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value);
            }
            return fallback;
        }

        private static int GetInt(Dictionary<string, object> values, string key, int fallback)
        {
            object value;
            if (values != null && values.TryGetValue(key, out value) && value != null)
            {
                try
                {
                    return Convert.ToInt32(value);
                }
                catch
                {
                }
            }
            return fallback;
        }

        private static bool GetBool(Dictionary<string, object> values, string key, bool fallback)
        {
            object value;
            if (values != null && values.TryGetValue(key, out value) && value != null)
            {
                try
                {
                    return Convert.ToBoolean(value);
                }
                catch
                {
                }
            }
            return fallback;
        }

        private static bool IsFullscreenWindow()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero || !Native.IsWindowVisible(foreground))
            {
                return false;
            }
            uint processId;
            Native.GetWindowThreadProcessId(foreground, out processId);
            if (processId == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                return false;
            }
            try
            {
                string processName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
                if (WorkProcesses.Contains(processName))
                {
                    return false;
                }
            }
            catch
            {
                // The process may have exited between the two calls.
            }
            var className = new StringBuilder(256);
            Native.GetClassName(foreground, className, className.Capacity);
            string name = className.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd"
                || name == "NotifyIconOverflowWindow" || name == "TopLevelWindowForOverflowXamlIsland")
            {
                return false;
            }
            int style = Native.GetWindowLong(foreground, Native.GWL_STYLE);
            if ((style & Native.WS_CHILD) != 0)
            {
                return false;
            }
            int extended = Native.GetWindowLong(foreground, Native.GWL_EXSTYLE);
            if ((extended & Native.WS_EX_TOOLWINDOW) != 0)
            {
                return false;
            }
            var placement = new Native.WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
            if (Native.GetWindowPlacement(foreground, ref placement) && placement.showCmd == Native.SW_SHOWMAXIMIZED)
            {
                return false;
            }
            Native.RECT bounds;
            if (!Native.GetWindowRect(foreground, out bounds))
            {
                return false;
            }
            IntPtr monitor = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }
            var info = new Native.MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(monitor, ref info))
            {
                return false;
            }
            const int tolerance = 2;
            return bounds.Left <= info.rcMonitor.Left + tolerance
                && bounds.Top <= info.rcMonitor.Top + tolerance
                && bounds.Right >= info.rcMonitor.Right - tolerance
                && bounds.Bottom >= info.rcMonitor.Bottom - tolerance;
        }

        private static uint IdleMilliseconds()
        {
            var info = new Native.LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
            if (!Native.GetLastInputInfo(ref info))
            {
                return 0;
            }
            uint tick = (uint)Environment.TickCount;
            return unchecked(tick - info.dwTime);
        }
    }

    internal sealed class OverlayWindow : Window
    {
        private readonly TextBlock countdown;
        private readonly TextBlock hint;
        private readonly Border progressFill;
        private readonly Border emergencyFill;
        private readonly Grid emergencyTrack;
        private readonly Ellipse glow;
        private readonly Action<int, bool> keyChanged;
        private bool allowClose;

        public OverlayWindow(WinForms.Screen screen, Config current, Action<int, bool> onKeyChanged)
        {
            keyChanged = onKeyChanged;
            Title = "Eye Guard break";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = screen.Primary;
            Focusable = true;
            Width = 800;
            Height = 600;
            screenForWindow = screen;

            bool highContrast = current.HighContrast || SystemParameters.HighContrast;
            Brush textBrush = new SolidColorBrush(highContrast ? Colors.White : Color.FromRgb(235, 243, 252));
            Brush secondaryBrush = new SolidColorBrush(highContrast ? Colors.White : Color.FromRgb(167, 184, 204));
            Brush accentBrush = new SolidColorBrush(highContrast ? Color.FromRgb(255, 212, 0) : Color.FromRgb(139, 211, 255));
            Brush cardBrush = new SolidColorBrush(highContrast ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(242, 10, 18, 31));
            Brush overlayBrush = new SolidColorBrush(highContrast ? Color.FromArgb(240, 0, 0, 0) : Color.FromArgb(208, 5, 10, 18));

            var root = new Grid();
            root.Background = overlayBrush;
            AutomationProperties.SetName(root, "Eye Guard break. Look at something 20 feet away.");
            Content = root;

            glow = new Ellipse();
            glow.Width = 980;
            glow.Height = 980;
            glow.HorizontalAlignment = HorizontalAlignment.Center;
            glow.VerticalAlignment = VerticalAlignment.Center;
            glow.IsHitTestVisible = false;
            var radial = new RadialGradientBrush();
            radial.GradientStops.Add(new GradientStop(Color.FromArgb(highContrast ? (byte)0 : (byte)52, 139, 211, 255), 0));
            radial.GradientStops.Add(new GradientStop(Color.FromArgb(0, 139, 211, 255), 1));
            glow.Fill = radial;
            root.Children.Add(glow);

            var card = new Border();
            card.Background = cardBrush;
            card.CornerRadius = new CornerRadius(24);
            card.BorderBrush = new SolidColorBrush(highContrast ? Colors.White : Color.FromArgb(42, 255, 255, 255));
            card.BorderThickness = new Thickness(highContrast ? 2 : 1);
            card.Padding = new Thickness(44, 36, 44, 32);
            card.MaxWidth = 560;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            card.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black };
            AutomationProperties.SetName(card, "Eye Guard break card");
            root.Children.Add(card);

            var stack = new StackPanel();
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            card.Child = stack;

            var kicker = new TextBlock();
            kicker.Text = "EYE GUARD";
            kicker.FontSize = 11;
            kicker.FontWeight = FontWeights.SemiBold;
            kicker.Foreground = accentBrush;
            kicker.HorizontalAlignment = HorizontalAlignment.Center;
            kicker.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(kicker);

            var headline = new TextBlock();
            headline.Text = "Rest your eyes";
            headline.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            headline.FontSize = 34;
            headline.FontWeight = FontWeights.Bold;
            headline.Foreground = textBrush;
            headline.HorizontalAlignment = HorizontalAlignment.Center;
            AutomationProperties.SetLiveSetting(headline, AutomationLiveSetting.Assertive);
            stack.Children.Add(headline);

            var instruction = new TextBlock();
            instruction.Text = "Look at something 20 feet away.";
            instruction.FontSize = 16;
            instruction.Foreground = secondaryBrush;
            instruction.HorizontalAlignment = HorizontalAlignment.Center;
            instruction.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(instruction);

            countdown = new TextBlock();
            countdown.Text = current.BreakSeconds.ToString();
            countdown.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            countdown.FontSize = 84;
            countdown.FontWeight = FontWeights.Bold;
            countdown.Foreground = accentBrush;
            countdown.HorizontalAlignment = HorizontalAlignment.Center;
            countdown.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(countdown);

            var seconds = new TextBlock();
            seconds.Text = "seconds";
            seconds.FontSize = 11;
            seconds.Foreground = secondaryBrush;
            seconds.HorizontalAlignment = HorizontalAlignment.Center;
            seconds.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(seconds);

            var progressTrack = new Grid();
            progressTrack.Width = 320;
            progressTrack.Height = 4;
            progressTrack.HorizontalAlignment = HorizontalAlignment.Center;
            progressTrack.Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
            var trackClip = new Border();
            trackClip.CornerRadius = new CornerRadius(2);
            trackClip.ClipToBounds = true;
            progressFill = new Border();
            progressFill.Width = 320;
            progressFill.HorizontalAlignment = HorizontalAlignment.Left;
            progressFill.Background = accentBrush;
            progressFill.CornerRadius = new CornerRadius(2);
            trackClip.Child = progressFill;
            progressTrack.Children.Add(trackClip);
            stack.Children.Add(progressTrack);

            hint = new TextBlock();
            hint.Text = "Hold Esc for 3 seconds to end early";
            hint.FontSize = 12;
            hint.Foreground = secondaryBrush;
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 22, 0, 0);
            AutomationProperties.SetName(hint, "Hold Escape for three seconds to end this break early.");
            stack.Children.Add(hint);

            emergencyTrack = new Grid();
            emergencyTrack.Width = 220;
            emergencyTrack.Height = 3;
            emergencyTrack.HorizontalAlignment = HorizontalAlignment.Center;
            emergencyTrack.Margin = new Thickness(0, 10, 0, 0);
            emergencyTrack.Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
            emergencyTrack.Visibility = Visibility.Hidden;
            var emergencyClip = new Border();
            emergencyClip.CornerRadius = new CornerRadius(2);
            emergencyClip.ClipToBounds = true;
            emergencyFill = new Border();
            emergencyFill.Width = 0;
            emergencyFill.HorizontalAlignment = HorizontalAlignment.Left;
            emergencyFill.Background = new SolidColorBrush(Color.FromRgb(255, 212, 0));
            emergencyClip.Child = emergencyFill;
            emergencyTrack.Children.Add(emergencyClip);
            stack.Children.Add(emergencyTrack);

            Loaded += OnLoaded;
            SourceInitialized += OnSourceInitialized;
            Closing += OnClosing;
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    keyChanged(0x1B, true);
                    e.Handled = true;
                }
            };
            PreviewKeyUp += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    keyChanged(0x1B, false);
                    e.Handled = true;
                }
            };

            if (!highContrast && !current.ReduceMotion && SystemParameters.ClientAreaAnimation)
            {
                var pulse = new DoubleAnimation(0.35, 0.75, TimeSpan.FromSeconds(3.2));
                pulse.AutoReverse = true;
                pulse.RepeatBehavior = RepeatBehavior.Forever;
                glow.BeginAnimation(OpacityProperty, pulse);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int extended = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
            Native.SetWindowLong(handle, Native.GWL_EXSTYLE, extended | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
            PositionWindow();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int extended = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
            Native.SetWindowLong(handle, Native.GWL_EXSTYLE, extended | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
            PositionWindow();
        }

        private void PositionWindow()
        {
            if (screenForWindow == null)
            {
                return;
            }
            Drawing.Rectangle bounds = screenForWindow.Bounds;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(
                handle,
                Native.HWND_TOPMOST,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        private WinForms.Screen screenForWindow;

        public bool IsPrimary
        {
            get { return screenForWindow != null && screenForWindow.Primary; }
        }

        public void SetScreen(WinForms.Screen screen)
        {
            screenForWindow = screen;
        }

        public void ApplyConfig(Config current)
        {
            // The break card keeps its opening palette so the contrast does not jump mid-break.
        }

        public void UpdateBreak(TimeSpan remaining, int totalSeconds, bool holdingEscape, double heldSeconds, Config current)
        {
            countdown.Text = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds)).ToString();
            double fraction = totalSeconds <= 0 ? 0 : Math.Max(0, Math.Min(1, remaining.TotalSeconds / totalSeconds));
            progressFill.Width = 320 * fraction;
            if (holdingEscape)
            {
                emergencyTrack.Visibility = Visibility.Visible;
                emergencyFill.Width = 220 * Math.Max(0, Math.Min(1, heldSeconds / 3.0));
                hint.Text = "Keep holding Esc… " + Math.Min(3.0, heldSeconds).ToString("0.0") + " / 3.0";
            }
            else
            {
                emergencyTrack.Visibility = Visibility.Hidden;
                emergencyFill.Width = 0;
                hint.Text = "Hold Esc for 3 seconds to end early";
            }
        }

        public void ReassertTopmost()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(
                handle,
                Native.HWND_TOPMOST,
                0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void TakeKeyboardFocus()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                Native.ForceForeground(handle);
                Activate();
                Focus();
                Keyboard.Focus(this);
            }
            catch
            {
            }
        }

        public void CloseForced()
        {
            allowClose = true;
            Close();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!allowClose)
            {
                e.Cancel = true;
            }
        }
    }

    internal sealed class WarningWindow : Window
    {
        private readonly TextBlock countdown;
        private readonly Border progressFill;
        private bool allowClose;

        public WarningWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromRgb(10, 18, 31));
            Title = "Eye Guard break warning";
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowActivated = false;
            Width = 350;
            Height = 120;

            var card = new Border();
            card.CornerRadius = new CornerRadius(0);
            card.Background = Brushes.Transparent;
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255));
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(16, 12, 16, 10);
            AutomationProperties.SetName(card, "Eye break warning");
            Content = card;

            var stack = new StackPanel();
            card.Child = stack;

            var kicker = new TextBlock();
            kicker.Text = "EYE BREAK IN";
            kicker.FontSize = 10;
            kicker.FontWeight = FontWeights.SemiBold;
            kicker.Foreground = new SolidColorBrush(Color.FromRgb(139, 211, 255));
            stack.Children.Add(kicker);

            countdown = new TextBlock();
            countdown.Text = "00:30";
            countdown.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            countdown.FontSize = 26;
            countdown.FontWeight = FontWeights.Bold;
            countdown.Foreground = Brushes.White;
            countdown.Margin = new Thickness(0, 2, 0, 0);
            AutomationProperties.SetLiveSetting(countdown, AutomationLiveSetting.Polite);
            stack.Children.Add(countdown);

            var hint = new TextBlock();
            hint.Text = "Finish this thought.";
            hint.FontSize = 11;
            hint.Foreground = new SolidColorBrush(Color.FromRgb(167, 184, 204));
            hint.Margin = new Thickness(0, 1, 0, 7);
            stack.Children.Add(hint);

            var track = new Grid();
            track.Height = 3;
            track.Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
            progressFill = new Border();
            progressFill.Width = 298;
            progressFill.HorizontalAlignment = HorizontalAlignment.Left;
            progressFill.Background = new SolidColorBrush(Color.FromRgb(139, 211, 255));
            progressFill.CornerRadius = new CornerRadius(2);
            track.Children.Add(progressFill);
            stack.Children.Add(track);

            SourceInitialized += delegate
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                int extended = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
                Native.SetWindowLong(handle, Native.GWL_EXSTYLE,
                    extended | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
            };
            Closing += delegate(object sender, CancelEventArgs e)
            {
                if (!allowClose)
                {
                    e.Cancel = true;
                }
            };
            Loaded += delegate { PositionWindow(); };
        }

        public void Update(TimeSpan remaining, int warningSeconds, Config current)
        {
            int seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            countdown.Text = (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
            double fraction = warningSeconds <= 0 ? 0 : Math.Max(0, Math.Min(1, remaining.TotalSeconds / warningSeconds));
            progressFill.Width = 298 * fraction;
            ReassertTopmost();
        }

        private void ReassertTopmost()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(
                handle,
                Native.HWND_TOPMOST,
                0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void CloseForced()
        {
            allowClose = true;
            Close();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            PositionWindow();
        }

        private void PositionWindow()
        {
            WinForms.Screen screen = WinForms.Screen.PrimaryScreen;
            if (screen == null)
            {
                return;
            }
            Drawing.Rectangle area = screen.WorkingArea;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(
                handle,
                Native.HWND_TOPMOST,
                area.Left + 24,
                area.Top + 72,
                0,
                0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
    }

    internal sealed class LowLevelKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardData
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private readonly HookProcedure procedure;
        private IntPtr hook = IntPtr.Zero;

        public event Action<int, bool> KeyChanged;
        public Func<int, bool> ShouldBlock;

        public LowLevelKeyboardHook()
        {
            procedure = Callback;
        }

        public bool Installed
        {
            get { return hook != IntPtr.Zero; }
        }

        public void Install()
        {
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, procedure, GetModuleHandle(null), 0);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        }

        private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var data = (KeyboardData)Marshal.PtrToStructure(lParam, typeof(KeyboardData));
                int message = wParam.ToInt32();
                bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                bool up = message == WM_KEYUP || message == WM_SYSKEYUP;
                if (down || up)
                {
                    int key = (int)data.VirtualKey;
                    Action<int, bool> listener = KeyChanged;
                    if (listener != null)
                    {
                        listener(key, down);
                    }
                    Func<int, bool> blocker = ShouldBlock;
                    if (blocker != null && blocker(key))
                    {
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }

    internal static class Native
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int SW_SHOWMAXIMIZED = 3;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        public static void ForceForeground(IntPtr window)
        {
            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = 0;
            if (foreground != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foreground, out foregroundThread);
            }
            uint thisThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != thisThread
                && AttachThreadInput(thisThread, foregroundThread, true);
            try
            {
                SetForegroundWindow(window);
                BringWindowToTop(window);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(thisThread, foregroundThread, false);
                }
            }
        }
    }
}
