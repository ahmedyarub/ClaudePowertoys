namespace ClaudePowertoys;

/// <param name="Waiting">Sessions waiting for the person: Claude asked something, or finished a turn they haven't seen.</param>
/// <param name="Working">Sessions in the middle of a turn.</param>
sealed record StatusSnapshot(
    List<(SessionInfo Session, SessionStatusInfo Status)> Waiting,
    List<(SessionInfo Session, SessionStatusInfo Status)> Working);

/// <summary>
/// Every few seconds, works out the state of each recent session and reports the ones waiting for
/// the person and the ones working. The session on screen doesn't count as waiting while Claude is
/// in front: they are looking at it.
/// </summary>
sealed class StatusMonitor : IDisposable
{
    const int PollMs = 3000;

    readonly Thread _thread;
    readonly Action<StatusSnapshot> _onUpdate;
    volatile bool _stop;
    volatile Dictionary<string, SessionStatusInfo> _statuses = [];
    string _lastWaitingKey = "";

    // The app records when a session was switched to, not how long it stayed on screen, so a turn
    // that finishes while the person watches would look unseen. The monitor notes, each poll, which
    // session is on screen. Turns that ended before it started are not counted as new.
    readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    readonly Dictionary<string, long> _seenAt = [];

    public TimeSpan RecentWindow = TimeSpan.FromHours(8);

    /// <param name="onUpdate">Called on the monitor's thread after every poll.</param>
    public StatusMonitor(Action<StatusSnapshot> onUpdate)
    {
        _onUpdate = onUpdate;
        _thread = new Thread(Loop) { IsBackground = true, Name = "StatusMonitor" };
    }

    public void Start() => _thread.Start();

    public void Dispose() => _stop = true;

    /// <summary>The latest known status of a session, by its app session id.</summary>
    public SessionStatusInfo? Get(string sessionId) => _statuses.GetValueOrDefault(sessionId);

    void Loop()
    {
        while (!_stop)
        {
            try { Tick(); }
            catch (Exception ex) { Log.Write($"Status monitor failed: {ex.GetType().Name}: {ex.Message}"); }
            Thread.Sleep(PollMs);
        }
    }

    void Tick()
    {
        var sessions = RecentSessions.Scan(RecentWindow);
        var statuses = new Dictionary<string, SessionStatusInfo>();
        var waiting = new List<(SessionInfo, SessionStatusInfo)>();
        var working = new List<(SessionInfo, SessionStatusInfo)>();
        bool claudeInFront = ClaudeWindow.IsInFrontSlow();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (claudeInFront && sessions.Count > 0) _seenAt[sessions[0].SessionId] = now;

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.CliSessionId == "") continue;
            long seenAt = Math.Max(Math.Max(s.LastFocusedAt, _startedAt), _seenAt.GetValueOrDefault(s.SessionId));
            var status = SessionStatus.Read(s.CliSessionId, seenAt);
            if (status == null) continue;
            statuses[s.SessionId] = status;
            bool onScreen = i == 0 && claudeInFront;
            if (!onScreen && status.State is SessionState.NeedsYou or SessionState.NewReply) waiting.Add((s, status));
            if (status.State == SessionState.Working) working.Add((s, status));
        }
        _statuses = statuses;

        var key = string.Join("|", waiting.Select(w => $"{w.Item1.SessionId}:{w.Item2.State}"));
        if (key != _lastWaitingKey)
        {
            _lastWaitingKey = key;
            Log.Write($"Waiting sessions: {(waiting.Count == 0 ? "none" : string.Join(", ", waiting.Select(w => $"'{w.Item1.Title}' ({w.Item2.State})")))}");
        }
        _onUpdate(new StatusSnapshot(waiting, working));
    }
}
