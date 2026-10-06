using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.UI;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The Load window (LoadUI.Show) and the Save window (SaveUI.Show) rebuild one row per save on every opening: with
/// many saves (188 measured) each opening froze the game ~400 ms (re-binding every row, then UI layout over all of
/// them), plus ~630 ms the first time to decode every save screenshot (PNG) in the same frame.
/// - Rows: the UI layout cost is per ACTIVE row (each layout pass walks all of them; one row component even runs a
///   nested ForceRebuildLayoutImmediate). Only the rows around the view are active (the first VisibleRows when a
///   window opens), the others are switched on as they come into view, with their place kept so the scrollbar
///   does not move (see LazyRows). Every save stays reachable.
/// - Reopening: the windows are hidden with a CanvasGroup, so their rows stay alive and bound between openings.
///   When the save list is unchanged (same names and timestamps) and nothing was saved or deleted since the
///   window's last build, we skip the rebuild and
///   replay only the rest of the game's Show (state reset, scroll to the top, rows unselected, controller focus,
///   show the panel), hiding again the rows revealed by scrolling.
/// - Screenshots: SerializationManager keeps decoded screenshots in a cache (_saveScreenshots). We fill it in the
///   background, one save per frame, so the first opening finds them ready.
/// - Rows: a window creates its row objects the first time it needs them. After the screenshots, the rows of both
///   windows are created in advance (see RowPrebuilder): the game reuses them at its first opening.
/// </summary>
internal sealed unsafe class SaveMenusReuse : Feature
{
    public override string Name => "Save and load menus without freeze";
    protected override string Section => "SaveMenus";
    protected override string Description =>
        "Open the Load and Save windows without a freeze when there are many saves: rows appear as you scroll, the list is not rebuilt when the saves did not change, screenshots are prepared in advance.";

    private ConfigEntry<int> visibleRows, marginRows;
    private ConfigEntry<bool> preloadScreenshots;

    /// <summary>Per window type: the rows built last time and the save list they show.</summary>
    private sealed class Menu
    {
        public UIPanel Built;
        public string Signature, Pending;
        public int Count;
        public bool Rebuilding;
    }

    private static SaveMenusReuse self;
    private static readonly Menu loadMenu = new(), saveMenu = new();

    private static IntPtr panelShowCode, panelShowInfo; // UIPanel.Show, called non-virtually

    private readonly List<string> preloadNames = new();
    private int preloadIndex, sceneHandle, preloadDelay;
    private int saveRows;                      // rows the Save window shows: "new save" + non-autosaves
    private bool loadFound, saveFound;
    private int searchTries, searchWait;
    private const int SearchTries = 10, SearchGapFrames = 300;

    protected override void BindSettings(ConfigFile config)
    {
        visibleRows = config.Bind(Section, "VisibleRows", 20,
            new ConfigDescription("Save rows shown when a window opens.", new AcceptableValueRange<int>(5, 200)));
        marginRows = config.Bind(Section, "MarginRows", 4,
            new ConfigDescription("Save rows kept ready above and below the visible ones.", new AcceptableValueRange<int>(1, 50)));
        preloadScreenshots = config.Bind(Section, "PreloadScreenshots", true,
            "Prepare save screenshots in the background (one per frame) so the first opening does not decode them all at once.");
    }

