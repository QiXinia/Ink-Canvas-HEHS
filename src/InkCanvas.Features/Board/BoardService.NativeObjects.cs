using System.Runtime.CompilerServices;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board;

public sealed partial class BoardService
{
    private sealed class NativeObjects
    {
        public List<BoardTextObject> Texts { get; } = new();
        public List<BoardFormulaObject> Formulas { get; } = new();
        public IEnumerable<ISelectionElement> Elements => Texts.Cast<ISelectionElement>().Concat(Formulas);

        public NativeObjects Copy(bool preserveId)
        {
            var copy = new NativeObjects();
            copy.Texts.AddRange(Texts.Select(text => text.Copy(preserveId)));
            copy.Formulas.AddRange(Formulas.Select(formula => formula.Copy(preserveId)));
            if (!preserveId) InkController.RemapSelectionGroups(copy.Elements);
            return copy;
        }
    }

    private readonly ConditionalWeakTable<InkController, NativeObjects> _nativePages = new();
    private NativeObjects NativePage(InkController page) => _nativePages.GetValue(page, _ => new NativeObjects());
    public IReadOnlyList<BoardTextObject> CurrentTextObjects => NativePage(Current).Texts;
    public IReadOnlyList<BoardFormulaObject> CurrentFormulaObjects => NativePage(Current).Formulas;

    public event Action NativeObjectsChanged;

    private void ChangeNativeObjects(InkController page)
    {
        page.NotifyElementsChanged();
        NativeObjectsChanged?.Invoke();
    }

    private void SetNativeObjects(InkController page, NativeObjects items)
    {
        _nativePages.Remove(page);
        if (items is null) return;
        foreach (var text in items.Texts) text.Attach(this, page);
        foreach (var formula in items.Formulas) formula.Attach(this, page);
        _nativePages.Add(page, items);
        page.NotifyElementsChanged();
    }

    public int GetTextIndex(InkController page, BoardTextObject text) =>
        _pages.Contains(page) ? NativePage(page).Texts.IndexOf(text) : -1;
    public int GetFormulaIndex(InkController page, BoardFormulaObject formula) =>
        _pages.Contains(page) ? NativePage(page).Formulas.IndexOf(formula) : -1;
    public void NotifyTextPlacement(BoardTextObject _) => ChangeNativeObjects(Current);
    public void NotifyFormulaPlacement(BoardFormulaObject _) => ChangeNativeObjects(Current);
    public void SetTextPresent(InkController page, BoardTextObject text, bool present, int restoreIndex) =>
        SetNativePresent(page, NativePage(page).Texts, text, present, restoreIndex);
    public void SetFormulaPresent(InkController page, BoardFormulaObject formula, bool present, int restoreIndex) =>
        SetNativePresent(page, NativePage(page).Formulas, formula, present, restoreIndex);

    private void SetNativePresent<T>(InkController page, List<T> list, T item, bool present, int restoreIndex)
    {
        if (!_pages.Contains(page)) return;
        bool exists = list.Contains(item);
        if (exists == present) return;
        if (present) list.Insert(restoreIndex < 0 ? list.Count : Math.Min(restoreIndex, list.Count), item);
        else list.Remove(item);
        ChangeNativeObjects(page);
    }

    public bool WriteNativeBoardObjects(IEnumerable<BoardTextObject> texts, IEnumerable<BoardFormulaObject> formulas,
        IReadOnlyList<InkStroke> replace = null)
    {
        var page = Current;
        var addedTexts = texts.Select(text => text.Copy()).ToArray();
        var addedFormulas = formulas.Select(formula => formula.Copy()).ToArray();
        if (addedTexts.Length + addedFormulas.Length == 0) return false;
        var replacing = (replace ?? Array.Empty<InkStroke>()).ToHashSet();
        var removed = page.Document.Strokes.Select((stroke, index) => (Index: index, Stroke: stroke))
            .Where(entry => replacing.Contains(entry.Stroke)).ToArray();
        if (removed.Length != replacing.Count || removed.Any(entry => entry.Stroke.InkLayerIndex != page.ActiveInkLayer)) return false;
        var native = NativePage(page);
        var ids = page.DrawingObjects.Select(item => item.Id).ToHashSet();
        if (addedTexts.Any(text => !ids.Add(text.Id)) || addedFormulas.Any(formula => !ids.Add(formula.Id))) return false;

        long layer = page.NextLayerOrder();
        foreach (var text in addedTexts)
        {
            BoardAiObjectSerialization.Normalize(text);
            if (!BoardAiObjectSerialization.IsValid(text)) return false;
            text.Attach(this, page);
            text.LayerOrder = layer++;
        }
        foreach (var formula in addedFormulas)
        {
            BoardAiObjectSerialization.Normalize(formula);
            if (!BoardAiObjectSerialization.IsValid(formula)) return false;
            formula.Attach(this, page);
            formula.LayerOrder = layer++;
        }

        var removedStrokes = removed.Select(entry => entry.Stroke).ToArray();
        Apply();
        page.History.Push(new DelegateAction(removed.Length == 0 ? "写入 AI 板书" : "替换板书", Apply, Revert));
        page.SelectElement((ISelectionElement)addedTexts.FirstOrDefault() ?? addedFormulas[0]);
        return true;

        void Apply()
        {
            page.ClearSelection();
            native.Texts.AddRange(addedTexts);
            native.Formulas.AddRange(addedFormulas);
            page.Document.RemoveAll(removedStrokes);
            ChangeNativeObjects(page);
        }

        void Revert()
        {
            page.ClearSelection();
            native.Texts.RemoveAll(addedTexts.Contains);
            native.Formulas.RemoveAll(addedFormulas.Contains);
            page.Document.InsertRange(removed);
            ChangeNativeObjects(page);
        }
    }

