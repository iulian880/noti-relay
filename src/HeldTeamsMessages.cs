namespace NotiRelay;

/// <summary>What happens to a Teams message from before monitoring started that was read late.</summary>
public enum FromBeforeOutcome
{
    /// <summary>Newly held: the user is asked about it.</summary>
    Held,

    /// <summary>Already held through another file carrying the same message.</summary>
    AlreadyHeld,

    /// <summary>The user chose to start from now: skip it without asking.</summary>
    Skip
}

/// <summary>Held messages the user has answered, for the inbox worker to apply.</summary>
public sealed record DecidedTeamsMessages(
    IReadOnlyList<MissedTeamsMessage> ToSend,
    IReadOnlyList<MissedTeamsMessage> ToSkip);

/// <summary>
/// Teams messages from before monitoring started, held until the user answers: missed while
/// NotiRelay was closed, while monitoring was stopped or while the PC slept, including files
/// OneDrive syncs only after the launch question. A message counts as "from before" by its own
/// time, not by when its file shows up, so a late file is held instead of sent.
/// Thread-safe: the inbox worker adds and applies, the UI thread reads and answers.
/// Platform-neutral so it is unit-tested on any OS.
/// </summary>
public sealed class HeldTeamsMessages
{
    private enum Decision { None, Send, Skip }

    private sealed class Entry(MissedTeamsMessage message)
    {
        public MissedTeamsMessage Message { get; set; } = message;
        public Decision Decision { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);

    // Null while monitoring is not running: nothing counts as from before.
    private DateTimeOffset? _fromBeforeUtc;

    // The user answered No: the rest of this run skips old messages without asking.
    private bool _skipFromBefore;

    /// <summary>
    /// A monitoring run starts: messages older than <paramref name="fromBeforeUtc"/> are held,
    /// beginning with those the launch scan already found.
    /// </summary>
    public void Start(DateTimeOffset fromBeforeUtc, IEnumerable<MissedTeamsMessage>? foundAtStart = null)
    {
        lock (_lock)
        {
            ClearLocked();
            _fromBeforeUtc = fromBeforeUtc;

            foreach (MissedTeamsMessage missed in foundAtStart ?? Array.Empty<MissedTeamsMessage>())
                AddLocked(missed);
        }
    }

    /// <summary>Monitoring stopped: unanswered messages lapse and are offered again next time.</summary>
    public void Clear()
    {
        lock (_lock)
            ClearLocked();
    }

    /// <summary>
    /// The PC woke up: what was written before now is from before again, and the user is
    /// asked again even after an earlier No. Never moves back; ignored while not running.
    /// </summary>
    public void MoveCutoff(DateTimeOffset fromBeforeUtc)
    {
        lock (_lock)
        {
            if (_fromBeforeUtc is not { } current)
                return;

            if (fromBeforeUtc > current)
                _fromBeforeUtc = fromBeforeUtc;

            _skipFromBefore = false;
        }
    }

    public bool IsFromBefore(DateTimeOffset messageTimeUtc)
    {
        lock (_lock)
            return _fromBeforeUtc is { } cutoff && messageTimeUtc < cutoff;
    }

    /// <summary>A held file waits for the answer: the inbox scan leaves it alone.</summary>
    public bool ContainsFile(string fileName)
    {
        lock (_lock)
            return _fileNames.Contains(fileName);
    }

    public FromBeforeOutcome Add(MissedTeamsMessage missed)
    {
        lock (_lock)
        {
            if (_held.TryGetValue(missed.Key, out Entry? entry))
            {
                entry.Message = entry.Message with
                {
                    FileNames = entry.Message.FileNames
                        .Concat(missed.FileNames)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                foreach (string name in missed.FileNames)
                    _fileNames.Add(name);

                return FromBeforeOutcome.AlreadyHeld;
            }

            if (_skipFromBefore)
                return FromBeforeOutcome.Skip;

            AddLocked(missed);
            return FromBeforeOutcome.Held;
        }
    }

    /// <summary>Held messages the user has not answered yet, oldest first.</summary>
    public IReadOnlyList<MissedTeamsMessage> Undecided()
    {
        lock (_lock)
            return Ordered(Decision.None);
    }

    /// <summary>
    /// The user's answer. Yes sends exactly the messages they were told about; anything held
    /// since then waits for another question. No skips everything held and, for the rest of
    /// this run, every old message that is still to arrive.
    /// </summary>
    public void Resolve(bool send, IEnumerable<string> askedKeys)
    {
        lock (_lock)
        {
            if (!send)
            {
                _skipFromBefore = true;

                foreach (Entry entry in _held.Values.Where(entry => entry.Decision == Decision.None))
                    entry.Decision = Decision.Skip;

                return;
            }

            foreach (string key in askedKeys)
            {
                if (_held.TryGetValue(key, out Entry? entry) && entry.Decision == Decision.None)
                    entry.Decision = Decision.Send;
            }
        }
    }

    public DecidedTeamsMessages Decided()
    {
        lock (_lock)
            return new DecidedTeamsMessages(Ordered(Decision.Send), Ordered(Decision.Skip));
    }

    /// <summary>The worker queued or skipped these: forget them and release their files.</summary>
    public void Remove(IEnumerable<string> keys)
    {
        lock (_lock)
        {
            foreach (string key in keys)
            {
                if (!_held.Remove(key, out Entry? entry))
                    continue;

                foreach (string name in entry.Message.FileNames)
                    _fileNames.Remove(name);
            }
        }
    }

    private void AddLocked(MissedTeamsMessage missed)
    {
        _held[missed.Key] = new Entry(missed);

        foreach (string name in missed.FileNames)
            _fileNames.Add(name);
    }

    private void ClearLocked()
    {
        _held.Clear();
        _fileNames.Clear();
        _fromBeforeUtc = null;
        _skipFromBefore = false;
    }

    private List<MissedTeamsMessage> Ordered(Decision decision) =>
        _held.Values
            .Where(entry => entry.Decision == decision)
            .Select(entry => entry.Message)
            .OrderBy(missed => missed.SortTimeUtc)
            .ToList();
}
