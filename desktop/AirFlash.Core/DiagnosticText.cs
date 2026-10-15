using System.Text.RegularExpressions;
namespace AirFlash.Core;

public static class DiagnosticText
{
    public static string ForExport(string text)
    {
        const string labels = @"(?:host|address|ip_?address|(?:device|receiver|endpoint|adapter|session)_?(?:instance_?)?(?:id|name)|instance_?id|capture_?id|endpoint_?label|(?:friendly|original|assigned)_?name|name|old_?id|new_?id|sid|task_?path)";
        text = Regex.Replace(Protect(text), "(?i)(\"" + labels + @"""\s*:\s*)(""(?:\\.|[^""\\])*""|[0-9]+)", "$1\"[device]\"");
        text = Regex.Replace(text, @"(?i)(""default_render_ids""\s*:\s*\[)([^\]]*)(\])",
            match => match.Groups[1].Value + Regex.Replace(match.Groups[2].Value, @"""(?:\\.|[^""\\])*""", "\"[device]\"") + match.Groups[3].Value);
        // Free-form engine text can embed arbitrary room, endpoint and device names.
        text = Regex.Replace(text, @"(?i)(""(?:message|detail)""\s*:\s*)""(?:\\.|[^""\\])*""", "$1\"[detail omitted]\"");
        // Four-part driver versions resemble IPv4 addresses but are needed for qualification.
        text = Regex.Replace(text, @"(?i)(?<version>""driver_?version""\s*:\s*""\d+(?:\.\d+){1,3}"")|\b(?:\d{1,3}\.){3}\d{1,3}\b",
            match => match.Groups["version"].Success ? match.Value : "[address]");
        return Regex.Replace(text, @"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", "[device]");
    }
    public static string Protect(string text)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length == 0) continue;
            text = text.Replace(path.Replace("\\", "\\\\"), "[user-path]", StringComparison.OrdinalIgnoreCase)
                .Replace(path, "[user-path]", StringComparison.OrdinalIgnoreCase);
        }
        const string labels = @"(?:pin|password|(?:client[_-]?)?secret|(?:(?:access|refresh|auth)[_-]?)?token|credentials|(?:(?:private|public|session|api)[_-]?)?key|pairing[_-]?pin)";
        text = Regex.Replace(text, "(?i)(\"" + labels + @"""\s*:\s*)(""(?:\\.|[^""\\])*""|[0-9]+)", "$1\"[redacted]\"");
        return Regex.Replace(text, @"(?i)\b(" + labels + @")\s*[:=]\s*(""[^""\r\n]*""|[^\s,;]+)", "$1=[redacted]");
    }
}
