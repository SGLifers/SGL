using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SGL.Core;

public static class BugReports
{
    public const string IssuesUrl = "https://github.com/SGLifers/SGL/issues/new";

    public static string Compose(string title, string steps, string expected, string actual, string? diagnostics)
    {
        static string Text(string value) => string.IsNullOrWhiteSpace(value) ? "Not provided." : value.Trim();
        var report = new StringBuilder();
        report.AppendLine("## Summary").AppendLine(Text(title)).AppendLine();
        report.AppendLine("## Steps to reproduce").AppendLine(Text(steps)).AppendLine();
        report.AppendLine("## Expected behavior").AppendLine(Text(expected)).AppendLine();
        report.AppendLine("## Actual behavior").AppendLine(Text(actual));
        if (!string.IsNullOrWhiteSpace(diagnostics))
            report.AppendLine().AppendLine("## Diagnostics").AppendLine("```text").AppendLine(diagnostics.Trim()).AppendLine("```");
        return report.ToString();
    }

    // Collect only explicit, non-content fields. Never include exception messages,
    // raw stack traces, file paths, save contents, service URLs or account state.
    public static string Diagnostics(Version version, int? modes, int? maps, bool dirty, int invalidFields, Exception? error)
    {
        var text = new StringBuilder();
        text.AppendLine($"SGL: {version.ToString(3)}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        text.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        text.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"Installed modes: {modes?.ToString() ?? "Not scanned"}");
        text.AppendLine($"Installed maps: {maps?.ToString() ?? "Not scanned"}");
        text.AppendLine($"Unsaved editor changes: {dirty}");
        text.AppendLine($"Invalid fields: {invalidFields}");
        if (error != null)
        {
            text.AppendLine("Last error types:");
            for (int i = 0; error != null && i < 5; i++, error = error.InnerException)
            {
                text.AppendLine("- " + error.GetType().FullName);
                foreach (var method in new StackTrace(error, false).GetFrames().Select(f => f.GetMethod())
                    .Where(m => m?.DeclaringType?.Namespace?.StartsWith("SGL.", StringComparison.Ordinal) == true).Take(12))
                    text.AppendLine($"  at {method!.DeclaringType!.FullName}.{method.Name}");
            }
        }
        return text.ToString().TrimEnd();
    }

    public static Uri IssueDraft(string title, string report, out bool pasteRequired)
    {
        string prefix = IssuesUrl + "?title=" + Uri.EscapeDataString(title.ReplaceLineEndings(" ").Trim()) + "&body=";
        string url = prefix + Uri.EscapeDataString(report);
        pasteRequired = url.Length > 7000;
        return new Uri(pasteRequired ? prefix + Uri.EscapeDataString("Paste the report copied from SGL here.") : url);
    }
}
