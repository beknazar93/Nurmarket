using Avalonia.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Fiscal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-14 (П0-1, ответ Умара 13.11 вечер): в карточке товара — «Ставка НДС» (vat_rate товара: число или «ставка компании» = null). Поле видно, только когда у компании включена фискализация
// (режим не «выключено», FiscalSettings), список ставок — из настроек «Налоги и ККМ» (vat_rates). Блок собирается из кода и встаёт под «Серийными номерами»; в запрос vat_rate попадает
// только если владелец менял значение (у нового товара — если выбрана ставка). Без включённой фискализации и на старом сервере ничего не меняется — поле в запрос не попадает.
public partial class ProductEditDialog
{
    private StackPanel? _vatBlock;
    private ComboBox? _vatCombo;
    private decimal? _vatOrig;
    private bool _vatChanged;
    private bool _vatFilling;

    /// <summary>Пункт списка ставок: Rate = null — «ставка компании».</summary>
    private sealed record VatChoice(decimal? Rate, string Text)
    {
        public override string ToString() => Text;
    }

    private void InitVatBlock(NurMarketKassa.Models.Pos.CatalogProductTileVm? existing)
    {
        if (!FiscalFormRules.ProductVatVisible || SerialsBlock.Parent is not Panel host)
            return;
        var label = new TextBlock { Text = Tr.T("Ставка НДС", "ЧКС ставкасы", "VAT rate", "KDV oranı", "QQS stavkasi") };
        label.Classes.Add("FieldLabel");
        _vatCombo = new ComboBox { Name = "ProductVatCombo", MinHeight = 44, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        _vatCombo.Classes.Add("TouchComboBox");
        Avalonia.Automation.AutomationProperties.SetName(_vatCombo, label.Text);
        RebuildVatItems(null);
        _vatCombo.SelectionChanged += (_, _) => { if (!_vatFilling) _vatChanged = true; };
        var hint = new TextBlock
        {
            Text = Tr.T("«Ставка компании» — по настройке «Налоги и ККМ». Для товаров без НДС выберите 0 %.", "«Компаниянын ставкасы» — «Салыктар жана ККМ» жөндөөсү боюнча. НДСсиз товарлар үчүн 0 % тандаңыз.",
                "“Company rate” follows the “Taxes and cash register” setting. For goods without VAT choose 0 %.", "«Şirket oranı» «Vergiler ve ÖKC» ayarına göredir. KDV'siz ürünler için %0 seçin.",
                "«Kompaniya stavkasi» — «Soliqlar va KKM» sozlamasi bo'yicha. QQSsiz tovarlar uchun 0 % tanlang."),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        _vatBlock = new StackPanel { Name = "ProductVatBlock", Spacing = 8, Width = 260, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        _vatBlock.Children.Add(label);
        _vatBlock.Children.Add(_vatCombo);
        _vatBlock.Children.Add(hint);
        host.Children.Insert(host.Children.IndexOf(SerialsBlock) + 1, _vatBlock);
        _vatOrig = null;
        _vatChanged = false;
    }

    /// <summary>Список: «Ставка компании» + ставки компании; если у товара ставка не из списка — она добавляется, чтобы показать как есть.</summary>
    private void RebuildVatItems(decimal? extra)
    {
        if (_vatCombo is null)
            return;
        var choices = new List<VatChoice>
        {
            new(null, Tr.T("Ставка компании", "Компаниянын ставкасы", "Company rate", "Şirket oranı", "Kompaniya stavkasi")),
        };
        var rates = FiscalSettings.VatRates.ToList();
        if (extra is { } e && !rates.Contains(e))
            rates.Add(e);
        choices.AddRange(rates.OrderBy(r => r).Select(r => new VatChoice(r, FiscalFormRules.FormatPercent(r) + " %")));
        _vatFilling = true;
        try
        {
            _vatCombo.ItemsSource = choices;
            _vatCombo.SelectedIndex = 0;
        }
        finally
        {
            _vatFilling = false;
        }
    }

    private void SelectVat(decimal? rate)
    {
        if (_vatCombo is null)
            return;
        _vatFilling = true;
        try
        {
            if (_vatCombo.ItemsSource is IEnumerable<VatChoice> items && items.All(c => c.Rate != rate))
                RebuildVatItems(rate);
            if (_vatCombo.ItemsSource is IEnumerable<VatChoice> list)
                _vatCombo.SelectedItem = list.FirstOrDefault(c => c.Rate == rate) ?? list.First();
        }
        finally
        {
            _vatFilling = false;
        }
    }

    /// <summary>vat_rate карточки с сервера: подставляется, пока владелец сам ставку не менял. У товара без vat_rate (старый сервер или «ставка компании») — «Ставка компании».</summary>
    private void ApplyVatFromDetail(JsonElement card)
    {
        if (_vatCombo is null || _vatChanged)
            return;
        _vatOrig = FiscalFormRules.ReadProductVat(card);
        SelectVat(_vatOrig);
        _vatChanged = false;
    }

    private decimal? VatSelected => (_vatCombo?.SelectedItem as VatChoice)?.Rate;

    /// <summary>vat_rate в запрос: у нового товара — если выбрана ставка; у существующего — только если владелец менял и значение отличается от прочитанного.</summary>
    private bool VatRequestSend()
    {
        if (_vatCombo is null)
            return false;
        return _existing is null ? VatSelected is not null : _vatChanged && VatSelected != _vatOrig;
    }

    private decimal? VatRequestValue() => VatSelected;
}
