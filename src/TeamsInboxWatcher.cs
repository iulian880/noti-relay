
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NotiRelay;

public sealed class TeamsInboxWatcher
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NotiRelay");

    private static readonly string ProcessedPath =
        Path.Combine(StateDirectory, "teams-processed.dat");

    private static readonly string BaselinePath =
        Path.Combine(StateDirectory, "teams-inbox-baseline.dat");

    // A file that still cannot be parsed after this many attempts (about
    // one minute at 10 s apart) is skipped instead of retried forever.
    internal const int MaxProcessingAttempts = 6;

    private readonly DeliveryQueue _queue;
    private readonly Func<string> _topicProvider;
    private readonly Action<string> _log;
    private readonly Action? _onOlderHeld;

    private readonly HashSet<string> _processedMessageKeys;
    private readonly HashSet<string> _baselineFileNames;

    private readonly Dictionary<string, FileState> _fileStates =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cancellation;
    private Task? _workerTask;
    private string _inboxPath = string.Empty;

    // Messages from before monitoring started, held until the user answers: found at
    // launch, or read later (OneDrive still syncing, PC woke up). Thread-safe.
    private readonly HeldTeamsMessages _held = new();
    private bool _heldTopicWarningShown;

    // Counted during one inbox scan, reported once after it.
    private int _scanHeld;
    private int _scanSkipped;

    public string? RecoveryWarning { get; private set; }

    /// <param name="onOlderHeld">
    /// Called on the worker thread when old messages were held after monitoring started,
    /// so the UI can ask about them.
    /// </param>
    public TeamsInboxWatcher(
        DeliveryQueue queue,
        Func<string> topicProvider,
        Action<string> log,
        Action? onOlderHeld = null)
    {
        _queue = queue;
        _topicProvider = topicProvider;
        _log = log;
        _onOlderHeld = onOlderHeld;

        Directory.CreateDirectory(StateDirectory);

        _processedMessageKeys = LoadProcessedKeys();
        _baselineFileNames = LoadBaselineFileNames();
    }

    /// <summary>
    /// Inbox messages that were never forwarded (they arrived while NotiRelay was closed).
    /// Call only while monitoring is stopped. Files that cannot be read or parsed yet are
    /// left out and follow the normal path once monitoring runs.
    /// </summary>
    public IReadOnlyList<MissedTeamsMessage> FindUnsent(string inboxPath)
    {
        if (_workerTask is not null)
        {
            throw new InvalidOperationException(
                "Stop Teams monitoring before looking for unsent messages.");
        }

        // First run: Start will baseline every existing file, so nothing counts as missed.
        if (!File.Exists(BaselinePath) || !Directory.Exists(inboxPath))
            return Array.Empty<MissedTeamsMessage>();

        var files = new List<(string FileName, TeamsInboxMessage Message, DateTimeOffset FileTimeUtc)>();

        foreach (string path in Directory.GetFiles(
                     inboxPath,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);

            if (_baselineFileNames.Contains(name))
                continue;

            try
            {
                if (TeamsMessageParser.TryParse(
                        File.ReadAllText(path),
                        out TeamsInboxMessage? message,
                        out _) &&
                    message is not null)
                {
                    files.Add((
                        name,
                        message,
                        new DateTimeOffset(File.GetLastWriteTimeUtc(path))));
                }
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException)
            {
                // Still syncing, locked or not downloaded yet: the normal scan reads it
                // later and holds it, since it was written before monitoring started.
            }
        }

        return MissedMessages.SelectUnsent(
            files,
            _baselineFileNames,
            _processedMessageKeys);
    }

    /// <summary>Held messages from before that the user has not answered yet, oldest first.</summary>
    public IReadOnlyList<MissedTeamsMessage> UndecidedHeld() => _held.Undecided();

    /// <summary>
    /// Records the user's answer. Yes sends exactly <paramref name="askedKeys"/>; No skips
    /// everything held and every old message still to arrive in this run. The worker
    /// applies it on its own thread at the start of its next loop.
    /// </summary>
    public void ResolveHeld(bool send, IEnumerable<string> askedKeys) =>
        _held.Resolve(send, askedKeys);

    public void Start(string inboxPath, IReadOnlyList<MissedTeamsMessage>? held = null)
    {
        if (_workerTask is not null)
            return;

        if (string.IsNullOrWhiteSpace(inboxPath))
            throw new ArgumentException("Teams inbox path is empty.");

        if (!Directory.Exists(inboxPath))
        {
            throw new DirectoryNotFoundException(
                "Teams inbox folder does not exist: " + inboxPath);
        }

        _inboxPath = Path.GetFullPath(inboxPath);

        // First run: ignore files already present in the inbox.
        if (!File.Exists(BaselinePath))
        {
            string[] existingFiles = Directory.GetFiles(
                _inboxPath,
                "*.json",
                SearchOption.TopDirectoryOnly);

            foreach (string path in existingFiles)
            {
                var info = new FileInfo(path);

                _baselineFileNames.Add(Path.GetFileName(path));

                _fileStates[path] = new FileState(
                    info.Length,
                    info.LastWriteTimeUtc)
                {
                    Processed = true
                };
            }

            SaveBaselineFileNames();

            Report(
                $"Initial Teams inbox baseline created. " +
                $"Existing files ignored: {existingFiles.Length}.");
        }
        else
        {
            // Restore the original baseline after application restart.
            foreach (string path in Directory.GetFiles(
                         _inboxPath,
                         "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                if (!_baselineFileNames.Contains(Path.GetFileName(path)))
                    continue;

                var info = new FileInfo(path);

                _fileStates[path] = new FileState(
                    info.Length,
                    info.LastWriteTimeUtc)
                {
                    Processed = true
                };
            }

            Report(
                $"Teams inbox baseline restored. " +
                $"Tracked existing files: {_baselineFileNames.Count}.");
        }

        // Anything written before this moment is from before: held, never sent unasked,
        // even when its file only appears later.
        _held.Start(DateTimeOffset.UtcNow, held);
        _heldTopicWarningShown = false;

        if (held is { Count: > 0 })
        {
            Report(
                $"Teams messages missed while closed: {held.Count}. " +
                "Held until you answer.");
        }

        _cancellation = new CancellationTokenSource();

        Report("Teams inbox monitoring started: " + _inboxPath);

        _workerTask = RunAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
            return;

        _cancellation.Cancel();

        if (_workerTask is not null)
            await _workerTask;

        _cancellation.Dispose();
        _cancellation = null;
        _workerTask = null;

        // An unanswered question lapses: those messages are still unsent and will be
        // offered again on the next start.
        _held.Clear();

        Report("Teams inbox monitoring stopped.");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ApplyHeldDecisionAsync(cancellationToken);

                await ScanInboxAsync(cancellationToken);

                DateTimeOffset pausedUtc = DateTimeOffset.UtcNow;

                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    cancellationToken);

                NoticeSleep(pausedUtc, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DebugTrace.Detail("Teams inbox worker error", ex);

                Report(
                    "Teams inbox worker error: " +
                    ex.GetType().Name + ": " + ex.Message);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Report("Teams inbox worker stopped.");
    }

    // The PC slept through the scan pause: messages written while it slept and synced only
    // now are from before, so they are asked about instead of sent.
    private void NoticeSleep(DateTimeOffset pausedUtc, DateTimeOffset resumedUtc)
    {
        if (!MissedMessages.SleptBetween(pausedUtc, resumedUtc))
            return;

        _held.MoveCutoff(resumedUtc);

        Report(
            $"Teams monitoring resumed after about {FormatGap(resumedUtc - pausedUtc)} " +
            "(the PC slept). Older Teams messages are held until you answer.");
    }

    private static string FormatGap(TimeSpan gap) =>
        gap.TotalHours >= 1
            ? $"{(int)gap.TotalHours} h {gap.Minutes} min"
            : $"{Math.Max(1, (int)gap.TotalMinutes)} min";

    private async Task ApplyHeldDecisionAsync(CancellationToken cancellationToken)
    {
        DecidedTeamsMessages decided = _held.Decided();

        if (decided.ToSkip.Count > 0)
        {
            // Record the keys, not just the file names: a later OneDrive touch on a
            // skipped file must not send it after all.
            foreach (MissedTeamsMessage missed in decided.ToSkip)
                _processedMessageKeys.Add(missed.Key);

            SaveProcessedKeys();
            _held.Remove(decided.ToSkip.Select(missed => missed.Key));

            Report($"Missed Teams messages skipped: {decided.ToSkip.Count}.");
        }

        if (decided.ToSend.Count == 0)
            return;

        string topic = _topicProvider();

        if (string.IsNullOrWhiteSpace(topic))
        {
            if (!_heldTopicWarningShown)
            {
                _heldTopicWarningShown = true;
                Report("Missed Teams messages are waiting: ntfy topic is not configured.");
            }

            return;
        }

        DateTime nowLocal = DateTime.Now;
        int queued = 0;

        // Oldest first; keys are recorded one by one so a failure part-way never
        // sends the same message twice when the worker retries.
        foreach (MissedTeamsMessage missed in decided.ToSend)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_processedMessageKeys.Contains(missed.Key))
            {
                TeamsInboxMessage message = missed.Message;

                string sender = string.IsNullOrWhiteSpace(message.SenderName)
                    ? "Unknown sender"
                    : message.SenderName.Trim();

                await _queue.EnqueueAsync(
                    "teams:" + missed.Key,
                    MissedMessages.Body(message.Text, missed.SortTimeUtc.LocalDateTime, nowLocal),
                    MissedMessages.Title(sender),
                    "high",
                    topic);

                _processedMessageKeys.Add(missed.Key);

                try
                {
                    SaveProcessedKeys();
                }
                catch
                {
                    _processedMessageKeys.Remove(missed.Key);
                    throw;
                }

                queued++;
            }

            _held.Remove(new[] { missed.Key });
        }

        Report($"Missed Teams messages queued: {queued}.");
    }

    private async Task ScanInboxAsync(
        CancellationToken cancellationToken)
    {
        string[] files;

        try
        {
            files = Directory.GetFiles(
                _inboxPath,
                "*.json",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            DebugTrace.Detail("Cannot scan Teams inbox", ex);
            Report("Cannot scan Teams inbox: " + ex.Message);
            return;
        }

        var presentFiles = new HashSet<string>(
            files,
            StringComparer.OrdinalIgnoreCase);

        _scanHeld = 0;
        _scanSkipped = 0;

        foreach (string oldPath in _fileStates.Keys
                     .Where(path => !presentFiles.Contains(path))
                     .ToList())
        {
            _fileStates.Remove(oldPath);
        }

        foreach (string path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                    continue;

                // From before monitoring started: waits for the user's answer.
                if (_held.ContainsFile(info.Name))
                    continue;

                if (!_fileStates.TryGetValue(path, out FileState? state))
                {
                    bool existedInBaseline =
                        _baselineFileNames.Contains(Path.GetFileName(path));

                    _fileStates[path] = new FileState(
                        info.Length,
                        info.LastWriteTimeUtc)
                    {
                        Processed = existedInBaseline
                    };

                    continue;
                }

                // Detect changes during OneDrive synchronization.
                if (state.Length != info.Length ||
                    state.LastWriteTimeUtc != info.LastWriteTimeUtc)
                {
                    state.Length = info.Length;
                    state.LastWriteTimeUtc = info.LastWriteTimeUtc;
                    state.StableScans = 0;
                    state.Processed = false;
                    state.FailedAttempts = 0;
                    state.RetryAfterUtc = DateTime.MinValue;
                    continue;
                }

                if (state.Processed)
                    continue;

                // Require two stable observations before reading.
                if (state.StableScans < 1)
                {
                    state.StableScans++;
                    continue;
                }

                if (state.RetryAfterUtc > DateTime.UtcNow)
                    continue;

                await ProcessFileAsync(
                    path,
                    state,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DebugTrace.Detail(
                    $"Teams file processing error ({Path.GetFileName(path)})", ex);

                Report(
                    $"Teams file processing error " +
                    $"({Path.GetFileName(path)}): {ex.Message}");
            }
        }

        if (_scanSkipped > 0)
        {
            Report(
                $"Older Teams messages skipped (you chose to start from now): {_scanSkipped}.");
        }

        if (_scanHeld > 0)
        {
            Report(
                $"Older Teams messages found after start: {_scanHeld}. " +
                "Held until you answer.");

            try
            {
                _onOlderHeld?.Invoke();
            }
            catch
            {
                // The question is asked on the UI thread; a failure here must not stop the scan.
            }
        }
    }

    private async Task ProcessFileAsync(
        string path,
        FileState state,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(
                path,
                cancellationToken);

            if (!TeamsMessageParser.TryParse(
                    json,
                    out TeamsInboxMessage? message,
                    out string error,
                    out TeamsInboxMessage? nothingToForward) ||
                message is null)
            {
                if (nothingToForward is not null)
                {
                    // Complete but empty (system event, deleted message...): record it
                    // once so it is not retried and logged again on every launch.
                    SkipPermanently(nothingToForward, error, state);
                    return;
                }

                RetryLater(state, error, path);
                return;
            }

            string messageKey = MissedMessages.MessageKey(message);

            if (_processedMessageKeys.Contains(messageKey))
            {
                state.Processed = true;
                return;
            }

            // Written before monitoring started (OneDrive synced it late, or the PC slept):
            // never sent without asking.
            DateTimeOffset writtenUtc =
                message.CreatedAt ?? new DateTimeOffset(state.LastWriteTimeUtc);

            if (_held.IsFromBefore(writtenUtc))
            {
                HoldOrSkipOlder(path, messageKey, message, writtenUtc, state);
                return;
            }

            string topic = _topicProvider();

            if (string.IsNullOrWhiteSpace(topic))
            {
                // Configuration problem, not a bad file: keep waiting.
                RetryLater(
                    state,
                    "ntfy topic is not configured.",
                    path,
                    countAttempt: false);
                return;
            }

            string sender =
                string.IsNullOrWhiteSpace(message.SenderName)
                    ? "Unknown sender"
                    : message.SenderName.Trim();

            // Notification title: sender only.
            string title = sender;

            if (title.Length > 120)
                title = title[..120];

            // Notification body: message only.
            string payload = message.Text;

            bool added = await _queue.EnqueueAsync(
                "teams:" + messageKey,
                payload,
                title,
                "high",
                topic);

            // Persist processed ID after the durable queue operation.
            _processedMessageKeys.Add(messageKey);

            try
            {
                SaveProcessedKeys();
            }
            catch
            {
                _processedMessageKeys.Remove(messageKey);
                throw;
            }

            state.Processed = true;
            state.RetryAfterUtc = DateTime.MinValue;
            state.FailedAttempts = 0;

            Report(added
                ? $"Teams message queued: {sender}. " +
                  $"Message ID: {message.MessageId}"
                : $"Teams message already exists in delivery queue: " +
                  message.MessageId);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugTrace.Detail(
                $"Teams file will be retried ({Path.GetFileName(path)})", ex);

            RetryLater(
                state,
                ex.GetType().Name + ": " + ex.Message,
                path);
        }
    }

    private void HoldOrSkipOlder(
        string path,
        string messageKey,
        TeamsInboxMessage message,
        DateTimeOffset writtenUtc,
        FileState state)
    {
        state.RetryAfterUtc = DateTime.MinValue;
        state.FailedAttempts = 0;

        FromBeforeOutcome outcome = _held.Add(new MissedTeamsMessage(
            new[] { Path.GetFileName(path) },
            messageKey,
            message,
            writtenUtc));

        if (outcome == FromBeforeOutcome.Held)
        {
            // Left unprocessed: the scan skips held files, and once the answer is applied
            // the file is read again and found processed.
            _scanHeld++;
            return;
        }

        if (outcome != FromBeforeOutcome.Skip)
            return;

        _processedMessageKeys.Add(messageKey);

        try
        {
            SaveProcessedKeys();
        }
        catch
        {
            _processedMessageKeys.Remove(messageKey);
            throw;
        }

        state.Processed = true;
        _scanSkipped++;
    }

    private void SkipPermanently(
        TeamsInboxMessage message,
        string reason,
        FileState state)
    {
        string messageKey = MissedMessages.MessageKey(message);

        state.Processed = true;
        state.RetryAfterUtc = DateTime.MinValue;
        state.FailedAttempts = 0;

        if (!_processedMessageKeys.Add(messageKey))
            return;

        try
        {
            SaveProcessedKeys();
        }
        catch
        {
            _processedMessageKeys.Remove(messageKey);
            throw;
        }

        Report("Teams message skipped, nothing to forward: " + reason);
    }

    private void RetryLater(
        FileState state,
        string reason,
        string path,
        bool countAttempt = true)
    {
        if (countAttempt)
            state.FailedAttempts++;

        if (countAttempt &&
            state.FailedAttempts >= MaxProcessingAttempts)
        {
            // Give up on this file version; a later change to the file
            // (e.g. OneDrive finishing a sync) resets the state and retries.
            state.Processed = true;
            state.RetryAfterUtc = DateTime.MinValue;

            Report(
                $"Teams file skipped after {state.FailedAttempts} " +
                $"failed attempts ({Path.GetFileName(path)}): {reason}");

            return;
        }

        state.RetryAfterUtc = DateTime.UtcNow.AddSeconds(10);

        if (DateTime.UtcNow - state.LastErrorLoggedUtc <
            TimeSpan.FromSeconds(30))
        {
            return;
        }

        state.LastErrorLoggedUtc = DateTime.UtcNow;

        Report("Teams inbox retry scheduled: " + reason);
    }

    private HashSet<string> LoadProcessedKeys()
    {
        if (!File.Exists(ProcessedPath))
            return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            byte[] encrypted = File.ReadAllBytes(ProcessedPath);

            byte[] decrypted = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);

            try
            {
                string[] keys =
                    JsonSerializer.Deserialize<string[]>(decrypted)
                    ?? Array.Empty<string>();

                return new HashSet<string>(
                    keys,
                    StringComparer.Ordinal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
        catch (Exception ex) when (
            ex is CryptographicException ||
            ex is JsonException ||
            ex is FormatException ||
            ex is IOException)
        {
            string backupPath = ProcessedPath +
                ".unreadable-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            try
            {
                File.Move(ProcessedPath, backupPath);

                RecoveryWarning =
                    "Teams processed-message registry was unreadable. " +
                    "Original preserved as " +
                    Path.GetFileName(backupPath) + ".";
            }
            catch (Exception moveException)
            {
                RecoveryWarning =
                    "Teams processed-message registry recovery failed: " +
                    moveException.Message;
            }

            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private HashSet<string> LoadBaselineFileNames()
    {
        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(BaselinePath))
            return names;

        try
        {
            byte[] encrypted = File.ReadAllBytes(BaselinePath);

            byte[] decrypted = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);

            try
            {
                string[] stored =
                    JsonSerializer.Deserialize<string[]>(decrypted)
                    ?? Array.Empty<string>();

                foreach (string name in stored)
                    names.Add(name);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
        catch (Exception ex) when (
            ex is CryptographicException ||
            ex is JsonException ||
            ex is FormatException ||
            ex is IOException)
        {
            string backupPath = BaselinePath +
                ".unreadable-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            try
            {
                File.Move(BaselinePath, backupPath);

                RecoveryWarning =
                    "Teams inbox baseline was unreadable. " +
                    "Original preserved as " +
                    Path.GetFileName(backupPath) + ".";
            }
            catch (Exception moveException)
            {
                RecoveryWarning =
                    "Teams inbox baseline recovery failed: " +
                    moveException.Message;
            }
        }

        return names;
    }

    private void SaveBaselineFileNames()
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
            _baselineFileNames.OrderBy(
                name => name,
                StringComparer.OrdinalIgnoreCase).ToArray());

        byte[] encrypted = ProtectedData.Protect(
            plain,
            null,
            DataProtectionScope.CurrentUser);

        string temporaryPath = BaselinePath + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);

            File.Move(
                temporaryPath,
                BaselinePath,
                overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(encrypted);

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void SaveProcessedKeys()
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
            _processedMessageKeys.OrderBy(key => key).ToArray());

        byte[] encrypted = ProtectedData.Protect(
            plain,
            null,
            DataProtectionScope.CurrentUser);

        string temporaryPath = ProcessedPath + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);

            File.Move(
                temporaryPath,
                ProcessedPath,
                overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(encrypted);

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void Report(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Logging must never interrupt message ingestion.
        }
    }

    private sealed class FileState
    {
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public int StableScans { get; set; }
        public bool Processed { get; set; }
        public DateTime RetryAfterUtc { get; set; }
        public DateTime LastErrorLoggedUtc { get; set; }
        public int FailedAttempts { get; set; }

        public FileState(long length, DateTime lastWriteTimeUtc)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }
    }
}
