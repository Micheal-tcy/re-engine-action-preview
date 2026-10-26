using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace ReExtractor.Gui;

public partial class MainWindow
{
    private sealed class QuickCategoryEntry
    {
        public string GameId { get; set; } = "";
        public string Path { get; set; } = "";
        public int MotionNumber { get; set; }
        public string Category { get; set; } = "unknown";
        public string Reason { get; set; } = "";
        public string? SubcategoryId { get; set; }
        public string? SubcategoryName { get; set; }
    }
    private sealed class QuickCategoryFile
    {
        public QuickCategoryEntry[] Entries { get; set; } = [];
    }
    private sealed record QuickCategoryOption(string Id, int? Count = null)
    {
        public override string ToString() => QuickCategoryName(Id) + (Count is { } n ? $" · {n}" : "");
    }
    private sealed record QuickSubcategoryOption(string Id, string Name, int Count)
    {
        public override string ToString() => Name + " · " + Count;
    }
    private static readonly string[] QuickCategoryIds = ["all", "attack", "idle", "locomotion", "reaction", "interaction", "other", "unknown"];
    private static string QuickCategoryName(string id) => id switch
    {
        "all" => "全部动作", "attack" => "攻击", "idle" => "Idle · 待机",
        "locomotion" => "Locomotion · 移动", "reaction" => "受击反应", "interaction" => "交互", "other" => "其他动作", _ => "待确认"
    };
    private readonly Dictionary<string, QuickCategoryEntry> _quickCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuickCategoryEntry> _quickManualCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuickCategoryEntry> _quickInferredManualCategories = new(StringComparer.OrdinalIgnoreCase);
    private string? _quickManualCategoryPath;
    private bool _quickCategorySync;
    private bool _quickAssignSync;
    private bool _quickSubcategorySync;
    private static string QuickCategoryKey(string gameId, string path, int id)
        => gameId.ToLowerInvariant() + "|" + path.Replace('\\', '/').ToLowerInvariant() + "|" + id;

