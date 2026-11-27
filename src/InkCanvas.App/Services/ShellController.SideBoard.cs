using InkCanvas.App.Views;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private BoardWindow _sideBoard;
    private bool _sideBoardTarget;
    private bool _assistantHiddenWithSideBoard;
    private BoardWindow ActiveBoardWindow => _sideBoardTarget ? _sideBoard ?? _board : _board;
    internal BoardWindow MainBoardWindow => _board;

    public void ToggleSideBoard()
    {
        if (_disposed || _exiting || _board is null) return;
        if (_sideBoard?.AppWindow.IsVisible == true) { HideSideBoard(); return; }
        if (_sideBoard is null)
        {
            var window = new BoardWindow(Services, this, _board, Services.Board.EnableSideBoard());
            _sideBoard = window;
            window.CurrentPageChanged += OnSideBoardPageChanged;
            window.Activated += (_, args) =>
            {
                if (ReferenceEquals(window, _sideBoard) && args.WindowActivationState != WindowActivationState.Deactivated)
                    SetSideBoardTarget(true);
            };
            window.Closed += (_, _) =>
            {
                window.CurrentPageChanged -= OnSideBoardPageChanged;
                if (!ReferenceEquals(window, _sideBoard)) return;
                _sideBoard = null;
                SetSideBoardTarget(false);
            };
            NotifyWindowOpened(window);
        }
        WindowChrome.KeepVisible(_sideBoard);
        _sideBoard.RestoreFromAnnotation();
        _sideBoard.Activate();
        SetSideBoardTarget(true);
        if (_assistantHiddenWithSideBoard)
        {
            _assistantHiddenWithSideBoard = false;
            Assistant?.SetCanvasVisible(true);
        }
    }

    internal void HideSideBoard()
    {
        if (_sideBoard?.AppWindow.IsVisible != true) return;
        if (_sideBoardTarget && Assistant?.IsOpen == true)
        {
            _assistantHiddenWithSideBoard = true;
            Assistant.SetCanvasVisible(false);
        }
        _sideBoard.HideSideBoardWindow();
        SetSideBoardTarget(false);
        _board?.Activate();
    }

    private void CloseSideBoard()
    {
        var window = _sideBoard;
        if (window is null) return;
        if (_sideBoardTarget) Assistant?.SetCanvasVisible(false);
        SetSideBoardTarget(false);
        _sideBoard = null;
        _assistantHiddenWithSideBoard = false;
        window.CloseForShutdown();
    }

    private void SetSideBoardTarget(bool side)
    {
        if (_sideBoardTarget == side) return;
        _sideBoardTarget = side;
        _shapePicker?.Close();
        _palette?.Close();
        CanvasToolTargetChanged?.Invoke();
        Assistant?.NotifyBoardChanged();
    }

    private void OnSideBoardPageChanged()
    {
        if (!_sideBoardTarget) return;
        _shapePicker?.Close();
        _palette?.Close();
        CanvasToolTargetChanged?.Invoke();
        Assistant?.NotifyBoardChanged();
    }
}
