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

    // Keyed by executable file name, which is what a running process can be matched on
    // cheaply; the values are the full paths configured for that name, used to tell two
    // different programs sharing a file name apart. See Matches.
    private Dictionary<string, List<string>> _watched = new(StringComparer.OrdinalIgnoreCase);
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
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // A program removed from the list while running must not keep holding HDR on.
        if (_running != null && !_watched.ContainsKey(_running))
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
            if (!Matches(process))
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
                if (match == null && Matches(process))
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

    /// <summary>
    /// Decides whether a running process is one of the watched entries.
    ///
    /// The file name selects the candidates, because that is what survives the cases the
    /// scanner exists for — launcher stubs, anti-cheat relaunches, an install moved to
    /// another drive. The full path is then used only to <i>rule a candidate out</i>, and
    /// only when both halves of the comparison are trustworthy: the running process's path
    /// could be read, and at least one configured path for that name is still on disk.
    ///
    /// Anything less certain falls back to the file name alone, which is what this matched
    /// on before. So the change only ever rejects a confident mismatch — two different
    /// programs that happen to share an executable name.
    /// </summary>
    private bool Matches(Process process)
    {
        if (!_watched.TryGetValue(process.ProcessName, out var configured))
            return false;

        string? actual = TryGetExecutablePath(process);
        if (actual == null)
            return true;

        if (configured.Any(p => string.Equals(p, actual, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!configured.Any(File.Exists))
        {
            // The configured copy is gone, so this is almost certainly the same program
            // reinstalled or moved rather than an impostor.
            Logger.Log($"{process.ProcessName} matched on file name; no configured path still exists");
            return true;
        }

        Logger.Log($"Ignoring {process.ProcessName} at {actual}: a different program of that name is watched");
        return false;
    }

    /// <summary>The running process's own executable path, or null when Windows will not
    /// say. Uses QueryFullProcessImageName rather than <see cref="ProcessModule"/>: it needs
    /// only PROCESS_QUERY_LIMITED_INFORMATION, which an unelevated caller is granted for far
    /// more processes — including the elevated and anti-cheat-protected ones this watches.</summary>
    private static string? TryGetExecutablePath(Process process)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.Id);
            if (handle == IntPtr.Zero)
                return null;

            var buffer = new System.Text.StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch (Exception ex)
        {
            // Exited between the match and here, most likely.
            Logger.Log($"Could not read the path of {process.ProcessName}: {ex.Message}");
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags,
        System.Text.StringBuilder exeName, ref uint size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    #endregion
}
