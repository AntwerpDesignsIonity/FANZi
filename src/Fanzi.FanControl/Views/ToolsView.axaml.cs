using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fanzi.FanControl.ViewModels;
using System.Collections.Specialized;

namespace Fanzi.FanControl.Views;

public partial class ToolsView : UserControl
{
    private ToolsViewModel? _vm;

    public ToolsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.LogLines.CollectionChanged -= OnLogChanged;
        _vm = DataContext as ToolsViewModel;
        if (_vm is not null) _vm.LogLines.CollectionChanged += OnLogChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Drives, drive health and power plans are read the first time the tab is opened (not at app start).
        if (_vm is not null) _ = _vm.EnsureLoadedAsync();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.UIThread.Post(() => ConsoleScroll?.ScrollToEnd(), DispatcherPriority.Background);
    }
}
