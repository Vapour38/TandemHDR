using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
// Both UI stacks are enabled in this project and each defines a Control.
using Control = System.Windows.Forms.Control;

namespace TandemHdr.Services;

/// <summary>
/// Watches for whitelisted programs and reports the two transitions that matter: the first
/// one appearing, and the last one going away.
///
/// Deliberately edge-triggered, not level-triggered. If it asserted the desired HDR state on
/// every tick it would fight the user — flipping HDR back the moment they turned it off by
/// hand mid-game. Firing only on transitions means a manual override sticks until the next
/// launch or exit.
///
/// Three mechanisms, because Windows offers no single unelevated "a process started" event:
///
/// * <b>Exit</b> is not polled at all. Once a program is held, <see cref="Process.Exited"/>
///   reports its death through a kernel wait — instant, and free while waiting.
/// * <b>Start</b> is pushed by a foreground-window hook whenever a watched program takes
///   focus, which covers launching a game the ordinary way.
/// * <b>A slow poll</b> is the safety net for a program that starts without ever taking
///   focus, and it runs only while nothing is held — so a running game costs nothing.
///
/// Rejected: WMI's <c>__InstanceCreationEvent … WITHIN n</c> looks event-driven but polls
/// the process table inside WmiPrvSE, usually costing more than doing it here.
/// <c>Win32_ProcessStartTrace</c> and the ETW kernel process provider are true events but
/// both require elevation, which a tray utility should not demand.
/// </summary>
internal sealed class GameWatcher : IDisposable
{
    /// <summary>Only covers the gap where a watched program starts without taking focus, so
    /// it can be slow: a game takes far longer than this to reach a menu.</summary>
    private const int StartPollSeconds = 12;

    private readonly System.Windows.Forms.Timer _startPoll;

    // Process.Exited is raised on a thread pool thread. Everything downstream touches the
    // tray icon and WPF, so hand the event back to the UI thread via SynchronizingObject.
    // A bare Control is the standard ISynchronizeInvoke for that; its handle is created
    // here, on the UI thread, which is what binds the marshalling to the right thread.
    private readonly Control _marshaller;

    // Held in a field on purpose: the delegate is passed to unmanaged code, and if it were
    // collected the callback would fire into freed memory.
    private readonly WinEventProc _foregroundProc;

    private HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _hook;
    private Process? _held;
    private string? _running;
    private bool _started;
    private bool _disposed;

    /// <summary>Raised when a watched program starts and none was running before.</summary>
    public event Action<string>? FirstStarted;

    /// <summary>Raised when the last watched program exits.</summary>
    public event Action<string>? LastExited;

    public GameWatcher()
    {
        _startPoll = new System.Windows.Forms.Timer { Interval = StartPollSeconds * 1000 };
        _startPoll.Tick += (_, _) => PollForStart();

        _marshaller = new Control();
        _ = _marshaller.Handle;

        _foregroundProc = OnForegroundChanged;
    }

    /// <summary>The program currently holding HDR on, if any.</summary>
    public string? RunningProgram => _running;

    public void SetWatchList(IEnumerable<string> executablePaths)
    {
        _watched = executablePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A program removed from the list while running must not keep holding HDR on.
        if (_running != null && !_watched.Contains(_running))
        {
            ReleaseAndNotify();
            return;
        }

        // One added while already running should take effect without waiting for a relaunch.
        if (_started)
            PollForStart();
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        // WINEVENT_OUTOFCONTEXT delivers the callback on this thread's message loop, so the
        // handler is already on the UI thread and needs no marshalling of its own.
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
            Logger.Log("Foreground hook could not be installed; falling back to the start poll alone");

        PollForStart();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _startPoll.Stop();
        Release();
    }

    /// <summary>Foreground changed: the cheapest possible start signal, since the only work
    /// is a PID lookup on a window we were handed.</summary>
    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // Already holding one, or the event is for a child accessible object rather than the
        // window itself — nothing to do either way.
        if (_running != null || hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
            return;

        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0)
            return;

        Process? process = null;
        try
        {
            process = Process.GetProcessById((int)pid);
            if (!_watched.Contains(process.ProcessName))
                return;

            Hold(process, process.ProcessName);
            process = null; // ownership transferred to Hold
        }
        catch (Exception ex)
        {
            // Exited between the event and the lookup, or a process this app may not open.
            Logger.Log($"Foreground process lookup failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Safety-net scan, for a watched program that never takes the foreground. Runs
    /// only while nothing is held.</summary>
    private void PollForStart()
    {
        if (_running != null || _watched.Count == 0)
        {
            _startPoll.Stop();
            return;
        }

        Process? match = null;
        try
        {
            // One enumeration matched against the whole set. Measured as the cheapest option
            // available: per-name GetProcessesByName scales linearly and a Toolhelp32
            // snapshot is slower still.
            foreach (var process in Process.GetProcesses())
            {
                if (match == null && _watched.Contains(process.ProcessName))
                    match = process;
                else
                    process.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Process scan failed: {ex.Message}");
        }

        if (match != null)
            Hold(match, match.ProcessName);
        else
            _startPoll.Start();
    }

    private void Hold(Process process, string name)
    {
        _held = process;
        _running = name;
        _startPoll.Stop();

        try
        {
            process.SynchronizingObject = _marshaller;
            process.Exited += OnHeldExited;
            // If the process is already gone this raises Exited rather than throwing, and the
            // marshalling guarantees it lands after FirstStarted below.
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Without exit notification the hold could never be released, so give it up now
            // and let the poll pick the program up again.
            Logger.Log($"Could not subscribe to exit of {name}: {ex.Message}");
            Release();
            _startPoll.Start();
            return;
        }

        Logger.Log($"Watched program started: {name}");
        FirstStarted?.Invoke(name);
    }

    private void OnHeldExited(object? sender, EventArgs e)
    {
        // Guard against a release that already happened by another route.
        if (!ReferenceEquals(sender, _held)) return;

        Logger.Log($"Watched program exited: {_running}");
        ReleaseAndNotify();
    }

    private void ReleaseAndNotify()
    {
        string? name = _running;
        Release();

        if (_started)
            _startPoll.Start();

        if (name != null)
            LastExited?.Invoke(name);
    }

    private void Release()
    {
        if (_held != null)
        {
            _held.Exited -= OnHeldExited;
            _held.Dispose();
            _held = null;
        }

        _running = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _startPoll.Dispose();
        _marshaller.Dispose();
    }

    #region Foreground hook interop

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    #endregion
}
