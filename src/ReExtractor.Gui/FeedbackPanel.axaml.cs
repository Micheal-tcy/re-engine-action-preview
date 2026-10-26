using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReExtractor.Gui;

public partial class FeedbackPanel : UserControl
{
    public const string IssuesUrl = "https://github.com/Micheal-tcy/re-engine-action-preview/issues/new";
    public event Action? CloseRequested;

    // Compatibility with MainWindow; this property is never read by feedback.
    public Func<string>? CurrentLogProvider { get; set; }

    public FeedbackPanel() => InitializeComponent();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnOpenIssuesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(IssuesUrl) { UseShellExecute = true });
            FeedbackStatus.Text = "已在浏览器中打开 GitHub Issues，请在那里填写并提交。";
        }
        catch (Exception)
        {
            FeedbackStatus.Text = "无法打开浏览器，请复制下方地址后手动打开。";
        }
    }
}
