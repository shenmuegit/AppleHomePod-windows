using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
namespace AirFlash.Core;

public sealed record NativeTaskReceipt(int SchemaVersion, string Sid, string Executable, string Arguments, string XmlSha256, string? ExecutableSha256 = null);
public static class NativeStartupPolicy
{
    public const string Arguments = "--native-agent";
    public static bool IsHeadless(string[] args) => args.Any(a => a.StartsWith("--native-", StringComparison.Ordinal))
        || args.Contains("--startup") && !args.Contains("--legacy-panel");
    public static bool OwnsLegacyRun(string? value, string knownExecutable) => value == $"\"{knownExecutable}\" --startup";
    public static bool RebootedAfter(DateTimeOffset preinstall, DateTimeOffset boot) => boot > preinstall;
    public static string TaskXml(string sid, string executable)
    {
        if (!sid.StartsWith("S-1-", StringComparison.Ordinal) || sid.Any(c => c != 'S' && c != '-' && !char.IsAsciiDigit(c))) throw new ArgumentException("Current user SID required.", nameof(sid));
        executable = Path.GetFullPath(executable);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? value) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new[] { E("Author", sid), E("Description", "AppleHome native output") }),
            E("Triggers", E("LogonTrigger", new[] { E("Enabled", true), E("UserId", sid) })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false), E("AllowStartOnDemand", true),
                E("Enabled", true), E("ExecutionTimeLimit", "PT0S"), E("RestartOnFailure", new[] { E("Interval", "PT1M"), E("Count", 3) }) }),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), E("Exec", new[] { E("Command", executable), E("Arguments", Arguments) }))).ToString(SaveOptions.DisableFormatting);
    }
    private static string XmlHash(string xml) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml.Trim()))).ToLowerInvariant();
    public static NativeTaskReceipt Receipt(string registeredXml, string sid, string executable, string? executableHash = null) => new(1, sid, Path.GetFullPath(executable), Arguments, XmlHash(registeredXml), executableHash);
    public static bool OwnsInstalledBinary(NativeTaskReceipt? receipt, string sid, string executable, string hash)
        => receipt is { SchemaVersion: 1 } && receipt.Sid == sid && receipt.ExecutableSha256 == hash
        && string.Equals(receipt.Executable, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
    public static bool OwnsTask(string xml, NativeTaskReceipt? receipt, string sid, string executable)
    {
        if (receipt is null || receipt.SchemaVersion != 1 || xml.Length > 65536 || receipt.Sid != sid || receipt.Arguments != Arguments
            || !string.Equals(receipt.Executable, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) || receipt.XmlSha256 != XmlHash(xml)) return false;
        try
        {
            var root = XDocument.Parse(xml); XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var principals = root.Descendants(ns + "Principal").ToArray(); var actions = root.Descendants(ns + "Actions").Single().Elements().ToArray();
            return principals.Length == 1 && principals[0].Element(ns + "UserId")?.Value == sid && actions.Length == 1 && actions[0].Name == ns + "Exec"
                && string.Equals(actions[0].Element(ns + "Command")?.Value, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                && actions[0].Element(ns + "Arguments")?.Value == Arguments;
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException) { return false; }
    }
    public static async Task EnsureRunningAsync(Func<CancellationToken, Task<bool>> ready, Func<Task> start, CancellationToken cancellation)
    {
        if (await ready(cancellation).ConfigureAwait(false)) return;
        await start().ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (!await ready(timeout.Token).ConfigureAwait(false)) await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("Native output did not report capture_ready within fifteen seconds."); }
    }
    public static async Task<int> RunHeadlessAsync(Func<CancellationToken, Task> run, Func<Exception, Task> report, CancellationToken cancellation)
    {
        try { await run(cancellation).ConfigureAwait(false); return 0; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            try { await report(error).ConfigureAwait(false); }
            catch (Exception) { /* Failure reporting cannot turn a failed agent into success. */ }
            return 1;
        }
    }
}
