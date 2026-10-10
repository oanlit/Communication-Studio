using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Communication.App.ViewModels;
using Communication.App.Views;
using Communication.Core;

internal static class TrafficMonitorTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ics-traffic-filter-{Guid.NewGuid():N}.json");
        var workspace = new WorkspaceViewModel(path) { FollowTail = false };
        var view = new ConnectionsView { DataContext = workspace };
        var window = new Window { Content = view, Width = 1260, Height = 850, ShowInTaskbar = false };
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        TrafficRow Row(long sequence, Guid id, string name, string endpoint, TrafficDirection direction, DateTimeOffset timestamp) =>
            new(sequence, id, name, new(timestamp, direction, PayloadKind.StreamChunk, [0x41], endpoint), true);
        var first = Row(1, a, "保持寄存器", "peer A", TrafficDirection.Tx, now);
        var older = Row(2, b, "Device B", "peer B", TrafficDirection.Rx, now.AddSeconds(-1));
        var tied = Row(3, a, "保持寄存器", "peer C", TrafficDirection.Rx, now);
        try
        {
            workspace.Traffic.Add(first);
            workspace.Traffic.Add(older);
            workspace.Traffic.Add(tied);
            window.Show();
            await Task.Delay(150);
            await Idle();
            view.UpdateLayout();
            var grid = (DataGrid)view.FindName("TrafficGrid");
            Check(workspace.TimeAscending && workspace.TrafficView.Cast<TrafficRow>().SequenceEqual(new[] { older, first, tied }), "Monitor defaults to timestamp order with stable sequence ties");
            var headers = Visuals<DataGridColumnHeader>(grid).Where(header => header.Column is not null).ToArray();
            var time = headers.Single(header => header.Column == grid.Columns[0]);
            var arrows = Visuals<System.Windows.Shapes.Path>(time).ToArray();
            Check(arrows.Length == 2 && arrows[0].Opacity == 1 && arrows[1].Opacity < 1, "Monitor time header shows ascending and descending indicators");
            workspace.SelectedTraffic = first;
            typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(time, null);
            await Idle();
            Check(!workspace.TimeAscending && workspace.TrafficView.Cast<TrafficRow>().SequenceEqual(new[] { tied, first, older }), "Time header click changes sorting rather than applying default column sorting");
            Check(arrows[1].Opacity == 1 && arrows[0].Opacity < 1 && ReferenceEquals(workspace.SelectedTraffic, first), "Sort indicator updates and packet selection remains");

            foreach (var tag in new[] { "Direction", "Connection", "Endpoint" })
            {
                var header = headers.Single(header => Equals(header.Tag, tag));
                var popup = (Popup)view.FindName("TrafficFilterPopup");
                header.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
                Check(!popup.IsOpen, "Filter does not open while right button is held");
                header.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
                Check(!popup.IsOpen, "Filter is deferred until right release processing finishes");
                await Idle();
                await Idle();
                Check(popup.IsOpen && popup.DataContext is MultiSelectFilter && ReferenceEquals(popup.PlacementTarget, header), "Right release opens a persistent multi-select filter at the correct header");
                Check(popup.Placement == PlacementMode.Top, "Traffic header filters open above the header to keep results visible");
                var filter = (MultiSelectFilter)popup.DataContext;
                Check(filter.Options.Count == (tag == "Endpoint" ? 3 : 2), "Filter lists retained traffic values");
                Check(((Border)popup.Child).Background == window.FindResource("Raised"), "Traffic filter uses the shared theme surface");
                var currentEndpoint = Visuals<CheckBox>(popup.Child).First();
                Check(currentEndpoint.Visibility == (tag == "Connection" ? Visibility.Visible : Visibility.Collapsed), "Current-endpoint filter appears only in the endpoint header menu");
                if (tag == "Connection")
                {
                    currentEndpoint.IsChecked = true;
                    await Idle();
                    Check(workspace.OnlySelected && grid.Items.Count == 0, "Current-endpoint menu option updates the live filter");
                    Check(currentEndpoint.IsEnabled && Visuals<Button>(popup.Child).All(button => !button.IsEnabled) && Visuals<CheckBox>(popup.Child).Where(option => option.Visibility == Visibility.Visible).Skip(1).All(option => !option.IsEnabled), "Current-endpoint mode disables manual selection controls while keeping its toggle usable");
                    filter.Options.Single(option => option.Value == "保持寄存器").IsSelected = false;
                    workspace.Selected = new ConnectionViewModel(new ConnectionProfile { Id = a, Name = "保持寄存器" });
                    Check(grid.Items.Count == 2, "Current-endpoint mode ignores the saved manual endpoint exclusions");
                    currentEndpoint.IsChecked = false;
                    await Idle();
                    Check(grid.Items.Count == 1 && Visuals<Button>(popup.Child).All(button => button.IsEnabled), "Leaving current-endpoint mode restores manual selections and enables controls");
                    filter.SelectAll(true);
                    workspace.Selected = null;
                }
                var currentPeer = (CheckBox)view.FindName("CurrentPeerFilter");
                Check(currentPeer.Visibility == (tag == "Endpoint" ? Visibility.Visible : Visibility.Collapsed), "Current-peer option appears only in the peer header menu");
                if (tag == "Endpoint")
                {
                    workspace.SelectedTraffic = first;
                    currentPeer.IsChecked = true;
                    await Idle();
                    Check(currentPeer.IsEnabled && Visuals<Button>(popup.Child).All(button => !button.IsEnabled) && workspace.TrafficView.Cast<TrafficRow>().Single() == first, "Current-peer mode filters by the selected record and disables manual controls");
                    filter.Options.Single(option => option.Value == "peer A").IsSelected = false;
                    Check(workspace.TrafficView.Cast<TrafficRow>().Single() == first, "Current-peer mode ignores saved manual peer exclusions");
                    workspace.SelectedTraffic = null;
                    Check(workspace.CurrentPeerAddress == "peer A" && grid.Items.Count == 1, "Current-peer address survives selection clearing during refresh");
                    workspace.SelectedTraffic = tied;
                    Check(workspace.TrafficView.Cast<TrafficRow>().Single() == tied, "Choosing another record updates the current peer");
                    currentPeer.IsChecked = false;
                    await Idle();
                    Check(grid.Items.Count == 2 && Visuals<Button>(popup.Child).All(button => button.IsEnabled), "Leaving current-peer mode restores saved manual peer selections");
                    filter.SelectAll(true);
                    workspace.SelectedTraffic = first;
                }
                popup.Child.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(!popup.IsOpen, "Escape closes the traffic filter");
            }

            var dataHeader = headers.Single(header => Equals(header.Tag, "Data"));
            dataHeader.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            await Idle();
            await Idle();
            var dataPopup = (Popup)view.FindName("TrafficDataPopup");
            Check(dataPopup.IsOpen && dataPopup.Placement == PlacementMode.Top && ReferenceEquals(dataPopup.PlacementTarget, dataHeader), "Data header opens its search control above the table");
            var search = (TextBox)view.FindName("TrafficSearchInput");
            search.Text = "peer A";
            await Idle();
            Check(workspace.Filter == "peer A" && workspace.TrafficView.Cast<TrafficRow>().Single() == first, "Search in the data menu filters the visible table immediately");
            workspace.TrafficDirectionFilter.Options.Single(option => option.Value == "TX").IsSelected = false;
            Check(grid.Items.Count == 0, "I/O header filter combines with the data-menu search");
            workspace.TrafficDirectionFilter.SelectAll(true);
            search.Text = "";
            await Idle();
            Check(grid.Items.Count == 3, "Clearing data-menu filters restores visible records");
            dataPopup.Child.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!dataPopup.IsOpen, "Escape closes the data filter menu");

            workspace.TrafficDirectionFilter.Options.Single(option => option.Value == "RX").IsSelected = false;
            Check(workspace.TrafficView.Cast<TrafficRow>().Single() == first, "I/O supports multi-select filtering");
            workspace.TrafficDirectionFilter.SelectAll(true);
            workspace.TrafficConnectionFilter.Options.Single(option => option.Value == "Device B").IsSelected = false;
            workspace.TrafficEndpointFilter.Options.Single(option => option.Value == "peer A").IsSelected = false;
            Check(workspace.TrafficView.Cast<TrafficRow>().Single() == tied, "Connection and endpoint filters combine");
            workspace.Filter = "peer A";
            Check(grid.Items.Count == 0, "Text search combines with column filters");
            workspace.Filter = "";
            Check(((SolidColorBrush)headers.Single(header => Equals(header.Tag, "Connection")).Foreground).Color == ((SolidColorBrush)window.FindResource("Accent")).Color, "Active connection filter highlights its header");
            workspace.Traffic.Add(Row(4, b, "New device", "peer new", TrafficDirection.Rx, now.AddSeconds(1)));
            for (var i = 0; i < 150 && !workspace.TrafficConnectionFilter.Options.Any(option => option.Value == "New device"); i++)
                await Task.Delay(10);
            Check(!workspace.TrafficConnectionFilter.Options.Single(option => option.Value == "New device").IsSelected && grid.Items.Count == 1, "New connections cannot bypass an active filter");
            workspace.TrafficEndpointFilter.SelectAll(false);
            Check(grid.Items.Count == 0, "Clear selection hides all endpoint values");
            workspace.ClearCommand.Execute(null);
            Check(grid.Items.Count == 0 && workspace.TrafficConnectionFilter.Options.Count == 0 && !workspace.TrafficConnectionFilter.IsFiltered && !workspace.TrafficEndpointFilter.IsFiltered, "Clearing traffic resets column filters and options");

            for (var i = 0; i < 80; i++)
                workspace.Traffic.Add(Row(10 + i, a, "保持寄存器", "peer A", TrafficDirection.Tx, now.AddSeconds(i + 2)));
            await Task.Delay(150);
            await Idle();
            view.UpdateLayout();
            var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid);
            scroll.ScrollToBottom();
            await Idle();
            Check(scroll.VerticalOffset > 0, "Monitor regression fixture has scrollable records");
            workspace.FollowTail = true;
            workspace.Traffic.Add(Row(100, a, "保持寄存器", "peer A", TrafficDirection.Tx, now.AddSeconds(100)));
            await Idle();
            await Idle();
            Check(scroll.VerticalOffset == 0, "Descending follow navigates to newest record at the top");
            typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(time, null);
            await Idle();
            await Idle();
            Check(workspace.TimeAscending && scroll.VerticalOffset > 0 && ((TrafficRow)grid.Items[^1]).Sequence == 100, "Ascending follow navigates to newest record at the bottom");
        }
        finally
        {
            window.Close();
            await workspace.ShutdownAsync();
            File.Delete(path);
        }

    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Visuals<T>(child))
                yield return nested;
        }
    }
}
