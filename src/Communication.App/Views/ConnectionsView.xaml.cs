using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Communication.App.ViewModels;

namespace Communication.App.Views;

public partial class ConnectionsView : UserControl
{
    private bool _scrollPending;
    private void EndpointContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionViewModel endpoint } && DataContext is WorkspaceViewModel workspace)
            workspace.Selected = endpoint;
    }
    private async void RemoveEndpointClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ConnectionViewModel endpoint } && DataContext is WorkspaceViewModel workspace)
        {
            workspace.Selected = endpoint;
            await workspace.RemoveCommand.ExecuteAsync();
        }
    }
    private void ReceiverSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel workspace && e.AddedItems.OfType<SendTargetOption>().FirstOrDefault() is { } option)
            workspace.ReceiverAddress = option.Address;
    }
    private GridLength _expandedPaneHeight = new(280);
    private void ToggleLowerPaneClick(object sender, RoutedEventArgs e)
    {
        var collapse = !Equals(LowerPane.Tag, "True");
        if (collapse)
            _expandedPaneHeight = LowerPaneRow.Height;
        LowerPane.Tag = collapse ? "True" : "False";
        LowerPaneRow.MinHeight = collapse ? 0 : 260;
        LowerPaneRow.Height = collapse ? GridLength.Auto : _expandedPaneHeight;
        LowerPaneSplitter.IsEnabled = !collapse;
        e.Handled = true;
    }
    private void SendFormatClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel workspace || sender is not MenuItem { Tag: string format })
            return;
        workspace.SendHex = format == "HEX";
        if (!workspace.SendHex)
            workspace.SendEncoding = format;
        e.Handled = true;
    }
    public ConnectionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            TrafficFilterPopup.IsOpen = false;
            TrafficDataPopup.IsOpen = false;
            if (e.OldValue is WorkspaceViewModel oldWorkspace)
                oldWorkspace.Traffic.CollectionChanged -= OnTrafficChanged;
            if (e.NewValue is WorkspaceViewModel workspace)
                workspace.Traffic.CollectionChanged += OnTrafficChanged;
        };
        Unloaded += (_, _) =>
{
    TrafficFilterPopup.IsOpen = false;
    TrafficDataPopup.IsOpen = false;
}
;
    }
    private void TrafficGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column.SortMemberPath != nameof(TrafficRow.Timestamp) || DataContext is not WorkspaceViewModel workspace)
            return;
        workspace.ToggleTrafficTimeSort();
        QueueFollowTail();
    }
    private void TrafficFilterHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridColumnHeader header || DataContext is not WorkspaceViewModel workspace)
            return;
        var filter = (header.Tag as string) switch
        {
            "Direction" => workspace.TrafficDirectionFilter,
            "Connection" => workspace.TrafficConnectionFilter,
            "Endpoint" => workspace.TrafficEndpointFilter,
            _ => null
        };
        var isData = Equals(header.Tag, "Data");
        if (filter is null && !isData)
            return;
        e.Handled = true;
        // Wait until right-button release and header capture cleanup have finished.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded || !header.IsVisible || !ReferenceEquals(DataContext, workspace))
                return;
            TrafficFilterPopup.IsOpen = false;
            TrafficDataPopup.IsOpen = false;
            if (isData)
            {
                TrafficDataPopup.PlacementTarget = header;
                TrafficDataPopup.IsOpen = true;
                return;
            }
            TrafficFilterPopup.DataContext = filter;
            TrafficFilterPopup.PlacementTarget = header;
            TrafficFilterPopup.IsOpen = true;
        }));
    }
    private void TrafficFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            TrafficFilterPopup.IsOpen = false;
            TrafficDataPopup.IsOpen = false;
            e.Handled = true;
        }
    }
    private void TrafficFilterSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (TrafficFilterPopup.DataContext is MultiSelectFilter filter)
            filter.SelectAll(true);
    }
    private void TrafficFilterClearClick(object sender, RoutedEventArgs e)
    {
        if (TrafficFilterPopup.DataContext is MultiSelectFilter filter)
            filter.SelectAll(false);
    }
    private void OnTrafficChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => QueueFollowTail();
    private void QueueFollowTail()
    {
        if (_scrollPending || DataContext is not WorkspaceViewModel { FollowTail: true })
            return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (!IsVisible || DataContext is not WorkspaceViewModel { FollowTail: true } workspace || TrafficGrid.Items.Count == 0)
                return;
            TrafficGrid.ScrollIntoView(TrafficGrid.Items[workspace.TimeAscending ? TrafficGrid.Items.Count - 1 : 0]);
        });
    }
}
