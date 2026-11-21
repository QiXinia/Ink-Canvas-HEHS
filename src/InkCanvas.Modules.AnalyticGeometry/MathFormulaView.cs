using InkCanvas.AnalyticGeometry.Algebra;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>RichEdit MathML rendering with a native XAML fallback for older Windows installations.</summary>
internal sealed class MathFormulaView : UserControl
{
    private readonly Grid _surface = new();
    private readonly MathFormulaPreview _fallback = new();
    private RichEditBox _native;
    private bool _nativeUnavailable;
    private string _expression;
    public string MathML { get; private set; }
    public bool UsesRichEdit => _native is not null && !_nativeUnavailable;

    public MathFormulaView()
    {
        Content = _surface;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _surface.Children.Add(_fallback);
        AutomationProperties.SetName(this, "公式排版");
    }

    public bool SetExpression(string expression)
    {
        if (_expression == expression && MathML is not null) return true;
        try
        {
            string markup = MathMarkup.ToMathML(expression);
            SetMathML(markup);
            _expression = expression;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            MathML = null;
            _expression = null;
            return false;
        }
    }

    public void SetMathML(string markup)
    {
        // Validate markup before touching RichEdit, whose SetMathML clears invalid content.
        MathMarkup.ToExpression(markup);
        if (!_nativeUnavailable)
        {
            try
            {
                if (_native is null)
                {
                    _native = new RichEditBox
                    {
                        FontSize = 22, MinHeight = 58, MaxHeight = 144, IsSpellCheckEnabled = false,
                        IsTextPredictionEnabled = false, PreventKeyboardDisplayOnProgrammaticFocus = true,
                        AcceptsReturn = false, IsTabStop = false, BorderThickness = new Thickness(0),
                    };
                    _native.TextDocument.SetMathMode(RichEditMathMode.MathOnly);
                    _surface.Children.Add(_native);
                }
                _native.IsReadOnly = false;
                _native.TextDocument.SetMathML(markup);
                _native.TextDocument.GetMathML(out string nativeMarkup);
                _native.IsReadOnly = true;
                MathML = string.IsNullOrWhiteSpace(nativeMarkup) ? markup : nativeMarkup;
                _fallback.Visibility = Visibility.Collapsed;
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                or NotImplementedException or InvalidOperationException)
            {
                _nativeUnavailable = true;
                if (_native is not null) _native.Visibility = Visibility.Collapsed;
            }
        }
        _fallback.SetMathML(markup);
        _fallback.Visibility = Visibility.Visible;
        MathML = markup;
    }
}
