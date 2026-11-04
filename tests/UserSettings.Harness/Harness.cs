using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using MsCrmTools.UserSettingsUtility;
using ThreadExceptionEventHandler = System.Threading.ThreadExceptionEventHandler;

namespace UserSettings.Harness
{
    /// <summary>
    /// One step of a scenario: something to do, or something to wait for. Waiting is on a
    /// condition rather than a clock, so a slow machine makes a run slower, not wrong.
    /// </summary>
    public class Step
    {
        public string Label;
        public Action<Run> Do;
        public Func<Run, bool> Until;
        public int TimeoutMs;
    }

    /// <summary>The tool in a window, the org it is talking to, and what went wrong.</summary>
    public class Run
    {
        public Form Form;
        public MainControl Control;
        public IOrganizationService Service;
        public string ShotDir;
        public int UiThreadId;

        public readonly List<string> Failures = new List<string>();

        /// <summary>What the tool threw in a scenario that carries on afterwards, as a person would.</summary>
        public readonly List<string> Thrown = new List<string>();

        /// <summary>Captions and text of every dialog the tool put up, in order.</summary>
        public readonly List<string> Dialogs = new List<string>();

        public readonly Stopwatch Clock = new Stopwatch();

        public void Check(bool ok, string what)
        {
            if (!ok) Failures.Add(Clock.ElapsedMilliseconds + "ms  " + what);
        }

        public FakeOrg Fake
        {
            get { return Service as FakeOrg; }
        }

        /// <summary>
        /// The tool's own log. A user it fails to update is written here and nowhere else, so a
        /// run that only watched the dialogs would call a silent failure a pass.
        /// </summary>
        public string LogPath
        {
            get
            {
                var log = Ui.Field<object>(Control, "log");
                return log?.GetType().GetProperty("FilePath")?.GetValue(log) as string;
            }
        }

        public string[] LogLines
        {
            get { return LogPath != null && File.Exists(LogPath) ? File.ReadAllLines(LogPath) : new string[0]; }
        }

        /// <summary>A private control of the tool, by its designer name.</summary>
        public T Find<T>(string name) where T : class
        {
            return Ui.Field<T>(Control, name) ?? Ui.Field<T>(Ui.Field<object>(Control, "userSelector1"), name)
                   ?? throw new MissingMemberException("MainControl", name);
        }

        public ListView Users
        {
            get { return Find<ListView>("lvUsers"); }
        }

        /// <summary>
        /// Hands the tool a connection the way XrmToolBox does when the toolbar's connection
        /// changes. The empty action name, not null: with anything else the base class goes
        /// looking for a method of that name to run.
        /// </summary>
        public void SwitchTo(IOrganizationService service, string actionName = "")
        {
            Control.UpdateConnection(service, Ui.Detail(), actionName, null);
            Service = service;
        }
    }

    public class Scenario
    {
        public string Name;
        public string Why;
        public Func<IOrganizationService> Org;
        public readonly List<Step> Steps = new List<Step>();

        /// <summary>
        /// Dismiss what the tool throws and play on, the way XrmToolBox's error box lets a person
        /// do. The scenario decides at the end what the throws mean.
        /// </summary>
        public bool Survive;

        public Scenario Do(string label, Action<Run> what)
        {
            Steps.Add(new Step { Label = label, Do = what });
            return this;
        }

        public Scenario WaitFor(string label, Func<Run, bool> until, int timeoutMs = 15000)
        {
            Steps.Add(new Step { Label = label, Until = until, TimeoutMs = timeoutMs });
            return this;
        }

