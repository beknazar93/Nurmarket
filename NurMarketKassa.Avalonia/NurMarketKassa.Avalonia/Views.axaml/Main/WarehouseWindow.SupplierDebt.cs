using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-20, этап П3 (поставщики): в «Приёмке» при «В долг» — поле «Срок оплаты, дней» (по умолчанию 30). После проведения
/// касса записывает долг поставщику (WarehouseViewModel → SupplierDebtRecorder) с датой оплаты = день прихода + срок. Поле — строкой под
/// поставщиком и способом оплаты (в одну строку на 1024 не помещалось), рядом «оплатить до …»; видно только с поставщиком и при «В долг».
/// Сам приход не меняется.</summary>
public partial class WarehouseWindow
{
    private TextBox? _receivingDebtDays;

    private void InitSupplierDebtTerm()
    {
        // Строка «Поставщик [кнопка] [Оплачено | В долг]» — родитель сегмента оплаты; поле — следующей строкой. Повторная загрузка окна — поле уже есть.
        if (_receivingDebtDays is not null)
            return;
        if (ReceivingDebtRadio.Parent?.Parent is not Border segment || segment.Parent is not StackPanel row || row.Parent is not StackPanel column)
            return;
        var label = new TextBlock
        {
            Text = Tr.T("Срок оплаты, дней", "Төлөө мөөнөтү, күн", "Pay within, days", "Ödeme vadesi, gün", "To'lov muddati, kun"),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
        };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _receivingDebtDays = new TextBox
        {
            Text = _viewModel.ReceivingDebtDays.ToString(CultureInfo.InvariantCulture), Width = 72, MaxLength = 4,
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(_receivingDebtDays, label.Text);
        var due = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        due.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        void ShowDue()
        {
            var ok = int.TryParse((_receivingDebtDays.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650;
            if (ok)
                _viewModel.ReceivingDebtDays = days;
            var d = DateTime.Today.AddDays(_viewModel.ReceivingDebtDays);
            due.Text = ok
                ? Tr.T($"оплатить до {d:dd.MM.yyyy}", $"{d:dd.MM.yyyy} чейин төлөө", $"pay by {d:dd.MM.yyyy}", $"son ödeme {d:dd.MM.yyyy}", $"{d:dd.MM.yyyy} gacha to'lash")
                : Tr.T("от 1 до 3650 дней", "1ден 3650 күнгө чейин", "from 1 to 3650 days", "1–3650 gün", "1 dan 3650 kungacha");
        }
        _receivingDebtDays.TextChanged += (_, _) => ShowDue();
        ShowDue();
        var term = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { label, _receivingDebtDays, due } };
        column.Children.Insert(column.Children.IndexOf(row) + 1, term);

        void Update() => term.IsVisible = _viewModel.HasReceivingSupplier && ReceivingDebtRadio.IsChecked == true;
        ReceivingDebtRadio.IsCheckedChanged += (_, _) => Update();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(_viewModel.HasReceivingSupplier) or nameof(_viewModel.ReceivingSupplier))
                Update();
        };
        Update();
    }
}
