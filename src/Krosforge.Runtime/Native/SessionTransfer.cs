using System.Security.Cryptography;
namespace Krosforge.Runtime.Native;

// Owns short network transitions; no socket or database I/O runs under the lock.
internal sealed class SessionTransfer(TimeProvider clock)
{
    private readonly object gate = new();
    private long generation;
    private string phase = "idle";
    private string? credential;
    private DateTimeOffset expires;
    private bool disposed;
    internal long Open()
    {
        lock (gate)
        {
            Expire();
            if (disposed || phase != "idle") return 0;
            phase = "connection";
            return ++generation;
        }
    }
    internal string? Handoff(long lease)
    {
        lock (gate)
        {
            Expire();
            if (disposed || generation != lease || phase != "connection") return null;
            phase = "handoff"; return Issue();
        }
    }
    internal long Attach(string ticket, bool returning)
    {
        lock (gate)
        {
            Expire();
            if (disposed || phase != (returning ? "return" : "handoff") || credential is null || !Equal(ticket, credential)) return 0;
            credential = null; phase = returning ? "connection" : "world";
            return generation;
        }
    }
    internal (long Lease, string Ticket)? Return(long lease)
    {
        lock (gate)
        {
            if (disposed || generation != lease || phase != "world") return null;
            phase = "return"; generation++;
            return (generation, Issue());
        }
    }
    internal bool Current(long lease, string expected)
    {
        lock (gate) { Expire(); return !disposed && generation == lease && phase == expected; }
    }
    internal bool Release(long lease)
    {
        lock (gate) { if (generation != lease) return false; phase = "idle"; credential = null; return true; }
    }
    internal void Dispose() { lock (gate) { disposed = true; credential = null; phase = "idle"; } }
    private string Issue()
    {
        expires = clock.GetUtcNow().AddSeconds(30);
        return credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }
    private void Expire()
    {
        if (phase is "handoff" or "return" && expires <= clock.GetUtcNow()) { phase = "idle"; credential = null; }
    }
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a)), SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(b)));
}
