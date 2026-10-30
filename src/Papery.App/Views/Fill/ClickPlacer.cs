using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Papery.Controls;
using Papery.Pdf.Editing;

namespace Papery.Views.Fill;

/// <summary>A page tool for "click to put this here" (ticks, dates, signatures); a drag gives the size too.</summary>
internal sealed class ClickPlacer(Action<int, PagePoint, PagePoint> placed) : IPageTool
{
    private readonly Rectangle _preview = new()
    {
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x3D)),
        StrokeThickness = 1.5,
        StrokeDashArray = [3, 2],
    };

    private PagePoint _start;
    private int _page = -1;

    public void Press(PageToolInput input)
    {
        _page = input.Page;
        _start = input.Point;
        input.Layer.Children.Add(_preview);
        Move(input);
    }

    public void Move(PageToolInput input)
    {
        if (_page != input.Page)
        {
            return;
        }

        Canvas.SetLeft(_preview, Math.Min(_start.X, input.Point.X) * input.Width);
        Canvas.SetTop(_preview, Math.Min(_start.Y, input.Point.Y) * input.Height);
        _preview.Width = Math.Abs(input.Point.X - _start.X) * input.Width;
        _preview.Height = Math.Abs(input.Point.Y - _start.Y) * input.Height;
    }

    public void Release(PageToolInput input)
    {
        (_preview.Parent as Panel)?.Children.Remove(_preview);
        if (_page == input.Page)
        {
            _page = -1;
            placed(input.Page, _start, input.Point);
        }
    }
}