        /// <summary>
        /// Runs the scenario in a window of its own on this thread, with a live message loop,
        /// because that is where the tool lives: WorkAsync marshals its callbacks back here.
        /// A step that throws or a wait that times out ends the run; the rest would only be
        /// testing the wreckage.
        /// </summary>
        public List<string> Play(string root)
        {
            var run = new Run { ShotDir = Path.Combine(root, Name), UiThreadId = Environment.CurrentManagedThreadId };
            if (Directory.Exists(run.ShotDir)) Directory.Delete(run.ShotDir, true);
            Directory.CreateDirectory(run.ShotDir);

            run.Service = Org();
            run.Control = new MainControl { Dock = DockStyle.Fill };
            run.Form = new QuietForm { Text = "User Settings Utility — " + Name, ClientSize = new Size(1280, 800) };
            run.Form.Controls.Add(run.Control);

            // One log per scenario, so what one wrote cannot pass or fail another
            if (run.LogPath != null && File.Exists(run.LogPath)) File.Delete(run.LogPath);

            ThreadExceptionEventHandler onThreadException = (s, e) =>
            {
                if (Survive) run.Thrown.Add(Ui.Where(e.Exception));
                else run.Failures.Add(run.Clock.ElapsedMilliseconds + "ms  the tool threw: " + Ui.Head(e.Exception));
            };
            Application.ThreadException += onThreadException;

            var sweeper = new Dialogs(run);

            run.Form.Shown += (s, e) =>
            {
                run.Clock.Start();
                sweeper.Start();
                run.SwitchTo(run.Service);

                var queue = new Queue<Step>(Steps);
                var shot = 0;
                Stopwatch waiting = null;
                var pump = new Timer { Interval = 25 };
                pump.Tick += (s2, e2) =>
                {
                    // A step's own work can pump messages (a modal dialog does), which would
                    // re-enter here and run the next step from inside this one
                    pump.Stop();
                    var done = false;

                    while (queue.Count > 0 && run.Failures.Count == 0)
                    {
                        var step = queue.Peek();

                        if (step.Until != null)
                        {
                            bool met;
                            try { met = step.Until(run); }
                            catch (Exception ex) { met = false; run.Failures.Add("waiting for \"" + step.Label + "\" threw: " + Ui.Head(ex)); }

                            if (!met && run.Failures.Count == 0)
                            {
                                waiting = waiting ?? Stopwatch.StartNew();
                                if (waiting.ElapsedMilliseconds < step.TimeoutMs) break;
                                run.Failures.Add(run.Clock.ElapsedMilliseconds + "ms  gave up waiting for: " + step.Label);
                            }

                            waiting = null;
                        }
                        else
                        {
                            try { step.Do(run); }
                            catch (Exception ex) { run.Failures.Add(run.Clock.ElapsedMilliseconds + "ms  \"" + step.Label + "\" threw: " + Ui.Head(ex)); }
                        }

                        queue.Dequeue();
                        try { Capture.Of(run.Form, Path.Combine(run.ShotDir, (++shot).ToString("00") + "-" + Ui.Slug(step.Label) + ".png")); }
                        catch (Exception) { /* a shot that cannot be taken is not worth failing over */ }
                    }

                    done = queue.Count == 0 || run.Failures.Count > 0;
                    if (!done)
                    {
                        pump.Start();
                        return;
                    }

                    sweeper.Stop();
                    run.Form.Close();
                };
                pump.Start();
            };

            try
            {
                Application.Run(run.Form);
            }
            finally
            {
                Application.ThreadException -= onThreadException;
                sweeper.Stop();
                if (!run.Control.IsDisposed) run.Control.Dispose();
                run.Form.Dispose();
            }

            File.WriteAllLines(Path.Combine(run.ShotDir, "dialogs.txt"), run.Dialogs);
            File.WriteAllLines(Path.Combine(run.ShotDir, "tool.log"), run.LogLines);
            return run.Failures;
        }
    }

    public static class Ui
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static T Field<T>(object owner, string name) where T : class
        {
            if (owner == null) return null;
            for (var type = owner.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(owner) as T;
            }
            return null;
        }

        /// <summary>
        /// The connection detail XrmToolBox would fill in. The tool reads the major version off
        /// it to decide whether apps have sitemaps of their own.
        /// </summary>
        public static ConnectionDetail Detail()
        {
            var detail = new ConnectionDetail();
            var version = typeof(ConnectionDetail).GetProperty("OrganizationVersion");
            if (version != null && version.CanWrite) version.SetValue(detail, "9.2.25101.100");
            return detail;
        }

        public static string Head(Exception ex)
        {
            var line = ex.GetBaseException().ToString();
            var stop = line.IndexOf('\n');
            return (stop < 0 ? line : line.Substring(0, stop)).Trim();
        }

