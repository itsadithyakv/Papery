using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Papery.Controls;

/// <summary>A canvas that can change the mouse pointer (resize arrows over the crop handles).</summary>
public sealed partial class CropCanvas : Canvas
{
    private InputSystemCursorShape _shape = InputSystemCursorShape.Arrow;

    public void SetCursor(InputSystemCursorShape shape)
    {
        if (shape != _shape)
        {
            _shape = shape;
            ProtectedCursor = InputSystemCursor.Create(shape);
        }
    }
}
