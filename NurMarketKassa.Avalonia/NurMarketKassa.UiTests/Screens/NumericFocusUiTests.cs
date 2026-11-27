using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (владелец: «0 не убирается автоматически при создании товара в количестве!»): общий механизм числовых полей (NumericFocus) в настоящем окне
/// «Карточка товара» в Avalonia.Headless: фокус выделяет «0» целиком (клик мышью и Tab), цифра «5» даёт «5» (не «05»), «0,5» набирается как есть,
/// пустое поле при уходе снова «0», поле цены с числом выделяется целиком, обычные текстовые поля не затрагиваются.</summary>
[Collection("UiScenarios")]
public sealed class NumericFocusUiTests
{
    private static (ProductEditDialog Dlg, TextBox Qty, TextBox Price, TextBox Name) Open()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        NumericFocus.Register();
        var dlg = new ProductEditDialog();
        using var _ = UiShot.Render(dlg, new UiSize(1024, 768));
        UiShot.Settle();
        var boxes = dlg.GetVisualDescendants().OfType<TextBox>().ToList();
        var qty = boxes.First(t => t.Classes.Contains(NumericFocus.ZeroClass));
        var price = boxes.First(t => t.Classes.Contains(NumericFocus.FieldClass) && !t.Classes.Contains(NumericFocus.ZeroClass) && t.IsEffectivelyVisible && t.IsEnabled);
        var name = boxes.First(t => !t.Classes.Contains(NumericFocus.FieldClass) && t.IsVisible && t != qty);
        return (dlg, qty, price, name);
    }

    [AvaloniaFact]
    public void NewProduct_Quantity_Zero_IsSelected_OnTabFocus_And_DigitReplacesIt()
    {
        var (dlg, qty, _, _) = Open();
        try
        {
            qty.Text.Should().Be("0", "у нового товара количество по умолчанию 0");
            qty.Focus(Avalonia.Input.NavigationMethod.Tab);
            UiShot.Settle();
            Math.Abs(qty.SelectionEnd - qty.SelectionStart).Should().Be(1, "«0» выделен целиком");
            dlg.KeyTextInput("5");
            UiShot.Pump();
            qty.Text.Should().Be("5", "цифра заменяет ноль, а не «05»");
        }
        finally { dlg.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void NewProduct_Quantity_Zero_IsSelected_OnMouseClick_And_Fraction_Typed()
    {
        var (dlg, qty, _, _) = Open();
        try
        {
            UiDrive.Click(dlg, qty);
            UiShot.Settle();
            Math.Abs(qty.SelectionEnd - qty.SelectionStart).Should().Be(1, "после клика мышью «0» остаётся выделенным");
            foreach (var ch in "0,5")
                dlg.KeyTextInput(ch.ToString());
            UiShot.Pump();
            qty.Text.Should().Be("0,5", "дробное количество набирается как обычно");
        }
        finally { dlg.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void Quantity_Emptied_BecomesZero_OnLeave_And_PriceNumber_IsSelectedWhole()
    {
        var (dlg, qty, price, name) = Open();
        try
        {
            qty.Focus();
            UiShot.Settle();
            qty.Text = "";
            name.Focus(); // уходим из поля пустым
            UiShot.Settle();
            qty.Text.Should().Be("0", "пустое количество при уходе — «0», а не пустота");

            price.Text = "150";
            price.Focus();
            UiShot.Settle();
            Math.Abs(price.SelectionEnd - price.SelectionStart).Should().Be(3, "число в поле цены выделяется целиком");
            dlg.KeyTextInput("7");
            UiShot.Pump();
            price.Text.Should().Be("7");
        }
        finally { dlg.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void PlainTextField_IsNotSelected_OnFocus()
    {
        var (dlg, _, _, name) = Open();
        try
        {
            name.Text = "Молоко";
            name.Focus();
            UiShot.Settle();
            Math.Abs(name.SelectionEnd - name.SelectionStart).Should().Be(0, "обычное текстовое поле выделением не затрагивается");
        }
        finally { dlg.Close(); UiShot.Pump(); }
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0,00", true)]
    [InlineData("0.0", true)]
    [InlineData(" 00 ", true)]
    [InlineData("0,5", false)]
    [InlineData("10", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsZeroText_Matches_OnlyZeroValues(string? text, bool expected) =>
        NumericFocus.IsZeroText(text).Should().Be(expected);
}
