using System.Numerics;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board;

public sealed partial class BoardService
{
    private sealed record SideBoardSnapshot(int PageIndex, List<PreparedPage> Pages);
    private sealed class SideBoardFile
    {
        public int Version { get; set; }
        public int PageIndex { get; set; }
        public List<BoardPageFile> Pages { get; set; } = new();
    }

    public BoardService SideBoard { get; private set; }
    public event Action SideBoardChanged;
    public IEnumerable<InkController> DocumentPages => SideBoard is { } side ? Pages.Concat(side.Pages) : Pages;

    public BoardService EnableSideBoard()
    {
        if (SideBoard is not null) return SideBoard;

        var side = new BoardService { Background = Background, GridSpacing = GridSpacing };
        SideBoard = side;
        var observed = new HashSet<InkController>();
        side.PageChanged += ObservePages;
        side.BackgroundChanged += Changed;
        side.ImagesChanged += Changed;
        side.NativeObjectsChanged += Changed;
        side.ImagePlacementChanged += _ => Changed();
        side.MediaPlacementChanged += _ => Changed();
        ObservePages();
        return side;

        void Changed() => SideBoardChanged?.Invoke();
        void ViewChanged(Matrix3x2 _) => Changed();
        void ObservePages()
        {
            foreach (var page in observed.Where(page => !side.Pages.Contains(page)).ToArray())
            {
                page.Document.Changed -= Changed;
                page.History.Changed -= Changed;
                page.ViewChanged -= ViewChanged;
                observed.Remove(page);
            }
            foreach (var page in side.Pages)
            {
                if (!observed.Add(page)) continue;
                page.Document.Changed += Changed;
                page.History.Changed += Changed;
                page.ViewChanged += ViewChanged;
            }
            Changed();
        }
    }
}
