using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SGL.Core;

namespace SGL.Desktop;

public partial class BugReportWindow : Window
{
    readonly string diagnostics;
    bool ready;

    public BugReportWindow(string diagnostics)
    {
        this.diagnostics = diagnostics;
        InitializeComponent();
        ready = true;
        UpdatePreview();
        Loaded += (_, _) => ReportTitle.Focus();
    }

    void ReportChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    void DiagnosticsChanged(object sender, RoutedEventArgs e) => UpdatePreview();
    void UpdatePreview()
    {
        if (!ready) return;
        ReportPreview.Text = BugReports.Compose(ReportTitle.Text, ReportSteps.Text, ReportExpected.Text, ReportActual.Text,
            IncludeDiagnostics.IsChecked == true ? diagnostics : null);
        OpenIssueButton.IsEnabled = !string.IsNullOrWhiteSpace(ReportTitle.Text) && !string.IsNullOrWhiteSpace(ReportActual.Text);
    }

    void Attempt(Action action)
    {
        try { action(); }
        catch (Exception error) { ReportStatus.Text = "Could not complete this action: " + error.Message; }
    }
    void CopyReport(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        Clipboard.SetText(ReportPreview.Text);
        ReportStatus.Text = "Report copied. You can paste it into an issue or send it to the SGL maintainers.";
    });
    void SaveReport(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        var dialog = new SaveFileDialog { Filter = "Markdown report|*.md", FileName = "SGL-Bug-Report.md" };
        if (dialog.ShowDialog(this) != true) return;
        Json.Atomic(dialog.FileName, ReportPreview.Text);
        ReportStatus.Text = "Report saved.";
    });
    void OpenIssue(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        var url = BugReports.IssueDraft(ReportTitle.Text, ReportPreview.Text, out bool pasteRequired);
        Clipboard.SetText(ReportPreview.Text);
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        ReportStatus.Text = pasteRequired
            ? "Report copied. Paste it into the GitHub issue body, review it and click Submit new issue."
            : "GitHub draft opened and report copied. Review the draft and click Submit new issue.";
    });
}
