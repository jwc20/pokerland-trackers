using System.Security.Cryptography;

namespace Pokerland.Tracker;

/// <summary>Identifies a hand-history file by its first line (protocol/PROTOCOL.md, "Fingerprint").</summary>
public static class Fingerprint
{
    /// <summary>UUID v5 namespace for stream ids; must match the server and the Go tracker.</summary>
    public static readonly Guid StreamNamespace = Guid.Parse("6f1e7c1e-5a0b-4d3e-9b1a-2f3c4d5e6f70");

    private const int Window = 4096;

    /// <summary>sha256 hex of the first line including its newline, or null without a complete first line.</summary>
    public static string? Compute(string path)
    {
        using var stream = OpenShared(path);
        var head = new byte[Window];
        var read = 0;
        while (read < Window)
        {
            var n = stream.Read(head, read, Window - read);
            if (n == 0) break;
            read += n;
        }
        var end = Array.IndexOf(head, (byte)'\n', 0, read);
        if (end < 0) return null;
        return Convert.ToHexStringLower(SHA256.HashData(head.AsSpan(0, end + 1)));
    }

    /// <summary>The deterministic stream id for a fingerprint (RFC 4122 UUID v5).</summary>
    public static string StreamId(string fingerprint)
    {
        var ns = StreamNamespace.ToByteArray(bigEndian: true); // RFC byte order, not Guid's mixed-endian layout
        var name = System.Text.Encoding.UTF8.GetBytes(fingerprint);
        var input = new byte[ns.Length + name.Length];
        ns.CopyTo(input, 0); name.CopyTo(input, ns.Length);
        var hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    /// <summary>Read-only, sharing write and delete with the game, which may rename or rotate the file.</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
}
