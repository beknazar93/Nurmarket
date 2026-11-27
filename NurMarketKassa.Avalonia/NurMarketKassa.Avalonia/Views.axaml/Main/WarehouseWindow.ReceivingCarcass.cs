using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (просьба владельца для мясных магазинов: «при покупке коровы вес сразу убавлять, для продажи остаётся другой вес»): в карточке приёмки у весового товара (кг) — кнопка «Выход мяса»
/// и под строкой цен компактный блок «Куплено · Цена за кг · Сумма закупки · Выход % · В продажу» с итоговой строкой «Куплено 300 кг → в продаже 225 кг (отходы 75 кг · 25 %) · себестоимость 533,33 сом/кг».
/// Расчёт — CarcassYield (Infrastructure), состояние — ReceivingLineVm.Carcass.cs: «Кол-во» строки = вес в продажу, «Закупка» = себестоимость кг в продаже, сумма закупки не меняется.
/// Кнопка и блок видны, только когда модуль «Выход мяса при приходе» включён в Маркетплейсе (CarcassYieldFeature) и строка весовая, не в режиме упаковок. Здесь нет ни одного расчёта — только поля и подписи.
/// Поля — WrapPanel (на узком окне переносятся на следующую строку, не налезают); поля и кнопка ≥ 44 px; подписи на 5 языках.</summary>
public partial class WarehouseWindow
{
    /// <summary>Кнопка «Выход мяса» (в ряду кнопок карточки) и блок ввода (под рядом полей). Видимость решает сама строка: <see cref="ReceivingLineVm.CarcassAllowed"/> и <see cref="ReceivingLineVm.CarcassOn"/>.</summary>
    private (Button Toggle, Control Block) BuildCarcassParts(BulkScanRow row)
    {
        var line = row.Line;

        var toggle = BulkButton("", false);
        toggle.MinWidth = 0;
        toggle.Padding = new Thickness(12, 2);
        toggle.Tag = "rcv-carcass";
        AutomationProperties.SetAutomationId(toggle, "carcass-toggle");
        ToolTip.SetTip(toggle, TrB("Купили тушу или мясо с костью: впишите, сколько куплено и какой выход — в склад пойдёт вес для продажи, а цена килограмма пересчитается",
            "Союш же сөөгү менен эт сатып алдыңыз: канча сатып алынганын жана чыгымды жазыңыз — кампага сатуучу салмак келет, килограммдын баасы кайра эсептелет",
            "Bought a carcass or meat with bones: enter how much and the yield — the stock gets the weight for sale and the price per kilogram is recalculated",
            "Karkas veya kemikli et aldınız: ne kadar aldığınızı ve verimi yazın — depoya satış ağırlığı girer, kilogram fiyatı yeniden hesaplanır",
            "Tusha yoki suyakli go'sht sotib oldingiz: qancha olganingizni va chiqishni yozing — omborga sotuv vazni kiradi, kilogramm narxi qayta hisoblanadi"));

        // ---- блок
        var grossCap = Caption();
        var priceCap = Caption();
        var totalCap = Caption();
        var yieldCap = Caption();
        var netCap = Caption();
        var gross = CarcassBox(row, "Line.CarcassGrossText", 120, "carcass-gross");
        var price = CarcassBox(row, "Line.CarcassPriceText", 120, "carcass-price");
        var total = CarcassBox(row, "Line.CarcassTotalText", 140, "carcass-total");
        var yieldBox = CarcassBox(row, "Line.CarcassYieldText", 100, "carcass-yield");
        var net = CarcassBox(row, "Line.CarcassNetText", 120, "carcass-net");
        var boxes = new[] { gross, price, total, yieldBox, net };
        foreach (var box in boxes)
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
                    return;
                e.Handled = true;
                line.CarcassCommitText();
                var at = Array.IndexOf(boxes, box);
                // Enter: к следующему полю блока, из последнего — в поле скана
                Dispatcher.UIThread.Post(() =>
                {
                    if (at + 1 < boxes.Length)
                    {
                        boxes[at + 1].Focus();
                        boxes[at + 1].SelectAll();
                    }
                    else
                    {
                        ReceivingScanBox.Focus();
                        ReceivingScanBox.SelectAll();
                    }
                }, DispatcherPriority.Background);
            };
        }

        var fields = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (cap, box) in new[] { (grossCap, gross), (priceCap, price), (totalCap, total), (yieldCap, yieldBox), (netCap, net) })
        {
            var host = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
            host.Children.Add(cap);
            host.Children.Add(box);
            fields.Children.Add(host);
        }

        var title = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
        title.Text = TrB("Выход мяса: сколько куплено и сколько пойдёт в продажу", "Эттин чыгымы: канча сатып алынды жана канчасы сатууга кетет",
            "Meat yield: how much was bought and how much goes on sale", "Et verimi: ne kadar alındı ve ne kadarı satışa gider", "Go'sht chiqishi: qancha sotib olindi va qanchasi sotuvga ketadi");
        var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        result.Bind(TextBlock.ForegroundProperty, result.GetResourceObservable("BrushText"));
        result.Bind(TextBlock.TextProperty, new Binding("Line.CarcassResultText"));
        AutomationProperties.SetAutomationId(result, "carcass-result");
        var pay = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        pay.Bind(TextBlock.ForegroundProperty, pay.GetResourceObservable("BrushText"));
        pay.Bind(TextBlock.TextProperty, new Binding("Line.CarcassPayText"));
        pay.Bind(Visual.IsVisibleProperty, new Binding("Line.CarcassPayText") { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });
        AutomationProperties.SetAutomationId(pay, "carcass-pay");
        var loss = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        loss.Bind(TextBlock.ForegroundProperty, loss.GetResourceObservable("BrushDanger"));
        loss.Bind(TextBlock.TextProperty, new Binding("Line.CarcassLossText"));
        loss.Bind(Visual.IsVisibleProperty, new Binding("Line.CarcassLossText") { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });
        var problem = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable("BrushDanger"));
        problem.Bind(TextBlock.TextProperty, new Binding("Line.CarcassProblemText"));
        problem.Bind(Visual.IsVisibleProperty, new Binding("Line.CarcassHasProblem"));
        AutomationProperties.SetAutomationId(problem, "carcass-problem");

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(title);
        stack.Children.Add(fields);
        stack.Children.Add(result);
        stack.Children.Add(pay);
        stack.Children.Add(loss);
        stack.Children.Add(problem);
        var block = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(44, 4, 52, 2),
            BorderThickness = new Thickness(1),
            Child = stack,
            IsVisible = false,
        };
        block.Bind(Border.BackgroundProperty, block.GetResourceObservable("BrushPanel"));
        block.Bind(Border.BorderBrushProperty, block.GetResourceObservable("BrushBorder"));
        AutomationProperties.SetAutomationId(block, "carcass-block");

        // ---- видимость и подписи по состоянию строки (единица, режим упаковок, включён ли блок)
        void Apply()
        {
            var allowed = line.CarcassAllowed;
            var on = allowed && line.CarcassOn;
            toggle.IsVisible = allowed;
            toggle.Content = on
                ? TrB("Выход мяса ▴", "Эттин чыгымы ▴", "Meat yield ▴", "Et verimi ▴", "Go'sht chiqishi ▴")
                : TrB("Выход мяса ▾", "Эттин чыгымы ▾", "Meat yield ▾", "Et verimi ▾", "Go'sht chiqishi ▾");
            block.IsVisible = on;
            var u = string.IsNullOrWhiteSpace(line.Unit) ? "кг" : UnitCatalog.Display(line.Unit);
            grossCap.Text = TrB($"Куплено, {u} *", $"Сатып алынды, {u} *", $"Bought, {u} *", $"Alınan, {u} *", $"Sotib olindi, {u} *");
            priceCap.Text = TrB($"Цена за {u}, сом", $"1 {u} баасы, сом", $"Price per {u}, som", $"{u} fiyatı, som", $"1 {u} narxi, so'm");
            totalCap.Text = TrB("Сумма закупки, сом", "Сатып алуу суммасы, сом", "Purchase total, som", "Alış tutarı, som", "Xarid summasi, so'm");
            yieldCap.Text = TrB("Выход, %", "Чыгым, %", "Yield, %", "Verim, %", "Chiqish, %");
            netCap.Text = TrB($"В продажу, {u}", $"Сатууга, {u}", $"For sale, {u}", $"Satışa, {u}", $"Sotuvga, {u}");
        }
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName is nameof(ReceivingLineVm.CarcassOn) or nameof(ReceivingLineVm.Unit) or nameof(ReceivingLineVm.PackMode) or nameof(ReceivingLineVm.IsWeight) or null)
                Apply();
        };
        Action moduleChanged = () => Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background); // модуль включили или выключили в Маркетплейсе — кнопка появляется и пропадает без пересборки карточки
        toggle.AttachedToVisualTree += (_, _) =>
        {
            line.PropertyChanged += handler;
            TariffGate.PacksChanged += moduleChanged;
            CompanyModules.Changed += moduleChanged;
            Apply();
        };
        toggle.DetachedFromVisualTree += (_, _) =>
        {
            line.PropertyChanged -= handler;
            TariffGate.PacksChanged -= moduleChanged;
            CompanyModules.Changed -= moduleChanged;
        };
        toggle.Click += (_, _) =>
        {
            line.SetCarcass(!line.CarcassOn);
            Apply();
            if (line.CarcassOn)
                Dispatcher.UIThread.Post(() =>
                {
                    var first = string.IsNullOrEmpty(line.CarcassGrossText) ? gross : yieldBox; // сколько куплено ещё не вписано — начинаем с него, иначе — с выхода
                    first.Focus();
                    first.SelectAll();
                }, DispatcherPriority.Background);
            RefreshReceivingSummary();
        };
        Apply();
        return (toggle, block);

        static TextBlock Caption()
        {
            var cap = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 4) }; // 4 px до поля: проверка «подпись вплотную» требует зазор больше 2
            cap.Bind(TextBlock.ForegroundProperty, cap.GetResourceObservable("BrushTextSoft"));
            return cap;
        }
    }

    private TextBox CarcassBox(BulkScanRow row, string path, double width, string automationId)
    {
        var box = BulkInput("", width);
        box.Bind(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, Converter = CommaDecimal });
        AutomationProperties.SetAutomationId(box, automationId);
        // Тега у поля нет намеренно: цепочка Enter / Esc карточки (order в InitReceivingCards) относится к основным ячейкам; здесь Enter ведёт по полям блока, Esc ничего не откатывает.
        box.GotFocus += (_, _) =>
        {
            box.SelectAll();
            Dispatcher.UIThread.Post(() =>
            {
                if (box.IsFocused)
                    box.SelectAll(); // нажатие мышью снимает выделение после получения фокуса — выделяем число ещё раз
            }, DispatcherPriority.Input);
        };
        box.LostFocus += (_, _) =>
        {
            row.Line.CarcassCommitText();
            RefreshReceivingSummary();
        };
        return box;
    }
}
