using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailMigrator.Models;

namespace MailMigrator.Services;

/// <summary>
/// Saves/loads a Profile to %LOCALAPPDATA%\MailMigrator\profile.json.
/// Passwords are encrypted with DPAPI (DataProtectionScope.CurrentUser), so the
/// file is only readable by the same Windows user on the same machine.
/// </summary>
public static class ProfileStore
{
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailMigrator");

    private static string FilePath => Path.Combine(Dir, "profile.json");

    public static void Save(Profile p)
    {
        var dto = new ProfileFile
        {
            SourceHost = p.SourceHost, SourcePort = p.SourcePort, SourceUser = p.SourceUser,
            SourcePassword = Encrypt(p.SourcePassword), SourceSsl = p.SourceSsl,
            DestHost = p.DestHost, DestPort = p.DestPort, DestUser = p.DestUser,
            DestPassword = Encrypt(p.DestPassword), DestSsl = p.DestSsl,
            MigrateInbox = p.MigrateInbox, MigrateSent = p.MigrateSent,
            MaxMessageBytes = p.MaxMessageBytes, RateDelayMs = p.RateDelayMs,
            DateBoundaryForInbox = p.DateBoundaryForInbox,
            DateFilterEnabled = p.DateFilterEnabled, DateFrom = p.DateFrom, DateTo = p.DateTo,
        };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Profile? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(FilePath));
            if (dto is null) return null;
            return new Profile
            {
                SourceHost = dto.SourceHost, SourcePort = dto.SourcePort, SourceUser = dto.SourceUser,
                SourcePassword = Decrypt(dto.SourcePassword), SourceSsl = dto.SourceSsl,
                DestHost = dto.DestHost, DestPort = dto.DestPort, DestUser = dto.DestUser,
                DestPassword = Decrypt(dto.DestPassword), DestSsl = dto.DestSsl,
                MigrateInbox = dto.MigrateInbox, MigrateSent = dto.MigrateSent,
                MaxMessageBytes = dto.MaxMessageBytes, RateDelayMs = dto.RateDelayMs,
                DateBoundaryForInbox = dto.DateBoundaryForInbox,
                DateFilterEnabled = dto.DateFilterEnabled, DateFrom = dto.DateFrom, DateTo = dto.DateTo,
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed class ProfileFile
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
        public long MaxMessageBytes { get; set; } = 18874368;
        public int RateDelayMs { get; set; } = 120;
        public bool DateBoundaryForInbox { get; set; } = true;
        public bool DateFilterEnabled { get; set; } = false;
        public DateTime? DateFrom { get; set; }
        public DateTime? DateTo { get; set; }
    }

    private static string Encrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Decrypt(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
