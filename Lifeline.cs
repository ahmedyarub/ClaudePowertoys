using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// Keeps Powertoys running. It died on 2026-10-15: at 08:37 a .NET runtime installer asked Restart
/// Manager to shut it down, which closes an app by sending WM_CLOSE to its top-level windows. That
/// destroyed the session switcher's hidden window while the app itself carried on in the tray; half
/// an hour later a background scan posted its result to the dead window, and the exception that
/// threw ended the process.
/// So: windows refuse a close from outside the app (none of them is ever closed, only hidden); a
/// window destroyed anyway is made again, or the app restarts if the form itself is gone; posts to a
/// window never throw; and an unhandled exception starts a fresh copy before the process goes.
/// </summary>
static class Lifeline
{
    const string AfterArg = "--after";
    const string CrashedArg = "--crashed";

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    const int SM_SHUTTINGDOWN = 0x2000;

    static readonly DateTime StartedAt = DateTime.Now;
    static bool _startedAfterCrash;
    static int _restarting;

    /// <summary>Set once the app is exiting on purpose; windows going away then is expected.</summary>
    public static volatile bool Exiting;

    /// <summary>Exits the app the normal way (the tray context's ExitThread). Called on the UI thread.</summary>
    public static Action? ExitApp;

    /// <summary>
    /// Called first thing in Main. A copy started by <see cref="Restart"/> waits here for the old one to
    /// finish exiting, so the hooks and hotkeys it is about to register are free.
    /// </summary>
    public static void Begin(string[] args)
    {
        _startedAfterCrash = args.Contains(CrashedArg);
        int i = Array.IndexOf(args, AfterArg);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (!previous.WaitForExit(15_000)) Log.Write($"Restart: the previous copy (pid {pid}) is still running after 15 s; starting anyway");
        }
        catch (ArgumentException) { }   // already gone
    }

    [DllImport("user32.dll")] static extern bool EnumThreadWindows(int threadId, EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    static readonly Dictionary<IntPtr, Shield> Shields = [];

    /// <summary>
    /// Shields every top-level window of the UI thread now, and every one made later. Replaying the
    /// installer's request (RmShutdown, 2026-10-15) destroyed all 16 of them, not only the forms: the
    /// tray icon's, the hotkeys', the tooltips' -- plain windows, which DefWindowProc destroys on
    /// WM_CLOSE. The process lived on with nothing working. Call on the UI thread.
    /// </summary>
    public static void StartShielding()
    {
        ShieldNewWindows();
        // Windows are also made later (a form's first showing, a recreated one); a sweep is cheap.
        var sweep = new System.Windows.Forms.Timer { Interval = 2000 };
        sweep.Tick += (_, _) => { if (!Exiting) ShieldNewWindows(); };
        sweep.Start();
    }

    static void ShieldNewWindows()
    {
        foreach (var gone in Shields.Keys.Where(h => !IsWindow(h)).ToList()) Shields.Remove(gone);
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (Shields.ContainsKey(hwnd)) return true;
            var cls = new System.Text.StringBuilder(128);
            GetClassName(hwnd, cls, cls.Capacity);
            if (!cls.ToString().StartsWith("WindowsForms10.", StringComparison.Ordinal)) return true;   // the IME's, GDI+'s
            var shield = new Shield(isForm: Control.FromHandle(hwnd) is Form);
            shield.AssignHandle(hwnd);
            Shields[hwnd] = shield;
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Sits in front of one window's own window procedure.</summary>
    sealed class Shield(bool isForm) : NativeWindow
    {
        const int WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011;
        const long ENDSESSION_CLOSEAPP = 0x1;

        protected override void WndProc(ref Message m)
        {
            if (!Exiting)
            {
                // CLOSEAPP is Restart Manager asking on an installer's behalf, not the session ending
                // (a real logoff or shutdown passes through). Saying no is what it got at 08:37 anyway,
                // and the installer carried on; saying yes is what led to the WM_CLOSEs.
                if (m.Msg == WM_QUERYENDSESSION && (m.LParam.ToInt64() & ENDSESSION_CLOSEAPP) != 0)
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
                // A form decides for itself (see Guard: closing it only hides it). Any other window
                // is only ever closed by being destroyed, which takes its feature with it.
                if (m.Msg == WM_CLOSE && !isForm)
                {
                    Log.Write("Ignored a request from outside the app to close one of its windows");
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Makes a window survive close requests from outside the app; see the class summary.</summary>
    public static void Guard(Form form)
    {
        form.FormClosing += (_, e) =>
        {
            if (e.CloseReason is CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown) return;
            e.Cancel = true;
            form.Hide();
            if (e.CloseReason != CloseReason.UserClosing) Log.Write($"Refused to close {form.GetType().Name} ({e.CloseReason})");
        };
        // Destroyed without being closed: its owner went (Claude's window, for the overlays) or it was
        // destroyed from outside. BeginInvoke needs a window, and only the UI thread can make one, so
        // make it here -- hidden, as there is nothing to show it over. Posted, because the old window
        // is still being torn down; through the synchronization context, whose window is message-only
        // and so out of reach of whatever destroyed this one.
        var ui = SynchronizationContext.Current;
        form.HandleDestroyed += (_, _) =>
        {
            if (Exiting || form.RecreatingHandle || GetSystemMetrics(SM_SHUTTINGDOWN) != 0) return;
            ui?.Post(_ =>
            {
                if (Exiting || form.IsHandleCreated) return;
                if (form.IsDisposed) { Restart($"{form.GetType().Name} was disposed"); return; }
                form.Hide();
                _ = form.Handle;
                Log.Write($"Recreated {form.GetType().Name}'s window, which was destroyed from outside");
            }, null);
        };
    }

    /// <summary>Starts a fresh copy and exits this one. Called on the UI thread.</summary>
    public static void Restart(string reason)
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;
        Log.Write($"Restarting: {reason}");
        if (!StartCopy(crashed: false)) return;
        Exiting = true;
        if (ExitApp != null) ExitApp(); else Application.Exit();
    }

    /// <summary>
    /// From the unhandled-exception handler: the process is about to end whatever happens, so all
    /// that is left is to start its replacement. Not if this copy itself replaced a crashed one less
    /// than a minute ago -- a crash at startup would otherwise restart forever.
    /// </summary>
    public static void RestartAfterCrash()
    {
        if (Exiting || Interlocked.Exchange(ref _restarting, 1) == 1) return;
        if (_startedAfterCrash && DateTime.Now - StartedAt < TimeSpan.FromMinutes(1))
        {
            Log.Write("Crashed again within a minute of restarting after a crash; not restarting");
            return;
        }
        if (StartCopy(crashed: true)) Log.Write("Started a new copy after the crash");
    }

    static bool StartCopy(bool crashed)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(AfterArg);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            if (crashed) start.ArgumentList.Add(CrashedArg);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't start a new copy: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

static class Ui
{
    /// <summary>
    /// BeginInvoke that never throws. Background work posts its result to a window that may be gone
    /// by then; that must cost the result, not the process.
    /// </summary>
    public static void Post(Control target, Action action)
    {
        try
        {
            if (!target.IsDisposed && target.IsHandleCreated) { target.BeginInvoke(action); return; }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        Log.Write($"Dropped an update for {target.GetType().Name}: its window is gone");
    }
}
