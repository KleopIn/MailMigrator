using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MailMigrator.Models;
using MimeKit;

namespace MailMigrator.Services;

public sealed class MigrationStats
{
    public int Copied;
    public int Errors;
    public int DupSkipped;
    public int BoundarySkipped;
    public int LargeSkipped;
    public int DateSkipped;
}

public sealed record ProgressUpdate(
    string? Folder,
    int FolderDone,
    int FolderTotal,
    int Copied,
    int Errors,
    int DupSkipped,
    int BoundarySkipped,
    int LargeSkipped,
    int DateSkipped,
    string? LogLine);

/// <summary>
/// MailKit-based IMAP-to-IMAP migration with Message-ID dedup, INBOX date
/// boundary, size filtering, flag/INTERNALDATE preservation, and rate limiting.
/// </summary>
public sealed class ImapMigrationService
{
    private static readonly MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.Size |
        MessageSummaryItems.Envelope;

    private sealed class Conn
    {
        public required ImapClient Client { get; init; }
        public required string Host { get; init; }
        public required int Port { get; init; }
        public required bool Ssl { get; init; }
        public required string User { get; init; }
        public required string Password { get; init; }

        public async Task EnsureAsync(CancellationToken ct)
        {
            if (Client.IsConnected && Client.IsAuthenticated) return;
            if (!Client.IsConnected)
                await Client.ConnectAsync(Host, Port,
                    Ssl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
            if (!Client.IsAuthenticated)
                await Client.AuthenticateAsync(User, Password, ct);
        }
    }

    private readonly MigrationStats _stats = new();
    private readonly IProgress<ProgressUpdate>? _progress;
    private readonly Profile _o;
    private readonly CancellationToken _ct;

    public ImapMigrationService(Profile options, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        _o = options;
        _progress = progress;
        _ct = ct;
    }

    private void Log(string line)
    {
        _progress?.Report(new ProgressUpdate(null, 0, 0,
            _stats.Copied, _stats.Errors, _stats.DupSkipped, _stats.BoundarySkipped, _stats.LargeSkipped, _stats.DateSkipped, line));
    }

    private void Report(string? folder, int done, int total, string? line = null)
    {
        _progress?.Report(new ProgressUpdate(folder, done, total,
            _stats.Copied, _stats.Errors, _stats.DupSkipped, _stats.BoundarySkipped, _stats.LargeSkipped, _stats.DateSkipped, line));
    }

    public async Task<MigrationStats> RunAsync()
    {
        Log($"{TimeStamp()}  START  source={_o.SourceHost}:{_o.SourcePort} ({_o.SourceUser})  dest={_o.DestHost}:{_o.DestPort} ({_o.DestUser})");

        using var src = new ImapClient();
        using var dst = new ImapClient();
        var sc = new Conn { Client = src, Host = _o.SourceHost, Port = _o.SourcePort, Ssl = _o.SourceSsl, User = _o.SourceUser, Password = _o.SourcePassword };
        var dc = new Conn { Client = dst, Host = _o.DestHost, Port = _o.DestPort, Ssl = _o.DestSsl, User = _o.DestUser, Password = _o.DestPassword };

        Log("Connecting to source...");
        await sc.EnsureAsync(_ct);
        Log($"Source connected: {_o.SourceUser}");

        Log("Connecting to destination...");
        await dc.EnsureAsync(_ct);
        Log($"Destination connected: {_o.DestUser}");

        var srcSent = FindSentFolder(src);
        var dstSent = FindSentFolder(dst);

        if (_o.MigrateInbox)
            await MigrateFolderAsync(sc, src.Inbox, dc, dst.Inbox, isInbox: true);

        if (_o.MigrateSent)
        {
            if (srcSent is null)
            {
                Log("WARN: Sent folder not found on source — skipping Sent.");
            }
            else
            {
                IMailFolder? target = dstSent;
                if (target is null && dst.Inbox.ParentFolder is { } parent)
                {
                    try { target = parent.Create("Sent Items", false); }
                    catch { target = null; }
                }

                if (target is null)
                    Log("WARN: Sent folder not found on destination and could not be created — skipping Sent.");
                else
                    await MigrateFolderAsync(sc, srcSent, dc, target, isInbox: false);
            }
        }

        Log($"{TimeStamp()}  ALL DONE  copied={_stats.Copied} errors={_stats.Errors} alreadyOnDest={_stats.DupSkipped + _stats.BoundarySkipped} largeSkipped={_stats.LargeSkipped} outOfRange={_stats.DateSkipped}");
        return _stats;
    }

    private async Task MigrateFolderAsync(Conn sc, IMailFolder srcFolder, Conn dc, IMailFolder dstFolder, bool isInbox)
    {
        _ct.ThrowIfCancellationRequested();
        await srcFolder.OpenAsync(FolderAccess.ReadOnly, _ct);
        await dstFolder.OpenAsync(FolderAccess.ReadWrite, _ct);

        Log($"{TimeStamp()}  START [{srcFolder.Name}]  src={srcFolder.Count}  dest={dstFolder.Count}");

        // 1) Build the destination Message-ID set for dedup.
        var destIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var destSummaries = await dstFolder.FetchAsync(0, -1, MessageSummaryItems.Envelope, _ct);
            foreach (var s in destSummaries)
                if (s.Envelope?.MessageId is { } mid)
                    destIds.Add(mid.Trim().ToLowerInvariant());
        }
        catch (Exception ex) { Log($"WARN: could not read destination Message-IDs: {ex.Message}"); }