    public bool UpdateAiObject(BoardTextObject target, Action<BoardTextObject> update, string name)
    {
        var page = Current;
        if (GetTextIndex(page, target) < 0) return false;
        var before = target.Copy();
        var after = target.Copy();
        update(after);
        BoardAiObjectSerialization.Normalize(after);
        if (!BoardAiObjectSerialization.IsValid(after) || BoardAiObjectSerialization.SameState(before, after)) return false;
        CommitNativeEdit(page, name, () => target.CopyStateFrom(after), () => target.CopyStateFrom(before));
        return true;
    }

    public bool UpdateAiObject(BoardFormulaObject target, Action<BoardFormulaObject> update, string name)
    {
        var page = Current;
        if (GetFormulaIndex(page, target) < 0) return false;
        var before = target.Copy();
        var after = target.Copy();
        update(after);
        BoardAiObjectSerialization.Normalize(after);
        if (!BoardAiObjectSerialization.IsValid(after) || BoardAiObjectSerialization.SameState(before, after)) return false;
        CommitNativeEdit(page, name, () => target.CopyStateFrom(after), () => target.CopyStateFrom(before));
        return true;
    }

    private void CommitNativeEdit(InkController page, string name, Action apply, Action revert)
    {
        Apply();
        page.History.Push(new DelegateAction(name, Apply, Revert));

        void Apply() { apply(); ChangeNativeObjects(page); }
        void Revert() { revert(); ChangeNativeObjects(page); }
    }

    public bool UngroupNativeSelection()
    {
        var page = Current;
        var groups = page.SelectedElements.OfType<IGroupedSelectionElement>().Where(item => !string.IsNullOrEmpty(item.SelectionGroupId))
            .Select(item => (Item: item, Group: item.SelectionGroupId)).ToArray();
        if (groups.Length == 0) return false;
        CommitNativeEdit(page, "取消板书分组",
            () => { foreach (var (item, _) in groups) item.SelectionGroupId = ""; },
            () => { foreach (var (item, group) in groups) item.SelectionGroupId = group; });
        return true;
    }

    public bool EditNativeSelection(BoardPlanEditPolicy.BoardObjectEdit edit)
    {
        var page = Current;
        var native = NativePage(page);
        var selection = page.SelectedElements.ToHashSet();
        var texts = native.Texts.Select((item, index) => (Item: item, Index: index)).Where(entry => selection.Contains(entry.Item))
            .Select(entry => (entry.Item, entry.Index, Before: entry.Item.Copy(), After: entry.Item.Copy(), Remove: ShouldRemove(entry.Item.Type?.ToLowerInvariant() ?? "text"))).ToArray();
        var formulas = native.Formulas.Select((item, index) => (Item: item, Index: index)).Where(entry => selection.Contains(entry.Item))
            .Select(entry => (entry.Item, entry.Index, Before: entry.Item.Copy(), After: entry.Item.Copy(), Remove: ShouldRemove("formula"))).ToArray();
        if (texts.Length + formulas.Length == 0) return false;
        foreach (var entry in texts)
        {
            entry.After.FontSize = edit.ExplicitFontSize ?? entry.After.FontSize * edit.FontScale;
            entry.After.LineHeight = edit.ExplicitLineHeight ?? entry.After.LineHeight * edit.LineHeightScale;
            entry.After.Columns = edit.Columns ?? entry.After.Columns;
            if (!string.IsNullOrEmpty(edit.Align)) entry.After.Align = edit.Align;
            BoardAiObjectSerialization.Normalize(entry.After);
        }
        foreach (var entry in formulas)
        {
            entry.After.FontSize = edit.ExplicitFontSize ?? entry.After.FontSize * edit.FontScale;
            BoardAiObjectSerialization.Normalize(entry.After);
        }
        if (!texts.Any(entry => entry.Remove || !BoardAiObjectSerialization.SameState(entry.Before, entry.After))
            && !formulas.Any(entry => entry.Remove || !BoardAiObjectSerialization.SameState(entry.Before, entry.After))) return false;
        CommitNativeEdit(page, "修改 AI 板书", Apply, Revert);
        return true;

        bool ShouldRemove(string kind) => edit.RemoveKinds.Contains(kind) || edit.KeepKinds.Count > 0 && !edit.KeepKinds.Contains(kind);
        void Apply()
        {
            foreach (var entry in texts)
                if (entry.Remove) { native.Texts.Remove(entry.Item); page.SelectedElements.Remove(entry.Item); }
                else entry.Item.CopyStateFrom(entry.After);
            foreach (var entry in formulas)
                if (entry.Remove) { native.Formulas.Remove(entry.Item); page.SelectedElements.Remove(entry.Item); }
                else entry.Item.CopyStateFrom(entry.After);
        }
        void Revert()
        {
            foreach (var entry in texts)
                if (entry.Remove) native.Texts.Insert(entry.Index, entry.Item);
                else entry.Item.CopyStateFrom(entry.Before);
            foreach (var entry in formulas)
                if (entry.Remove) native.Formulas.Insert(entry.Index, entry.Item);
                else entry.Item.CopyStateFrom(entry.Before);
        }
    }
}
