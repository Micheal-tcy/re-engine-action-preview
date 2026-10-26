using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using ReExtractor.Core;

namespace ReExtractor.Gui;

public partial class MainWindow
{
    private sealed class QuickSubject
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] MeshPaths { get; set; } = [];
        public Dictionary<string, string> Materials { get; set; } = new();
        public string ModelNote { get; set; } = "";
        public QuickWeapon[] ActionSets { get; set; } = [];
        public override string ToString() => Name;
    }

    private sealed class QuickSubcategoryDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private QuickSubject? SelectedQuickSubject => QuickSubjectCombo.SelectedItem as QuickSubject;
    private static string QuickModelKey(QuickGame game, QuickSubject subject) => game.Id + "|" + subject.Id;
    private static string QuickAttachmentKey(QuickGame game, QuickSubject subject, QuickWeapon set)
        => QuickModelKey(game, subject) + "|" + set.Id;
    private static string QuickResourcePath(string path) => path.Replace('\\', '/');

    private void UpdateQuickSubjectDescription()
    {
        var subject = SelectedQuickSubject;
        QuickSubjectTitle.Text = subject?.Name ?? "";
        QuickSubjectDescription.Text = subject?.Description ?? "";
        QuickSubjectCard.IsVisible = QuickPanel.IsVisible && !string.IsNullOrWhiteSpace(subject?.Description);
    }

    // Normalize in memory only. Existing catalogs, source resources and user notes stay intact.
    private static void NormalizeQuickCatalog(QuickCatalog catalog)
    {
        RequireUniqueIds(catalog.Games.Select(g => g.Id), "游戏");
        foreach (var game in catalog.Games)
        {
            var legacy = game.Subjects.Length == 0;
            if (legacy)
            {
                game.Subjects = [new QuickSubject
                {
                    Id = "default", Name = "默认角色", MeshPaths = game.MeshPaths,
                    Materials = game.Materials, ModelNote = game.ModelNote, ActionSets = game.Weapons
                }];
            }
            RequireUniqueIds(game.Subjects.Select(s => s.Id), game.Name + "的角色/对象");
            RequireUniqueIds(game.Subcategories.Select(s => s.Id), game.Name + "的子类");
            if (game.Subcategories.Any(s => s.Id is "all" or "unassigned"))
                throw new InvalidDataException("子类 ID 不能使用保留值 all / unassigned");
            foreach (var subject in game.Subjects)
            {
                subject.MeshPaths = subject.MeshPaths.Select(QuickResourcePath).ToArray();
                subject.Materials = subject.Materials.ToDictionary(p => QuickResourcePath(p.Key), p => QuickResourcePath(p.Value), StringComparer.OrdinalIgnoreCase);
                RequireUniqueIds(subject.ActionSets.Select(s => s.Id), subject.Name + "的动作集");
                foreach (var set in subject.ActionSets)
                {
                    set.MotionLists = set.MotionLists.Select(QuickResourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    set.AttachmentSources = set.AttachmentSources.ToDictionary(p => QuickResourcePath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
                    set.SubcategoryByMotionList = set.SubcategoryByMotionList.ToDictionary(p => QuickResourcePath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
                    set.ExcludedSources = set.ExcludedSources.ToDictionary(p => QuickResourcePath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
                    if (set.ExcludedSources.Any(p => !set.MotionLists.Contains(p.Key, StringComparer.OrdinalIgnoreCase) || p.Value.Any(index => index < 0)))
                        throw new InvalidDataException("动作排除记录无效：" + set.Id);
                    foreach (var pair in set.AttachmentSources)
                        if (!set.MotionLists.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || !subject.ActionSets.Any(s => s.Id == pair.Value))
                            throw new InvalidDataException("附件来源未匹配动作集：" + set.Id + " / " + pair.Key);
                    foreach (var pair in set.SubcategoryByMotionList)
                        if (!set.MotionLists.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(pair.Value) || pair.Value is "all" or "unassigned")
                            throw new InvalidDataException("子类映射无效：" + set.Id + " / " + pair.Key);
                }
                // Preserve the old aggregate's appearance routing as explicit normalized data.
                if (legacy && game.Id == "dd2" && subject.ActionSets.FirstOrDefault(s => s.Id == "job10") is { } aggregate)
                    foreach (var path in aggregate.MotionLists)
                    {
                        var owner = subject.ActionSets.FirstOrDefault(s => s.Id != "job10" && s.Attachments.Length > 0 && s.MotionLists.Contains(path, StringComparer.OrdinalIgnoreCase));
                        if (owner != null) aggregate.AttachmentSources.TryAdd(path, owner.Id);
                    }
            }
        }
    }

    private static void RequireUniqueIds(IEnumerable<string> source, string label)
    {
        var ids = source.ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new InvalidDataException(label + " ID 为空或重复");
    }

    private void OnQuickSubjectChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_quickSync || _quickBusy || SelectedQuickSubject is not { } subject) return;
        ++_quickGeneration;
        _quickSync = true;
        QuickWeaponCombo.ItemsSource = subject.ActionSets;
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
        QuickModelNote.Text = "选择动作集后查看动作。";
        ToolTip.SetTip(QuickModelNote, subject.ModelNote);
        QuickStatus.Text = "已选择" + subject.Name + "，请选择动作集。";
        QuickCount.Text = subject.ActionSets.Length + " 个动作集";
    }

    private static void EnsureQuickMotionSelected(QuickMotion requested, MotionInfo? actual)
    {
        if (actual?.SourceIndex != requested.Motion.SourceIndex || actual.MotionNumber != requested.Motion.MotionNumber)
            throw new InvalidDataException("所选动作被当前预览用途筛选排除，已停止播放，未替换为其他动作：" + requested.Display);
    }
}
