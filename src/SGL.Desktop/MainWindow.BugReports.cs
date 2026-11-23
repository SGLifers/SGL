using System.Windows;
using SGL.Core;

namespace SGL.Desktop;

public partial class MainWindow
{
    Exception? lastReportError;
    void ReportBug(object sender, RoutedEventArgs e) => Run(() =>
    {
        string diagnostics = BugReports.Diagnostics(typeof(MainWindow).Assembly.GetName().Version!,
            snapshot?.Modes.Count, snapshot?.Maps.Count, dirty, invalid.Count, lastReportError);
        new BugReportWindow(diagnostics) { Owner = this }.ShowDialog();
    });
}
