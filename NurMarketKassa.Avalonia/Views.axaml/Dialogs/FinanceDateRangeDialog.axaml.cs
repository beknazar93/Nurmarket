using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class FinanceDateRangeDialog : Window
{
    public DateTime FromDate { get; private set; } = DateTime.Today;
    public DateTime ToDate { get; private set; } = DateTime.Today;
    public bool? DialogResult { get; set; }

    public FinanceDateRangeDialog()
    {
        InitializeComponent();
        FromDatePicker.SelectedDate = DateTime.Today;
        ToDatePicker.SelectedDate = DateTime.Today;
        RangeCalendar.SelectedDate = DateTime.Today;
    }

    private void Quick_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        var today = DateTime.Today;
        switch (tag)
        {
            case "Week":
                FromDatePicker.SelectedDate = today.AddDays(-6);
                ToDatePicker.SelectedDate = today;
                break;
            case "Month":
                FromDatePicker.SelectedDate = new DateTime(today.Year, today.Month, 1);
                ToDatePicker.SelectedDate = today;
                break;
            default:
                FromDatePicker.SelectedDate = today;
                ToDatePicker.SelectedDate = today;
                break;
        }

        RangeCalendar.SelectedDate = ToDatePicker.SelectedDate;
        ClearError();
    }

    private void RangeCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RangeCalendar.SelectedDate is not { } selected)
            return;

        // Первое нажатие задаёт «От», второе — «До» (если позже «От»).
        var from = FromDatePicker.SelectedDate?.Date;
        var to = ToDatePicker.SelectedDate?.Date;
        if (from is null || (from.HasValue && to.HasValue && from == to))
        {
            FromDatePicker.SelectedDate = selected.Date;
            ToDatePicker.SelectedDate = selected.Date;
        }
        else if (selected.Date < from.Value)
        {
            FromDatePicker.SelectedDate = selected.Date;
        }
        else
        {
            ToDatePicker.SelectedDate = selected.Date;
        }

        ClearError();
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (FromDatePicker.SelectedDate is null || ToDatePicker.SelectedDate is null)
        {
            ShowError("Выберите обе даты.");
            return;
        }

        var from = FromDatePicker.SelectedDate.Value.Date;
        var to = ToDatePicker.SelectedDate.Value.Date;
        if (from > to)
        {
            ShowError("Дата «От» не может быть позже даты «До».");
            return;
        }

        FromDate = from;
        ToDate = to;
        DialogResult = true;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorText.IsVisible = false;
    }
}