        // 2) INBOX date boundary (resume after a forward/copy that regenerated Message-IDs).
        DateTimeOffset? destOldest = null;
        if (isInbox && _o.DateBoundaryForInbox)
        {
            try
            {
                var first = (await dstFolder.FetchAsync(0, 1, MessageSummaryItems.InternalDate, _ct)).FirstOrDefault();
                destOldest = first?.InternalDate;
                if (destOldest is { } d) Log($"  inbox boundary: skip source msgs with date >= {d:yyyy-MM-dd HH:mm zzz}");
            }
            catch (Exception ex) { Log($"WARN: could not read destination oldest date: {ex.Message}"); }
        }

        // 3) Enumerate source messages (batched summaries), with retries for
        //    transient server errors (e.g. mail.ru "Service temporarily unavailable").
        IList<IMessageSummary> summaries;
        try
        {
            summaries = await FetchSummariesWithRetryAsync(sc, srcFolder, _ct);
        }
        catch (Exception ex)
        {
            Log($"ERROR: could not list source folder [{srcFolder.Name}] after retries: {ex.Message}");
            return;
        }

        var doneIds = new HashSet<string>(StringComparer.Ordinal);
        int total = summaries.Count;
        int done = 0;
        int consecutiveErrors = 0;

        foreach (var s in summaries)
        {
            _ct.ThrowIfCancellationRequested();
            try
            {
                if (s.Size is { } size && (long)size > _o.MaxMessageBytes)
                {
                    _stats.LargeSkipped++;
                    Log($"{TimeStamp()}  SKIP-LARGE uid={s.UniqueId} bytes={size}");
                    continue;
                }

                // Explicit date-range filter (inclusive, applies to every folder).
                if (_o.DateFilterEnabled && s.InternalDate is { } msgDate)
                {
                    var localDate = msgDate.LocalDateTime.Date;
                    if (_o.DateFrom is { } from && localDate < from.Date) { _stats.DateSkipped++; continue; }
                    if (_o.DateTo is { } to && localDate > to.Date) { _stats.DateSkipped++; continue; }
                }

                if (isInbox && destOldest is { } oldest && s.InternalDate is { } idate && idate >= oldest)
                {
                    _stats.BoundarySkipped++;
                    continue;
                }

                var mid = s.Envelope?.MessageId?.Trim().ToLowerInvariant();
                if (mid is not null && (destIds.Contains(mid) || doneIds.Contains(mid)))
                {
                    _stats.DupSkipped++;
                    continue;
                }

                var message = await srcFolder.GetMessageAsync(s.UniqueId, _ct);
                var flags = s.Flags ?? MessageFlags.None;
                DateTimeOffset? date = s.InternalDate;
                if (date is null && message.Date != DateTimeOffset.MinValue) date = message.Date;

                var request = date is { } d
                    ? new AppendRequest(message, flags, d)
                    : new AppendRequest(message, flags);
                dstFolder.Append(request, _ct);
                if (mid is not null) doneIds.Add(mid);
                _stats.Copied++;
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _stats.Errors++;
                consecutiveErrors++;
                Log($"{TimeStamp()}  ERROR uid={s.UniqueId} : {ex.Message}");
                try { await sc.EnsureAsync(_ct); await dc.EnsureAsync(_ct); } catch { }
            }
            finally
            {
                done++;
                if (done % 100 == 0 || done == total)
                    Report(srcFolder.Name, done, total,
                        $"{TimeStamp()}  progress done={done}/{total} copied={_stats.Copied} errors={_stats.Errors}");
            }

            // Rate limit between messages; back off harder after consecutive failures.
            if (_o.RateDelayMs > 0)
            {
                int delay = _o.RateDelayMs;
                if (consecutiveErrors > 0)
                    delay = Math.Min(30000, 1000 * (1 << Math.Min(consecutiveErrors, 5)));
                try { await Task.Delay(delay, _ct); } catch (OperationCanceledException) { throw; }
            }
        }

        Log($"{TimeStamp()}  DONE [{srcFolder.Name}]  copied={_stats.Copied} errors={_stats.Errors} alreadyOnDest={_stats.DupSkipped + _stats.BoundarySkipped} largeSkipped={_stats.LargeSkipped} outOfRange={_stats.DateSkipped}");
    }

    /// <summary>
    /// Fetches all source summaries, retrying transient IMAP errors (e.g. mail.ru
    /// "Service temporarily unavailable") with exponential backoff and reconnect.
    /// </summary>
    private async Task<IList<IMessageSummary>> FetchSummariesWithRetryAsync(Conn sc, IMailFolder folder, CancellationToken ct)
    {
        const int maxAttempts = 5;
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await folder.FetchAsync(0, -1, SummaryItems, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == maxAttempts) break;
                Log($"  WARN: list [{folder.Name}] failed ({ex.Message}) — retry {attempt}/{maxAttempts - 1}...");
                try { await sc.EnsureAsync(ct); } catch { }
                int delayMs = Math.Min(30000, 2000 * (1 << (attempt - 1)));
                try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { throw; }
            }
        }
        throw last!;
    }

    private static IMailFolder? FindSentFolder(ImapClient client)
    {
        var all = new List<IMailFolder>();
        try
        {
            if (client.PersonalNamespaces.Count > 0)
            {
                foreach (var ns in client.PersonalNamespaces)
                    all.AddRange(client.GetFolder(ns).GetSubfolders(false));
            }
            else if (client.Inbox.ParentFolder is { } root)
            {
                all.AddRange(root.GetSubfolders(false));
            }
        }
        catch { }
        try { all.AddRange(client.Inbox.GetSubfolders(false)); } catch { }

        return all.FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.Sent))
            ?? all.FirstOrDefault(f => f.Name.Equals("Sent", StringComparison.OrdinalIgnoreCase)
                                     || f.Name.Equals("Sent Items", StringComparison.OrdinalIgnoreCase)
                                     || f.Name.Equals("Отправленные", StringComparison.Ordinal));
    }

    private static string TimeStamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}