    private async Task InitializeQuickCategoriesAsync(string catalogPath, string[] files)
    {
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(catalogPath))!;
        _quickManualCategoryPath = System.IO.Path.Combine(folder, "动作分类备注.json");
        _quickCategories.Clear();
        _quickManualCategories.Clear();
        _quickInferredManualCategories.Clear();
        foreach (var file in files)
            await ReadQuickCategoriesAsync(System.IO.Path.GetFullPath(file, folder), _quickCategories);
        await ReadQuickCategoriesAsync(_quickManualCategoryPath, _quickManualCategories);
        MigrateUnambiguousLegacyManualCategories();
        if (_quickCatalog != null)
            foreach (var path in _quickCategories.Values.Concat(_quickManualCategories.Values).Where(e => e.GameId.Length == 0)
                .Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => LegacyQuickCategoryOwner(p) == null))
                AppendLog("旧分类路径无法唯一归属游戏，已保留待确认（未改写原记录）：" + path);
        _quickAssignSync = true;
        QuickAssignCategory.ItemsSource = QuickCategoryIds.Where(id => id != "all").Select(id => new QuickCategoryOption(id)).ToArray();
        QuickAssignCategory.SelectedIndex = -1;
        QuickAssignCategory.IsEnabled = false;
        _quickAssignSync = false;
        RefreshQuickCategories();
    }

    private async Task ReadQuickCategoriesAsync(string path, Dictionary<string, QuickCategoryEntry> target)
    {
        if (!File.Exists(path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<QuickCategoryFile>(await File.ReadAllTextAsync(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var entry in data?.Entries ?? [])
            {
                if (entry.Path.Length == 0 || !QuickCategoryIds.Contains(entry.Category) || entry.Category == "all") continue;
                target[QuickCategoryKey(entry.GameId, entry.Path, entry.MotionNumber)] = entry;
            }
        }
        catch (Exception ex) { AppendLog("分类文件读取失败：" + System.IO.Path.GetFileName(path) + "：" + ex.Message); }
    }

    private void ApplyQuickCategory(QuickMotion row)
    {
        var gameId = row.GameId.Length > 0 ? row.GameId : (QuickGameCombo.SelectedItem as QuickGame)?.Id ?? "";
        var key = QuickCategoryKey(gameId, row.Path, row.Motion.MotionNumber);
        var legacyKey = QuickCategoryKey("", row.Path, row.Motion.MotionNumber);
        // A scoped entry wins over any unscoped fallback. Legacy manual entries
        // with one known game owner are promoted in memory before this lookup.
        QuickCategoryEntry? Find(Dictionary<string, QuickCategoryEntry> entries, string lookup)
            => entries.TryGetValue(lookup, out var found) ? found : null;
        var explicitBase = Find(_quickCategories, key);
        var explicitManual = Find(_quickManualCategories, key) ?? Find(_quickInferredManualCategories, key);
        var legacyAllowed = _quickCatalog == null || LegacyQuickCategoryOwner(row.Path) == gameId;
        var entry = explicitManual ?? explicitBase ?? (legacyAllowed ? Find(_quickManualCategories, legacyKey) ?? Find(_quickCategories, legacyKey) : null);
        if (entry != null)
        {
            row.Category = entry.Category;
            row.CategoryReason = entry.Reason;
        }
        else
        {
            row.Category = "unknown";
            row.CategoryReason = "尚无明确用途证据，不能仅凭 Loop 或数字编号判断。预览后可手动标记。";
        }
        // A main-category-only manual correction must not erase a verified subgroup.
        var basis = explicitBase ?? (legacyAllowed ? Find(_quickCategories, legacyKey) : null);
        var subgroup = entry?.SubcategoryId ?? basis?.SubcategoryId;
        if (subgroup == null && QuickWeaponCombo.SelectedItem is QuickWeapon set)
            set.SubcategoryByMotionList.TryGetValue(QuickResourcePath(row.Path), out subgroup);
        row.SubcategoryId = subgroup is "all" or "unassigned" ? "" : subgroup ?? "";
        var game = _quickCatalog?.Games.FirstOrDefault(g => g.Id == gameId);
        row.SubcategoryName = entry?.SubcategoryName ?? basis?.SubcategoryName ??
            game?.Subcategories.FirstOrDefault(s => s.Id == row.SubcategoryId)?.Name ?? row.SubcategoryId;
    }

    private void MigrateUnambiguousLegacyManualCategories()
    {
        if (_quickCatalog == null) return;
        foreach (var old in _quickManualCategories.Values.Where(e => e.GameId.Length == 0).ToArray())
        {
            var gameId = LegacyQuickCategoryOwner(old.Path);
            if (gameId == null) continue;
            var key = QuickCategoryKey(gameId, old.Path, old.MotionNumber);
            _quickInferredManualCategories.TryAdd(key, new QuickCategoryEntry
            {
                GameId = gameId, Path = old.Path, MotionNumber = old.MotionNumber,
                Category = old.Category, Reason = old.Reason,
                SubcategoryId = old.SubcategoryId, SubcategoryName = old.SubcategoryName
            });
        }
    }

    private string? LegacyQuickCategoryOwner(string path)
    {
        var owners = _quickCatalog?.Games.Where(g => g.Subjects.Any(s => s.ActionSets.Any(a =>
            a.MotionLists.Contains(QuickResourcePath(path), StringComparer.OrdinalIgnoreCase)))).Select(g => g.Id).ToArray();
        return owners?.Length == 1 ? owners[0] : null;
    }

    private void ResetQuickCategoryFilters()
    {
        // A new subject or action set may not share the previous behavior tags.
        _quickCategorySync = true;
        _quickSubcategorySync = true;
        QuickCategoryCombo.SelectedIndex = -1;
        QuickSubcategoryCombo.SelectedIndex = -1;
        _quickCategorySync = false;
        _quickSubcategorySync = false;
    }

    private void RefreshQuickCategories()
    {
        var selected = (QuickCategoryCombo.SelectedItem as QuickCategoryOption)?.Id ?? "all";
        var choices = QuickCategoryIds.Select(id => new QuickCategoryOption(id,
            id == "all" ? _quickRows.Count : _quickRows.Count(row => row.Category == id))).ToArray();
        _quickCategorySync = true;
        QuickCategoryCombo.ItemsSource = choices;
        QuickCategoryCombo.SelectedItem = choices.First(c => c.Id == selected);
        _quickCategorySync = false;
        RefreshQuickSubcategories();
    }

    private void RefreshQuickSubcategories()
    {
        var selected = (QuickSubcategoryCombo.SelectedItem as QuickSubcategoryOption)?.Id ?? "all";
        var category = (QuickCategoryCombo.SelectedItem as QuickCategoryOption)?.Id ?? "all";
        var rows = _quickRows.Where(r => category == "all" || r.Category == category).ToArray();
        var options = new List<QuickSubcategoryOption> { new("all", "全部子类", rows.Length) };
        options.AddRange(rows.Where(r => r.SubcategoryId.Length > 0).GroupBy(r => r.SubcategoryId)
            .OrderBy(g => g.First().SubcategoryName).Select(g => new QuickSubcategoryOption(g.Key, g.First().SubcategoryName, g.Count())));
        options.Add(new("unassigned", "未细分（未配置）", rows.Count(r => r.SubcategoryId.Length == 0)));
        _quickSubcategorySync = true;
        QuickSubcategoryCombo.ItemsSource = options;
        QuickSubcategoryCombo.SelectedItem = options.FirstOrDefault(c => c.Id == selected) ?? options[0];
        _quickSubcategorySync = false;
    }

    private void OnQuickSubcategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_quickSubcategorySync && !_quickSync) FilterQuickRows();
    }

    private void OnQuickCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_quickCategorySync && !_quickSync) FilterQuickRows();
    }

    private void SyncQuickAssignment(QuickMotion? row)
    {
        _quickAssignSync = true;
        QuickAssignCategory.SelectedItem = row == null ? null : QuickAssignCategory.Items.Cast<QuickCategoryOption>().FirstOrDefault(c => c.Id == row.Category);
        QuickAssignCategory.IsEnabled = !_quickBusy && row != null;
        _quickAssignSync = false;
    }

    private async void OnQuickAssignCategory(object? sender, SelectionChangedEventArgs e)
    {
        if (_quickAssignSync || _quickBusy || QuickMotionList.SelectedItem is not QuickMotion row ||
            QuickAssignCategory.SelectedItem is not QuickCategoryOption option || _quickManualCategoryPath == null || option.Id == row.Category) return;
        var gameId = row.GameId.Length > 0 ? row.GameId : (QuickGameCombo.SelectedItem as QuickGame)?.Id ?? "";
        var key = QuickCategoryKey(gameId, row.Path, row.Motion.MotionNumber);
        _quickManualCategories.TryGetValue(key, out var previous);
        SetQuickBusy(true);
        try
        {
            _quickManualCategories[key] = new QuickCategoryEntry
            {
                GameId = gameId, Path = row.Path, MotionNumber = row.Motion.MotionNumber, Category = option.Id, Reason = "用户在预览工具中手动标记。"
            };
            var text = JsonSerializer.Serialize(new QuickCategoryFile { Entries = _quickManualCategories.Values.ToArray() },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var temp = _quickManualCategoryPath + ".tmp";
            await File.WriteAllTextAsync(temp, text);
            File.Move(temp, _quickManualCategoryPath, true);
            foreach (var item in _quickRows.Where(r => QuickCategoryKey(r.GameId.Length > 0 ? r.GameId : gameId, r.Path, r.Motion.MotionNumber) == key)) ApplyQuickCategory(item);
            RefreshQuickCategories();
            FilterQuickRows();
            QuickStatus.Text = "已保存分类：" + row.Display + " → " + QuickCategoryName(option.Id);
        }
        catch (Exception ex)
        {
            if (previous == null) _quickManualCategories.Remove(key); else _quickManualCategories[key] = previous;
            SyncQuickAssignment(row);
            QuickStatus.Text = "分类保存失败：" + ex.Message;
        }
        finally { SetQuickBusy(false); }
    }
}
