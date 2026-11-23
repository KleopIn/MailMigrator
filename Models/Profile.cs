using System;

namespace MailMigrator.Models;

/// <summary>
/// Everything the migration needs. Passwords are held in plaintext only in
/// memory; ProfileStore encrypts them (DPAPI, current-user scope) on disk.
/// </summary>
public sealed class Profile
{
    public string SourceHost { get; set; } = "imap.mail.ru";
    public int SourcePort { get; set; } = 993;
    public string SourceUser { get; set; } = "";
    public string SourcePassword { get; set; } = "";
    public bool SourceSsl { get; set; } = true;

    public string DestHost { get; set; } = "";
    public int DestPort { get; set; } = 993;
    public string DestUser { get; set; } = "";
    public string DestPassword { get; set; } = "";
    public bool DestSsl { get; set; } = true;

    public bool MigrateInbox { get; set; } = true;
    public bool MigrateSent { get; set; } = true;

    /// <summary>Messages larger than this (bytes) are skipped. Exchange IMAP APPEND
    /// has a hard ~18 MiB limit, so the default mirrors it.</summary>
    public long MaxMessageBytes { get; set; } = 18874368;

    /// <summary>Delay between messages, to avoid triggering server-side rate limiting.</summary>
    public int RateDelayMs { get; set; } = 120;

    /// <summary>For INBOX only: skip source messages whose date is at/after the oldest
    /// message already on the destination. Handles resume after a forward/copy that
    /// regenerated Message-IDs.</summary>
    public bool DateBoundaryForInbox { get; set; } = true;

    /// <summary>When true, only messages whose date falls inside [DateFrom, DateTo]
    /// (inclusive, date-only) are migrated. Applies to every folder.</summary>
    public bool DateFilterEnabled { get; set; } = false;

    public DateTime? DateFrom { get; set; }

    public DateTime? DateTo { get; set; }
}