        /// <summary>The exception's type and the method of the tool it came out of.</summary>
        public static string Where(Exception ex)
        {
            var root = ex.GetBaseException();
            var frame = (root.StackTrace ?? "").Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.Contains("MsCrmTools.UserSettingsUtility"));
            return root.GetType().Name + " " + frame;
        }

        public static string Slug(string label)
        {
            var sb = new StringBuilder();
            foreach (var c in label ?? "step") sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            return sb.ToString().Trim('-');
        }

        public static string[] Items(ComboBox combo)
        {
            return combo.Items.Cast<object>().Select(i => i?.ToString()).ToArray();
        }

        public static string[] Items(ToolStripComboBox combo)
        {
            return combo.Items.Cast<object>().Select(i => i?.ToString()).ToArray();
        }
    }

    /// <summary>
    /// Answers whatever modal the tool puts up, and writes down that it did, caption and text.
    /// A YesNo box has no Cancel, so WM_CLOSE does nothing to it: questions are answered No
    /// (do not overwrite the log, do not open it), everything else is closed.
    /// </summary>
    public class Dialogs
    {
        private const int WM_CLOSE = 0x0010;
        private const int BM_CLICK = 0x00F5;
        private const int IDNO = 7;
        private const int MessageText = 0xFFFF;

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumThreadDelegate callback, IntPtr param);

        private delegate bool EnumThreadDelegate(IntPtr hwnd, IntPtr param);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hwnd, int id);

        private readonly Run _run;
        private readonly Timer _timer;
        private readonly HashSet<IntPtr> _answered = new HashSet<IntPtr>();

        public Dialogs(Run run)
        {
            _run = run;
            // A modal dialog runs its own loop and pumps WM_TIMER, which is why this works from
            // inside one
            _timer = new Timer { Interval = 120 };
            _timer.Tick += (s, e) => Sweep();
        }

        public void Start() { _timer.Start(); }

        public void Stop() { _timer.Stop(); }

        private static string Text(IntPtr hwnd)
        {
            var text = new StringBuilder(1024);
            GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }

        private void Sweep()
        {
            var mine = _run.Form.IsDisposed ? IntPtr.Zero : _run.Form.Handle;
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, param) =>
            {
                if (hwnd == mine || !IsWindowVisible(hwnd)) return true;

                var caption = Text(hwnd);
                if (caption.Length == 0) return true;

                // The tool's own working message is a window too; it is not a dialog
                if (Application.OpenForms.Cast<Form>().Any(f => f.Handle == hwnd && f != _run.Form && !f.Modal)) return true;

                if (_answered.Add(hwnd))
                {
                    var body = GetDlgItem(hwnd, MessageText);
                    _run.Dialogs.Add(caption + ": " + (body == IntPtr.Zero ? "" : Text(body).Replace("\r", " ").Replace("\n", " ")));
                }

                var no = GetDlgItem(hwnd, IDNO);
                if (no != IntPtr.Zero) PostMessage(no, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                else PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
    }

    /// <summary>
    /// The window a scenario plays out in: off the right edge of every monitor and never
    /// activated, so the machine stays usable. USU_HARNESS_ONSCREEN=1 to watch instead.
    /// </summary>
    public class QuietForm : Form
    {
        public static bool OnScreen
        {
            get { return Environment.GetEnvironmentVariable("USU_HARNESS_ONSCREEN") == "1"; }
        }

        protected override bool ShowWithoutActivation
        {
            get { return !OnScreen; }
        }

        public QuietForm()
        {
            StartPosition = FormStartPosition.Manual;
            if (OnScreen)
            {
                Location = new Point(0, 0);
            }
            else
            {
                var desktop = SystemInformation.VirtualScreen;
                Location = new Point(desktop.Right + 64, desktop.Top + 64);
                ShowInTaskbar = false;
            }
        }
    }

    /// <summary>A window's own pixels, on disk, asked of the window rather than the screen.</summary>
    public static class Capture
    {
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        public static void Of(Form form, string outPath)
        {
            using (var bmp = new Bitmap(form.Bounds.Width, form.Bounds.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { PrintWindow(form.Handle, hdc, 2); }
                    finally { g.ReleaseHdc(hdc); }
                }
                bmp.Save(outPath, ImageFormat.Png);
            }
        }
    }
}
