using Avalonia.Controls;
using Avalonia.Input;

namespace Fanzi.FanControl.Views;

public partial class MiniOverlayWindow : Window
{
    public MiniOverlayWindow()
    {
        InitializeComponent();

        var drag = this.FindControl<Border>("DragRoot");
        if (drag is not null)
        {
            drag.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }

        var closeBtn = this.FindControl<Button>("CloseBtn");
        if (closeBtn is not null) closeBtn.Click += (_, _) => Hide();
    }
}
