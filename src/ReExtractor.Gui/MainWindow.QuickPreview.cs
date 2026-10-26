using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReExtractor.Core;

namespace ReExtractor.Gui;

public partial class MainWindow
{
    private sealed class QuickCatalog
    {
        public QuickGame[] Games { get; set; } = [];
        public string[] CategoryFiles { get; set; } = [];
    }
    private sealed class QuickGame
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Root { get; set; } = "";
        public string[] MeshPaths { get; set; } = [];
        public string ModelNote { get; set; } = "";
        public Dictionary<string, string> Materials { get; set; } = new();
        public QuickWeapon[] Weapons { get; set; } = [];
        public QuickSubject[] Subjects { get; set; } = [];
        public QuickSubcategoryDefinition[] Subcategories { get; set; } = [];
        public override string ToString() => Name;
    }
    private sealed class QuickWeapon
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
        public string[] MotionLists { get; set; } = [];
        public QuickAttachment[] Attachments { get; set; } = [];
        public int? DefaultMotionNumber { get; set; }
        public string Kind { get; set; } = "action-set";
        public Dictionary<string, string> AttachmentSources { get; set; } = new();
        public Dictionary<string, string> SubcategoryByMotionList { get; set; } = new();
        public Dictionary<string, int[]> ExcludedSources { get; set; } = new();
        public override string ToString() => Name;
    }
    private sealed class QuickAttachment
    {
        public string Name { get; set; } = "武器";
        public string MeshPath { get; set; } = "";
        public string MaterialPath { get; set; } = "";
        public string Bone { get; set; } = "";
        public bool FollowBodySkeleton { get; set; }
        public int[]? VisibleGroups { get; set; }
        public float[] LocalMatrix { get; set; } = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
        public string? PoseList { get; set; }
        public int PoseId { get; set; }
        public float PoseTime { get; set; }
        public string Note { get; set; } = "";
    }
    private sealed record QuickMotion(string Path, IReadOnlyList<MotionInfo> Motions, int Index)
    {
        public MotionInfo Motion => Motions[Index];
        public string Display => Motion.DisplayName;
        public string Group => System.IO.Path.GetFileName(Path);
        public string SearchText => Display + " " + Path;
        public string GameId { get; set; } = "";
        public string Category { get; set; } = "unknown";
        public string CategoryReason { get; set; } = "尚无明确用途证据，不能仅凭 Loop 或数字编号判断。";
        public string SubcategoryId { get; set; } = "";
        public string SubcategoryName { get; set; } = "";
        public string Detail => QuickCategoryName(Category) + (SubcategoryId.Length > 0 ? " / " + SubcategoryName : "") + " · " + Group;
    }

    private QuickCatalog? _quickCatalog;
    private readonly Dictionary<string, IReadOnlyList<MotionInfo>> _quickMotionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (PakService Source, List<EntryRow> Entries, FileTreeNode? Tree)> _quickSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private List<QuickMotion> _quickRows = new();
    private bool _quickSync;
    private bool _quickBusy;
    private int _quickGeneration;
    private bool _quickBeforeFullscreen;
    private string? _quickReport;
    private string? _quickModelGame;
    private readonly Dictionary<string, (ViewportMesh Mesh, AnimationClip? Pose)> _quickWeaponCache = new(StringComparer.OrdinalIgnoreCase);
    private string _quickWeaponStatus = "";
    private string? _quickLoadedWeapon;

    private void SetQuickLayout(bool enabled)
    {
        StopViewportNavigation();
        LogPanel.IsVisible = LogSplitter.IsVisible = !enabled;
        QuickPanel.IsVisible = enabled;
        UpdateQuickSubjectDescription();
        LeftPanel.IsVisible = !enabled;
        RightPanel.IsVisible = !enabled;
        RightSplitter.IsVisible = !enabled;
        MainLayout.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(enabled ? 410 : 330);
        MainLayout.ColumnDefinitions[3].Width = new Avalonia.Controls.GridLength(enabled ? 0 : 5);
        MainLayout.ColumnDefinitions[4].Width = new Avalonia.Controls.GridLength(enabled ? 0 : 340);
        MainLayout.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(enabled ? 0 : 180);
    }

    private void SetQuickBusy(bool busy, string? message = null)
    {
        _quickBusy = busy;
        MainMenu.IsEnabled = !busy;
        QuickGameCombo.IsEnabled = !busy;
        QuickSubjectCombo.IsEnabled = !busy;
        QuickWeaponCombo.IsEnabled = !busy;
        QuickCategoryCombo.IsEnabled = !busy;
        QuickSubcategoryCombo.IsEnabled = !busy;
        QuickAssignCategory.IsEnabled = !busy && QuickMotionList.SelectedItem != null;
        QuickMotionList.IsEnabled = !busy;
        QuickReplay.IsEnabled = !busy;
        QuickTextures.IsEnabled = !busy;
        if (message != null) QuickStatus.Text = message;
    }

    private async Task InitializeQuickPreviewAsync(string catalogPath)
    {
        try
        {
            _quickCatalog = JsonSerializer.Deserialize<QuickCatalog>(await File.ReadAllTextAsync(catalogPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("游戏列表为空");
            if (_quickCatalog.Games.Length == 0) throw new InvalidDataException("没有配置游戏");
            NormalizeQuickCatalog(_quickCatalog);
            await InitializeQuickCategoriesAsync(catalogPath, _quickCatalog.CategoryFiles);
            SetQuickLayout(true);
            Title = "ReExtractor · 游戏角色与动作预览（中文说明）";
            VersionTitleText.Text = "◆  选择游戏 → 角色/怪物 → 动作集 → 行为分类";
            QuickGameCombo.ItemsSource = _quickCatalog.Games;
            var args = Environment.GetCommandLineArgs();
            string? Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            _quickReport = Arg("--quick-report");
            var game = _quickCatalog.Games.FirstOrDefault(g => g.Id == Arg("--quick-game")) ?? _quickCatalog.Games[0];
            QuickGameCombo.SelectedItem = game;
            if (Arg("--quick-subject") is { } subjectId)
                QuickSubjectCombo.SelectedItem = game.Subjects.Single(s => s.Id == subjectId);
            if ((Arg("--quick-action-set") ?? Arg("--quick-weapon")) is { } weaponId)
            {
                var subject = SelectedQuickSubject ?? throw new InvalidDataException("未选择角色/对象");
                var weapon = subject.ActionSets.Single(w => w.Id == weaponId);
                _quickSync = true;
                QuickWeaponCombo.SelectedItem = weapon;
                _quickSync = false;
                await PrepareQuickWeaponAsync();
                if (Arg("--quick-motion") is { } query)
                {
                    var row = _quickRows.FirstOrDefault(r => r.Display.Contains(query, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("未找到检查动作：" + query);
                    _quickSync = true;
                    QuickMotionList.SelectedItem = row;
                    _quickSync = false;
                    await PlayQuickMotionAsync(row);
                    if (args.Contains("--quick-load-textures")) await LoadQuickTexturesAsync();
                    await Task.Delay(1200);
                    await WriteQuickReportAsync(Viewport.HasAnimation, Viewport.HasAnimation ? null : QuickStatus.Text);
                }
            }
        }
        catch (Exception ex)
        {
            QuickStatus.Text = "动作入口加载失败：" + ex.Message;
            ActionStatus.Text = QuickStatus.Text;
            await WriteQuickReportAsync(false, ex.ToString());
        }
    }

    private void OnQuickGameChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_quickSync || QuickGameCombo.SelectedItem is not QuickGame game) return;
        ++_quickGeneration;
        _quickSync = true;
        QuickSubjectCombo.ItemsSource = game.Subjects;
        QuickSubjectCombo.SelectedIndex = game.Subjects.Length > 0 ? 0 : -1;
        QuickWeaponCombo.ItemsSource = SelectedQuickSubject?.ActionSets;
        QuickWeaponCombo.SelectedIndex = -1;
        QuickMotionList.ItemsSource = null;
        QuickSearch.Text = "";
        _quickRows.Clear();
        ResetQuickCategoryFilters();
        RefreshQuickCategories();
        SyncQuickAssignment(null);
        _quickSync = false;
        InvalidateTextureLoad();
        ++_previewSeq;
        ClearMotionState();
        Viewport.ClearWeaponAttachments();
        _quickLoadedWeapon = null;
        _quickModelGame = null;
        _previewMeshPaths.Clear();
        Viewport.IsVisible = false;
        ViewportInput.IsVisible = false;
        UpdateQuickSubjectDescription();
        QuickModelNote.Text = "选择动作集后加载配套模型与动作；附件为可选。";
        ToolTip.SetTip(QuickModelNote, SelectedQuickSubject?.ModelNote);
        QuickStatus.Text = $"已选择{game.Name}，请选择角色/怪物和动作集。";
        QuickCount.Text = $"{SelectedQuickSubject?.ActionSets.Length ?? 0} 个动作集";
    }

    private async void OnQuickWeaponChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_quickSync && !_quickBusy) await PrepareQuickWeaponAsync();
    }

    private async Task PrepareQuickWeaponAsync()
    {
        if (QuickGameCombo.SelectedItem is not QuickGame game || SelectedQuickSubject is not { } subject || QuickWeaponCombo.SelectedItem is not QuickWeapon weapon) return;
        var generation = ++_quickGeneration;
        SetQuickBusy(true, $"正在准备{game.Name} · {weapon.Name}…");
        _quickSync = true;
        QuickMotionList.ItemsSource = null;
        QuickSearch.Text = "";
        _quickSync = false;
        _quickRows.Clear();
        ResetQuickCategoryFilters();
        RefreshQuickCategories();
        Viewport.ClearWeaponAttachments();
        ClearMotionState();
        try
        {
            if (!Directory.Exists(game.Root)) throw new DirectoryNotFoundException("资源目录不存在：" + game.Root);
            if (_pak == null || !string.Equals(_loadedFolderPath, Path.GetFullPath(game.Root), StringComparison.OrdinalIgnoreCase))
            {
                _quickModelGame = null;
                if (_quickSourceCache.TryGetValue(game.Root, out var cached))
                {
                    _pak = cached.Source;
                    _all = cached.Entries;
                    _treeRoot = cached.Tree;
                    _loadedFolderPath = Path.GetFullPath(game.Root);
                    _loadedPakPaths.Clear();
                    _materialChoices.Clear();
                    _byPath.Clear();
                    foreach (var entry in _all) _byPath[entry.Path] = entry;
                    FileTree.ItemsSource = _treeRoot?.Children;
                    ApplyFilter();
                }
                else
                {
                    await LoadExtractedFolderAsync(game.Root);
                    SetQuickBusy(true);
                    if (_pak != null && string.Equals(_loadedFolderPath, Path.GetFullPath(game.Root), StringComparison.OrdinalIgnoreCase))
                        _quickSourceCache[game.Root] = (_pak, _all, _treeRoot);
                }
                if (_pak == null || !string.Equals(_loadedFolderPath, Path.GetFullPath(game.Root), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(ActionStatus.Text);
            }
            if (subject.MeshPaths.Length == 0) throw new InvalidDataException("这个角色/对象尚未配置可用的预览模型");
            if (_quickModelGame != QuickModelKey(game, subject) || !_previewMeshPaths.SequenceEqual(subject.MeshPaths, StringComparer.OrdinalIgnoreCase))
            {
                _materialChoices.Clear();
                foreach (var pair in subject.Materials) _materialChoices[pair.Key] = pair.Value;
                await LoadModelPathsAsync(subject.MeshPaths);
                SetQuickBusy(true);
                if (!Viewport.HasMesh || !_previewMeshPaths.SequenceEqual(subject.MeshPaths, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("预览模型未完整加载：" + ActionStatus.Text);
                _quickModelGame = QuickModelKey(game, subject);
            }
            ShowViewport();
            await LoadQuickWeaponsAsync(false);
            var source = _pak;
            var failures = new List<string>();
            foreach (var path in weapon.MotionLists)
            {
                var key = game.Root + "|" + path;
                try
                {
                    if (!_quickMotionCache.TryGetValue(key, out var motions))
                    {
                        QuickStatus.Text = "正在读取动作列表：" + Path.GetFileName(path);
                        motions = await Task.Run(() => { using var stream = source!.ReadFile(path); return ViewportDataLoader.ListMotions(stream, path, source.ReadFile); });
                        if (generation != _quickGeneration || !ReferenceEquals(source, _pak))
                            throw new InvalidOperationException("资源来源已经改变，请重新选择武器");
                        _quickMotionCache[key] = motions;
                    }
                    var excluded = weapon.ExcludedSources.GetValueOrDefault(path, []);
                    _quickRows.AddRange(motions.Select((_, index) => new QuickMotion(path, motions, index) { GameId = game.Id })
                        .Where(row => !excluded.Contains(row.Motion.SourceIndex)));
                }
                catch (Exception ex) { failures.Add(Path.GetFileName(path) + "：" + ex.Message); }
            }
            if (generation != _quickGeneration || !ReferenceEquals(source, _pak))
                throw new InvalidOperationException("资源来源已经改变，请重新选择武器");
            foreach (var row in _quickRows) ApplyQuickCategory(row);
            RefreshQuickCategories();
            FilterQuickRows();
            UpdateQuickModelNote();
            QuickStatus.Text = _quickRows.Count > 0
                ? $"{game.Name} · {weapon.Name}：点击列表中的动作即可播放。"
                : "没有可读取的动作。请查看下方错误信息。";
            if (weapon.Attachments.Length > 0 && Viewport.WeaponAttachmentCount != weapon.Attachments.Length)
                QuickStatus.Text += "\n武器将在动作挂点就绪后装配。";
            if (failures.Count > 0) QuickStatus.Text += $"\n{failures.Count} 份动作包无法读取：" + string.Join("；", failures);
            await WriteQuickReportAsync(_quickRows.Count > 0, failures.Count > 0 ? string.Join("\n", failures) : null);
            if (QuickMotionList.ItemsSource is IEnumerable<QuickMotion> available && available.Any())
            {
                var first = available.FirstOrDefault(r => weapon.DefaultMotionNumber == r.Motion.MotionNumber &&
                    !r.Path.Contains("_tree", StringComparison.OrdinalIgnoreCase)) ??
                    available.FirstOrDefault(r => !r.Path.Contains("_tree", StringComparison.OrdinalIgnoreCase)) ?? available.First();
                _quickSync = true;
                QuickMotionList.SelectedItem = first;
                _quickSync = false;
                await PlayQuickMotionAsync(first);
            }
        }
        catch (Exception ex)
        {
            _quickRows.Clear();
            FilterQuickRows();
            ClearMotionState();
            QuickStatus.Text = "准备失败：" + ex.Message;
            await WriteQuickReportAsync(false, ex.ToString());
        }
        finally { SetQuickBusy(false); }
    }

    private void OnQuickSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_quickSync) FilterQuickRows();
    }
    private void FilterQuickRows()
    {
        if (QuickMotionList == null) return;
        var query = QuickSearch.Text?.Trim() ?? "";
        var category = (QuickCategoryCombo.SelectedItem as QuickCategoryOption)?.Id ?? "all";
        RefreshQuickSubcategories();
        var subcategory = (QuickSubcategoryCombo.SelectedItem as QuickSubcategoryOption)?.Id ?? "all";
        var rows = _quickRows.Where(r => (category == "all" || r.Category == category) &&
            (subcategory == "all" || (subcategory == "unassigned" ? r.SubcategoryId.Length == 0 : r.SubcategoryId == subcategory)) &&
            (query.Length == 0 || r.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _quickSync = true;
        var selected = QuickMotionList.SelectedItem;
        QuickMotionList.ItemsSource = rows;
        if (selected is QuickMotion previous && rows.Contains(previous)) QuickMotionList.SelectedItem = previous;
        else if (selected != null)
        {
            Viewport.PausePlayback();
            QuickStatus.Text = rows.Length == 0
                ? "该分类或搜索暂无匹配动作。可选择“全部动作”或“待确认”；当前预览已暂停。"
                : "筛选已更新，当前预览已暂停。请选择列表中的动作播放。";
        }
        _quickSync = false;
        QuickCount.Text = $"显示 {rows.Length} / {_quickRows.Count} 个动作";
        SyncQuickAssignment(QuickMotionList.SelectedItem as QuickMotion);
    }

    private async void OnQuickMotionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_quickSync && !_quickBusy && QuickMotionList.SelectedItem is QuickMotion row) await PlayQuickMotionAsync(row);
    }
    private async Task PlayQuickMotionAsync(QuickMotion row)
    {
        SyncQuickAssignment(row);
        SetQuickBusy(true, "正在加载：" + row.Display);
        try
        {
            if (QuickGameCombo.SelectedItem is not QuickGame game ||
                SelectedQuickSubject is not { } subject ||
                !string.Equals(Path.GetFullPath(game.Root), _loadedFolderPath, StringComparison.OrdinalIgnoreCase) ||
                _quickModelGame != QuickModelKey(game, subject) || !_previewMeshPaths.SequenceEqual(subject.MeshPaths, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("资源来源或模型已改变，请重新选择动作集后播放");
            ClearMotionState();
            SetMotionListUi(row.Path, row.Motions, row.Index);
            EnsureQuickMotionSelected(row, SelectedBaseMotion);
            await LoadSelectedMotionAsync(null);
            if (!Viewport.HasAnimation || _loadedPreviewRequest != _previewRequest || _loadedMotionSource != row.Motion.SourceIndex)
                throw new InvalidDataException(ActionStatus.Text);
            if (ResolveQuickVisualWeapon(row) is QuickWeapon weapon)
            {
                if (Viewport.WeaponAttachmentCount != weapon.Attachments.Length || _quickLoadedWeapon != QuickAttachmentKey(game, subject, weapon))
                    await LoadQuickWeaponsAsync(false, weapon);
                if (Viewport.WeaponAttachmentCount != weapon.Attachments.Length)
                    throw new InvalidDataException(_quickWeaponStatus);
            }
            UpdateQuickModelNote();
            QuickStatus.Text = $"正在播放：{row.Display}\n{Viewport.Duration:F2} 秒 · 时间轴可拖动 · 空格暂停/继续";
            await WriteQuickReportAsync(true, null);
        }
        catch (Exception ex)
        {
            ClearMotionState();
            QuickStatus.Text = "此动作无法预览：" + ex.Message + "\n可继续选择其他动作。";
            await WriteQuickReportAsync(false, ex.ToString());
        }
        finally { SetQuickBusy(false); }
    }

    private async void OnQuickReplay(object? sender, RoutedEventArgs e)
    {
        if (!_quickBusy && QuickMotionList.SelectedItem is QuickMotion row) await PlayQuickMotionAsync(row);
    }
    private async void OnQuickTextures(object? sender, RoutedEventArgs e)
    {
        await LoadQuickTexturesAsync();
    }
    private async Task LoadQuickTexturesAsync()
    {
        if (_quickBusy) return;
        SetQuickBusy(true, "正在加载外观贴图…");
        try
        {
            var time = Viewport.CurrentTime;
            var playing = Viewport.IsPlaying;
            var hadAnimation = Viewport.HasAnimation;
            await LoadPreviewTexturesAsync();
            SetQuickBusy(true);
            var textureStatus = ActionStatus.Text;
            // Fresh texture meshes need the MOT helper bones again before attachments can resolve.
            if (hadAnimation)
            {
                await LoadSelectedMotionAsync(null);
                if (!Viewport.HasAnimation || _loadedPreviewRequest != _previewRequest)
                    throw new InvalidDataException("重新加载动作挂点失败：" + ActionStatus.Text);
                Viewport.ScrubTo(time);
                if (playing && !Viewport.IsPlaying) Viewport.TogglePlayback();
                if (!playing) Viewport.PausePlayback();
            }
            await LoadQuickWeaponsAsync(true);
            UpdateQuickModelNote();
            QuickStatus.Text = textureStatus + "\n" + _quickWeaponStatus;
            var ready = ResolveQuickVisualWeapon(QuickMotionList.SelectedItem as QuickMotion) is QuickWeapon weapon &&
                Viewport.WeaponAttachmentCount == weapon.Attachments.Length;
            await WriteQuickReportAsync(ready, ready ? null : _quickWeaponStatus);
        }
        catch (Exception ex)
        {
            QuickStatus.Text = "外观加载失败：" + ex.Message;
            await WriteQuickReportAsync(false, ex.ToString());
        }
        finally { SetQuickBusy(false); }
    }
    private void OnQuickLayoutToggle(object? sender, RoutedEventArgs e)
    {
        if (_quickBusy) return;
        if (_quickCatalog != null) SetQuickLayout(!QuickPanel.IsVisible);
        else ActionStatus.Text = "请从“打开游戏武器动作预览”快捷入口启动。";
    }

    private async Task WriteQuickReportAsync(bool ready, string? error)
    {
        if (string.IsNullOrWhiteSpace(_quickReport)) return;
        try
        {
            await File.WriteAllTextAsync(_quickReport, JsonSerializer.Serialize(new
            {
                Ready = ready, Error = error,
                Game = (QuickGameCombo.SelectedItem as QuickGame)?.Id,
                Subject = SelectedQuickSubject?.Id,
                SubjectName = QuickSubjectTitle.Text,
                SubjectDescription = QuickSubjectDescription.Text,
                SubjectDescriptionVisible = QuickSubjectCard.IsVisible,
                ActionSet = (QuickWeaponCombo.SelectedItem as QuickWeapon)?.Id,
                Weapon = (QuickWeaponCombo.SelectedItem as QuickWeapon)?.Id,
                ActionCount = _quickRows.Count, ModelPaths = _previewMeshPaths,
                CategoryCounts = _quickRows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count()),
                MotionList = _currentMotlistPath, Motion = SelectedBaseMotion,
                HasAnimation = Viewport.HasAnimation, Playing = Viewport.IsPlaying,
                Viewport.WeaponAttachmentCount, Viewport.WeaponAttachmentNames,
                Viewport.RenderedWeaponAttachmentCount,
                Viewport.WeaponAttachmentTextureCount,
                Viewport.Duration, Viewport.CurrentTime, Viewport.RenderDiagnostics,
                MeshStats = Viewport.SceneMeshes.Select(m => new { m.VertexCount, m.FaceCount, Bones = m.Bones.Length }),
                Status = QuickStatus.Text
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex) { AppendLog("预览检查记录写入失败：" + ex.Message); }
    }

    private QuickWeapon? ResolveQuickVisualWeapon(QuickMotion? row = null)
    {
        if (QuickGameCombo.SelectedItem is not QuickGame game || QuickWeaponCombo.SelectedItem is not QuickWeapon weapon) return null;
        if (SelectedQuickSubject is not { } subject) return null;
        var path = row?.Path ?? _currentMotlistPath;
        if (path != null && weapon.AttachmentSources.TryGetValue(path, out var sourceId))
            return subject.ActionSets.Single(w => w.Id == sourceId);
        return weapon;
    }

    private void UpdateQuickModelNote()
    {
        QuickModelNote.Text = _quickWeaponStatus;
        ToolTip.SetTip(QuickModelNote, QuickModelNote.Text + "\n" + SelectedQuickSubject?.ModelNote + "\n" +
            (QuickWeaponCombo.SelectedItem as QuickWeapon)?.Note);
    }

    private async Task LoadQuickWeaponsAsync(bool textures, QuickWeapon? visualWeapon = null)
    {
        Viewport.ClearWeaponAttachments();
        _quickLoadedWeapon = null;
        if (_pak == null || QuickGameCombo.SelectedItem is not QuickGame game) return;
        var weapon = visualWeapon ?? ResolveQuickVisualWeapon(QuickMotionList.SelectedItem as QuickMotion);
        if (weapon == null) return;
        if (weapon.Attachments.Length == 0)
        {
            _quickWeaponStatus = "无附件：显示角色/对象本体（空手或无外部装备）。";
            if (SelectedQuickSubject is { } emptySubject) _quickLoadedWeapon = QuickAttachmentKey(game, emptySubject, weapon);
            return;
        }
        var source = _pak;
        var problems = new List<string>();
        foreach (var attachment in weapon.Attachments)
        {
            try
            {
                var key = game.Root + "|" + attachment.MeshPath + "|" + attachment.MaterialPath + "|" +
                    attachment.PoseList + "|" + attachment.PoseId + "|" + _selectedLod + "|" + textures;
                if (!_quickWeaponCache.TryGetValue(key, out var loaded))
                {
                    loaded = await Task.Run(() =>
                    {
                        Stream? Open(string path) { try { return source.ReadFile(path); } catch (FileNotFoundException) { return null; } }
                        var choices = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                        if (attachment.MaterialPath.Length > 0) choices[attachment.MeshPath] = attachment.MaterialPath;
                        using var stream = source.ReadFile(attachment.MeshPath);
                        var mesh = ViewportDataLoader.LoadMesh(stream, attachment.MeshPath, _selectedLod, Open, textures, source.CreateMaterialResolver(choices));
                        AnimationClip? pose = null;
                        if (!string.IsNullOrWhiteSpace(attachment.PoseList))
                        {
                            using var names = source.ReadFile(attachment.PoseList);
                            var motions = ViewportDataLoader.ListMotions(names, attachment.PoseList, Open);
                            var motion = motions.Single(m => m.MotionNumber == attachment.PoseId);
                            using var motionStream = source.ReadFile(attachment.PoseList);
                            pose = ViewportDataLoader.LoadAnimation(motionStream, attachment.PoseList, motion.SourceIndex,
                                mesh.Bones.Select(b=>b.Name).ToArray(), new[]{mesh}, Open);
                        }
                        return (mesh, pose);
                    });
                    _quickWeaponCache[key] = loaded;
                }
                if (!ReferenceEquals(source, _pak)) throw new InvalidOperationException("资源来源已改变");
                var m = attachment.LocalMatrix;
                if (m.Length != 16 || m.Any(v=>!float.IsFinite(v))) throw new InvalidDataException("武器挂点变换无效");
                Viewport.AddWeaponAttachment(loaded.Mesh, attachment.Name, attachment.Bone,
                    new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]), loaded.Pose, attachment.PoseTime,
                    attachment.FollowBodySkeleton, attachment.VisibleGroups);
            }
            catch (Exception ex) { problems.Add(attachment.Name + "：" + ex.Message); }
        }
        _quickWeaponStatus = problems.Count == 0
            ? "已加载武器模型：" + string.Join("、", Viewport.WeaponAttachmentNames)
            : "武器模型尚未完整加载：" + string.Join("；",problems);
        AppendLog(_quickWeaponStatus);
        if (problems.Count == 0 && SelectedQuickSubject is { } subject) _quickLoadedWeapon = QuickAttachmentKey(game, subject, weapon);
    }
}
