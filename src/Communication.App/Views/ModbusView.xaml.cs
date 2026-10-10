using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Communication.App.ViewModels;
using Communication.App.Presentation;
namespace Communication.App.Views;

public partial class ModbusView : UserControl
{
    private void PacketPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.ScrollableHeight <= 0 || e.Delta == 0)
            return;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0)
            return;
        var step = lines < 0 ? scroll.ViewportHeight : lines * 16d;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120d * step);
        e.Handled = true;
    }
    private void LogGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column.SortMemberPath == nameof(ModbusLogRow.Timestamp) && DataContext is ModbusViewModel vm)
            vm.ToggleLogTimeSort();
    }
    private void LogFilterHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridColumnHeader header || DataContext is not ModbusViewModel vm)
            return;
        var filter = (header.Tag as string) switch
        {
            "Level" => vm.LevelFilter,
            "Type" => vm.TypeFilter,
            "Role" => vm.RoleFilter,
            "Endpoint" => vm.EndpointFilter,
            _ => null
        };
        if (filter is null)
            return;
        e.Handled = true;
        // Open after the release event and the header's capture cleanup finish.
        // Otherwise a StaysOpen=false popup sees that same release as an outside click.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded || !header.IsVisible || !ReferenceEquals(DataContext, vm))
                return;
            LogFilterPopup.IsOpen = false;
            LogFilterPopup.DataContext = filter;
            LogFilterPopup.PlacementTarget = header;
            LogFilterPopup.IsOpen = true;
        }));
    }
    private void LogFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            LogFilterPopup.IsOpen = false;
            e.Handled = true;
        }
    }
    private void LogFilterSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (LogFilterPopup.DataContext is MultiSelectFilter filter)
            filter.SelectAll(true);
    }
    private void LogFilterClearClick(object sender, RoutedEventArgs e)
    {
        if (LogFilterPopup.DataContext is MultiSelectFilter filter)
            filter.SelectAll(false);
    }
    private ModbusViewModel? _subscribedViewModel;
    public ModbusView()
    {
        InitializeComponent();
        Loaded += (_, _) => SubscribeSerialErrors(DataContext as ModbusViewModel);
        Unloaded += (_, _) =>
{
    LogFilterPopup.IsOpen = false;
    SubscribeSerialErrors(null);
}
;
        DataContextChanged += (_, _) =>
{
    LogFilterPopup.IsOpen = false;
    if (IsLoaded)
        SubscribeSerialErrors(DataContext as ModbusViewModel);
}
;
    }
    private void SubscribeSerialErrors(ModbusViewModel? viewModel)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.SerialOpenFailed -= ShowSerialOpenFailure;
        _subscribedViewModel = viewModel;
        if (viewModel is not null)
            viewModel.SerialOpenFailed += ShowSerialOpenFailure;
    }
    private void ShowSerialOpenFailure(string message)
    {
        var owner = Window.GetWindow(this);
        if (owner is not null)
            MessageBox.Show(owner, Translation.Text(message), Translation.Text("打开串口失败"), MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(Translation.Text(message), Translation.Text("打开串口失败"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private (RegisterRow Row, Point Position)? _editClick;
    private void RegisterGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridCell)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is not DataGridCell { IsReadOnly: false, DataContext: RegisterRow { IsBit: false } row, Column: DataGridTemplateColumn column } cell)
            return;
        if (cell.IsEditing)
        {
            if (FindEditor(cell) is { } editor)
            {
                PlaceCaret(editor, e.GetPosition(editor));
                e.Handled = true;
            }
            return;
        }
        _editClick = (row, e.GetPosition(RegisterGrid));
        RegisterGrid.CurrentCell = new DataGridCellInfo(row, column);
        RegisterGrid.SelectedItem = row;
        if (RegisterGrid.BeginEdit())
            e.Handled = true;
        else
            _editClick = null;
    }
    private void RegisterEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not RegisterRow row)
            return;
        var click = _editClick;
        _editClick = null;
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!editor.IsVisible || !ReferenceEquals(RegisterGrid.CurrentCell.Item, row))
                return;
            PlaceCaret(editor, click is { } location && ReferenceEquals(location.Row, row) ? RegisterGrid.TranslatePoint(location.Position, editor) : null);
        }));
    }
    private void RegisterEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor && editor.DataContext is RegisterRow row)
        {
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            row.ValidateEdit();
        }
    }
    private void RegisterCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.Item is RegisterRow row)
            row.ValidateEdit();
    }
    private static TextBox? FindEditor(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox editor)
                return editor;
            if (FindEditor(child) is { } found)
                return found;
        }
        return null;
    }
    private static void PlaceCaret(TextBox editor, Point? position)
    {
        editor.Focus();
        Keyboard.Focus(editor);
        var index = position is { } point ? editor.GetCharacterIndexFromPoint(point, true) : editor.Text.Length;
        if (index < 0)
            index = editor.Text.Length;
        if (position is { } hit && index < editor.Text.Length)
        {
            var leading = editor.GetRectFromCharacterIndex(index, false);
            var trailing = editor.GetRectFromCharacterIndex(index, true);
            if (!leading.IsEmpty && !trailing.IsEmpty && hit.X > (leading.X + trailing.X) / 2)
                index++;
        }
        editor.Select(index, 0);
    }
    private void RevertEditsClick(object sender, RoutedEventArgs e)
    {
        RegisterGrid.CancelEdit(DataGridEditingUnit.Cell);
        RegisterGrid.CancelEdit(DataGridEditingUnit.Row);
    }
    private void SectionContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TabItem { DataContext: RegisterSectionViewModel section, ContextMenu: { } menu })
            return;
        menu.Items.Clear();
        var edit = new MenuItem { Header = "编辑分区…", DataContext = section };
        var close = new MenuItem { Header = "关闭标签", DataContext = section };
        Translation.Bind(edit, MenuItem.HeaderProperty, "编辑分区…");
        Translation.Bind(close, MenuItem.HeaderProperty, "关闭标签");
        edit.Click += EditSectionClick;
        close.Click += CloseSectionClick;
        menu.Items.Add(edit);
        menu.Items.Add(close);
    }
    private void NewSectionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModbusViewModel vm)
            return;
        if (vm.Sections.Count >= 32)
        {
            MessageBox.Show(Window.GetWindow(this), Translation.Text("最多创建 32 个寄存器标签。"), "Communication Studio");
            return;
        }
        var dialog = new RegisterSectionDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } configuration)
            vm.AddSection(configuration);
    }
    private void EditSectionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ModbusViewModel vm || (sender as FrameworkElement)?.DataContext is not RegisterSectionViewModel section)
            return;
        var dialog = new RegisterSectionDialog(section.Configuration) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } configuration)
            vm.EditSection(section, configuration);
    }
    private void CloseSectionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModbusViewModel vm && (sender as FrameworkElement)?.DataContext is RegisterSectionViewModel section)
            vm.RemoveSection(section);
    }
}
