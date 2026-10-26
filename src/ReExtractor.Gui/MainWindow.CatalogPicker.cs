using System;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ReExtractor.Gui;

public partial class MainWindow
{
    private async void OnOpenQuickCatalogClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开本机动作预览目录",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("动作目录 JSON") { Patterns = ["*.json"] }]
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        await InitializeQuickPreviewAsync(path);
    }
}