    protected override string TryInstall()
    {
        panelShowInfo = IL2CPP.il2cpp_class_get_method_from_name(Il2CppClassPointerStore<UIPanel>.NativeClassPtr, "Show", 0);
        panelShowCode = panelShowInfo == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)panelShowInfo;
        if (panelShowCode == IntPtr.Zero) return "UIPanel.Show not found";
        self = this;
        LazyRows.Install();
        Patch(typeof(LoadUI), nameof(LoadUI.Show), nameof(LoadShowPrefix), nameof(LoadShowPostfix));
        Patch(typeof(SaveUI), nameof(SaveUI.Show), nameof(SaveShowPrefix), nameof(SaveShowPostfix));
        Patch(typeof(SerializationManager), nameof(SerializationManager.Save), nameof(InvalidateAll), null);
        Patch(typeof(SerializationManager), nameof(SerializationManager.DeleteSave), nameof(InvalidateAll), null);
        return null;
    }

    private static void Patch(Type type, string method, string prefix, string postfix) =>
        Plugin.Harmony.Patch(AccessTools.Method(type, method),
            prefix: new HarmonyMethod(typeof(SaveMenusReuse), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(SaveMenusReuse), postfix));

    // ---- the two windows ----

    private static bool LoadShowPrefix(LoadUI __instance) =>
        ShowPrefix(loadMenu, __instance, __instance.slotList, __instance.scrollRect, () => ReopenLoad(__instance));
    private static void LoadShowPostfix(LoadUI __instance) =>
        ShowPostfix(loadMenu, __instance, __instance.slotList, __instance.scrollRect);
    private static bool SaveShowPrefix(SaveUI __instance) =>
        ShowPrefix(saveMenu, __instance, __instance.slotList, __instance.scrollRect, () => ReopenSave(__instance));
    private static void SaveShowPostfix(SaveUI __instance) =>
        ShowPostfix(saveMenu, __instance, __instance.slotList, __instance.scrollRect);
    /// <summary>
    /// A save was written or deleted: both windows rebuild at their next opening. Saving also replaces (destroys)
    /// that save's cached screenshot, so a row kept from before would show a destroyed texture (white).
    /// </summary>
    private static void InvalidateAll()
    {
        loadMenu.Built = null;
        saveMenu.Built = null;
    }

    /// <summary>LoadUI.Show without its row rebuild (BeginUpdate .. EndUpdate).</summary>
    private static void ReopenLoad(LoadUI w)
    {
        w._deleting = false;
        ResetList(w.scrollRect, w.toggleGroup);
        w.questDetails?.InitializeEmpty();
        FinishShow(w);
    }

    /// <summary>SaveUI.Show without its row rebuild (row 0 is the "new save" slot, autosaves are not listed).</summary>
    private static void ReopenSave(SaveUI w)
    {
        w._saving = false;
        w._deleting = false;
        w._currentSelectedSave = string.Empty;
        ResetList(w.scrollRect, w.toggleGroup);
        FinishShow(w);
    }

    private static void ResetList(ScrollRect scroll, ToggleGroup group)
    {
        if (scroll is not null) ScrollRectExtensions.SetVerticalNormalizedPositionWithoutSfx(scroll);
        if (group is null) return;
        group.allowSwitchOff = true;
        group.SetAllTogglesOff(true); // Show re-binds every row with its toggle off
    }

    private static void FinishShow(UIPanel w)
    {
        if (Singleton<PlayerInputManager>.Instance is { } input && input.IsController) w.OnControllerConnected();
        ((delegate* unmanaged<IntPtr, IntPtr, void>)panelShowCode)(w.Pointer, panelShowInfo); // base.Show()
    }

    // ---- reopen without rebuild ----

    private static bool ShowPrefix(Menu m, UIPanel panel, ItemListUI l, ScrollRect scroll, Action reopen)
    {
        m.Rebuilding = false;
        LazyRows.Begin(null, 0, 0);
        if (self == null || !self.Active) { m.Built = null; return true; }
        try
        {
            m.Pending = SaveSignature();
            if (m.Pending != null && m.Built is not null && Direct.Alive(m.Built) && m.Built.Pointer == panel.Pointer &&
                m.Pending == m.Signature && l is not null && l._displayedInstanceCount == m.Count)
            {
                reopen(); // unselects every row first (hidden toggles leave the group)
                StartLazy(l, scroll, m.Count); // back to the top rows (Show scrolled to the top)
                return false;
            }
            LazyRows.Begin(l, 0, self.visibleRows.Value);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{self.Name}: {e.Message}, normal opening");
            m.Built = null;
            return true;
        }
        m.Rebuilding = true;
        return true;
    }

    private static void ShowPostfix(Menu m, UIPanel panel, ItemListUI l, ScrollRect scroll)
    {
        if (!m.Rebuilding) return;
        m.Rebuilding = false;
        int count = LazyRows.End(l);
        if (count == 0) count = l?._displayedInstanceCount ?? 0;
        m.Built = m.Pending == null || l is null ? null : panel;
        m.Signature = m.Pending;
        m.Count = count;
        if (l is not null) StartLazy(l, scroll, count);
    }

    private static void StartLazy(ItemListUI l, ScrollRect scroll, int count) =>
        LazyRows.Start(self, l, scroll, count, 0, self.visibleRows.Value, self.marginRows.Value);

    /// <summary>Save names + timestamps, in the game's order; null if unavailable.</summary>
    private static string SaveSignature()
    {
        var saves = ReadSaves();
        if (saves == null) return null;
        var sb = new StringBuilder();
        foreach (var s in saves) sb.Append(s.Name).Append('|').Append(s.UnixTimestamp).Append('\n');
        return sb.ToString();
    }

    private readonly struct SaveEntry
    {
        public readonly string Name;
        public readonly int UnixTimestamp;
        public readonly bool IsAutoSave;
        public SaveEntry(string name, int unixTimestamp, bool isAutoSave)
        {
            Name = name; UnixTimestamp = unixTimestamp; IsAutoSave = isAutoSave;
        }
    }

    /// <summary>
    /// SerializationManager.GetValidSaves read from memory, in the game's order; null if unavailable.
    /// Il2CppInterop returns a wrong SaveHeader for ValueTuple&lt;string, SaveHeader&gt;.Item2 (IsAutoSave was read
    /// true for every save), so the List is walked like SaveUI.Show does: List._items / _size, elements
    /// (string Item1, SaveHeader Item2) with UnixTimestamp and IsAutoSave inside the header. The offsets and the
    /// element size come from the game's classes (see <see cref="EntryLayout"/>).
    /// </summary>
    private static List<SaveEntry> ReadSaves()
    {
        SerializationManager sm = Singleton<SerializationManager>.Instance;
        if (sm is null) return null;
        var handle = sm.GetValidSaves(out var saves);
        try
        {
            if (saves == null) return null;
            byte* list = (byte*)saves.Pointer;
            if (!ListLayout(list)) return null;
            byte* items = *(byte**)(list + listItems);
            if (items == null || !EntryLayout(items)) return null;
            int size = *(int*)(list + listSize);
            if (size < 0 || (ulong)size > *(ulong*)(items + Layout.ArrayLength)) return null;
            var result = new List<SaveEntry>(size);
            for (int i = 0; i < size; i++)
            {
                byte* e = items + Layout.ArrayData + (long)i * entrySize;
                IntPtr name = *(IntPtr*)(e + entryName);
                byte* header = e + entryHeader;
                result.Add(new SaveEntry(name == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(name),
                                         *(int*)(header + headerTimestamp), header[headerAutoSave] != 0));
            }
            return result;
        }
        finally { handle.Dispose(); }
    }

    private static int listItems, listSize, entrySize, entryName, entryHeader, headerTimestamp, headerAutoSave;
    private static bool layoutBad;

    /// <summary>List field offsets, resolved once; false if they cannot be trusted.</summary>
    private static bool ListLayout(byte* list)
    {
        if (layoutBad) return false;
        if (listItems > 0) return true;
        IntPtr listClass = Layout.ClassOf(list);
        listItems = Layout.Field(listClass, "_items");
        listSize = Layout.Field(listClass, "_size");
        return listItems > 0 && listSize > 0 || Bad("List fields");
    }

    /// <summary>Element size and offsets (name, header fields), resolved once; false if they cannot be trusted.</summary>
    private static bool EntryLayout(byte* items)
    {
        if (layoutBad) return false;
        if (entrySize > 0) return true;
        var (size, tuple) = Layout.ArrayElement(items);
        entryName = Layout.ValueField(tuple, "Item1");
        entryHeader = Layout.ValueField(tuple, "Item2");
        IntPtr header = Layout.FieldClass(tuple, "Item2");
        headerTimestamp = Layout.ValueField(header, "UnixTimestamp");
        headerAutoSave = Layout.ValueField(header, "IsAutoSave");
        if (entryName < 0 || entryHeader < 0 || headerTimestamp < 0 || headerAutoSave < 0 ||
            entryName + IntPtr.Size > size || entryHeader + headerAutoSave >= size || entryHeader + headerTimestamp + 4 > size)
            return Bad($"element size {size}, name {entryName}, header {entryHeader}, timestamp {headerTimestamp}, autosave {headerAutoSave}");
        entrySize = size;
        return true;
    }

    private static bool Bad(string why)
    {
        layoutBad = true;
        Plugin.Log.LogWarning($"Save menus: save list layout not recognised ({why}), menus open normally. Game update?");
        return false;
    }

    public override void Tick()
    {
        LazyRows.Tick();
        TickPreload();
    }

    // ---- screenshot preload ----

    private void TickPreload()
    {
        if (!Active || !preloadScreenshots.Value) return;
        int handle = Direct.ActiveSceneHandle;
        if (handle != sceneHandle) // new scene: wait a little, then (re)check every save once
        {
            sceneHandle = handle;
            preloadNames.Clear(); preloadIndex = 0; preloadDelay = 300;
            loadFound = saveFound = false; searchTries = 0; searchWait = 0;
            return;
        }
        if (preloadDelay > 0) { preloadDelay--; return; }
        if (preloadDelay == 0) { preloadDelay = -1; CollectNames(); }
        if (preloadIndex >= preloadNames.Count) { TickPrebuild(); return; }

        SerializationManager sm = Singleton<SerializationManager>.Instance;
        if (sm is null) return;
        var cache = sm._saveScreenshots;
        // skip saves already cached (cheap), decode at most one per frame
        while (preloadIndex < preloadNames.Count)
        {
            string name = preloadNames[preloadIndex++];
            if (cache != null && cache.ContainsKey(name)) continue;
            sm.LoadScreenshot(name);
            break;
        }
    }

    /// <summary>Queues the Load and Save windows of the scene for row prebuild (RowPrebuilder makes one row per frame).</summary>
    private void TickPrebuild()
    {
        if (!(loadFound && saveFound) && searchTries < SearchTries && --searchWait <= 0)
        {
            searchTries++;
            searchWait = SearchGapFrames;
            try
            {
                int queued = 0;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LoadUI>(), true))
                    if (o.TryCast<LoadUI>()?.slotList is { } l && RowPrebuilder.Add(l, preloadNames.Count, Name))
                    {
                        queued++;
                        loadFound = true;
                    }
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SaveUI>(), true))
                    if (o.TryCast<SaveUI>()?.slotList is { } l && RowPrebuilder.Add(l, saveRows, Name))
                    {
                        queued++;
                        saveFound = true;
                    }
                if (queued > 0)
                    Plugin.Log.LogInfo($"{Name}: preparing rows for {queued} window(s) " +
                                       $"(load {(loadFound ? "found" : "not found")}, save {(saveFound ? "found" : "not found")})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Name}: windows not found ({e.Message})");
            }
        }
        RowPrebuilder.Step();
    }

    private void CollectNames()
    {
        try
        {
            saveRows = 1;
            foreach (var s in ReadSaves() ?? new List<SaveEntry>())
            {
                preloadNames.Add(s.Name);
                if (!s.IsAutoSave) saveRows++;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{Name}: save list unavailable ({e.Message})");
        }
    }
}
