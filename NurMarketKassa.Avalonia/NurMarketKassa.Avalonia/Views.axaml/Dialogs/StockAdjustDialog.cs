using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-06, редизайн склада: «±» в строке товара — изменить остаток без перехода во вкладки: приход, списание
/// (с причиной, как во вкладке «Списание») или точное количество (ревизия одного товара). Проводит ProductActions —
/// остаток перечитывается с сервера, документ ревизии с итоговым количеством. Результат ShowDialog — true, если проведено.</summary>
public sealed class StockAdjustDialog : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly CatalogProductTileVm _product;
    private readonly StackPanel _modes = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly WrapPanel _reasons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _qty;
    private readonly TextBox _note;
    private readonly TextBlock _preview = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _apply;
    private string _mode = "in";
    private string _reason = "";

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public StockAdjustDialog(CatalogProductTileVm product)
    {
        _product = product;
        Title = T("Изменить остаток", "Калдыкты өзгөртүү", "Change stock", "Stoğu değiştir", "Qoldiqni o'zgartirish");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };

        var root = new StackPanel { Margin = new Thickness(22, 18), Spacing = 10 };
        var title = new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);
        var name = new TextBlock { Text = product.Title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(name);
        var now = new TextBlock
        {
            Text = T($"Сейчас на складе: {Qty(product.Quantity)} {Unit}", $"Азыр кампада: {Qty(product.Quantity)} {Unit}", $"In stock now: {Qty(product.Quantity)} {Unit}",
                $"Şu an stokta: {Qty(product.Quantity)} {Unit}", $"Hozir omborda: {Qty(product.Quantity)} {Unit}"),
            FontSize = 13,
        };
        Use(now, TextBlock.ForegroundProperty, "BrushTextSoft");
        root.Children.Add(now);

        root.Children.Add(_modes);
        root.Children.Add(UiKit.Label(this, T("Количество", "Саны", "Quantity", "Miktar", "Miqdor")));
        _qty = UiKit.Input(this, "0", 44);
        _qty.Text = "1";
        _qty.TextChanged += (_, _) => UpdatePreview();
        _qty.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await ApplyAsync().ConfigureAwait(true); };
        root.Children.Add(_qty);
        root.Children.Add(_reasons);
        _note = UiKit.Input(this, T("Комментарий (необязательно)", "Комментарий (милдеттүү эмес)", "Comment (optional)", "Yorum (isteğe bağlı)", "Izoh (ixtiyoriy)"), 40);
        root.Children.Add(_note);
        Use(_preview, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(_preview);
        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _apply = UiKit.Primary(this, T("Провести", "Өткөрүү", "Apply", "Uygula", "O'tkazish"));
        _apply.Click += async (_, _) => await ApplyAsync().ConfigureAwait(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_apply);
        root.Children.Add(buttons);
        Content = root;

        BuildModes();
        Opened += (_, _) => { _qty.Focus(); _qty.SelectAll(); };
    }

    private string Unit => _product.MustWeigh ? T("кг", "кг", "kg", "kg", "kg") : T("шт", "даана", "pcs", "adet", "dona");

    private static string Qty(double v) => v.ToString("0.###", Ru);

    private void BuildModes()
    {
        _modes.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("in", T("＋ Приход", "＋ Кириш", "＋ Receipt", "＋ Giriş", "＋ Kirim")),
                     ("out", T("− Списание", "− Эсептен чыгаруу", "− Write-off", "− Düşüm", "− Hisobdan chiqarish")),
                     ("set", T("= Точное количество", "= Так сан", "= Exact quantity", "= Tam miktar", "= Aniq miqdor")),
                 })
        {
            var chip = UiKit.Chip(this, text, _mode == key);
            var k = key;
            chip.Click += (_, _) =>
            {
                _mode = k;
                _reason = "";
                BuildModes();
                if (k == "set")
                    _qty.Text = Qty(_product.Quantity);
                _qty.Focus();
                _qty.SelectAll();
            };
            _modes.Children.Add(chip);
        }

        // Причины — только у списания, те же, что во вкладке «Списание».
        _reasons.Children.Clear();
        _reasons.IsVisible = _mode == "out";
        if (_mode == "out")
        {
            var reasons = WarehouseViewModel.WriteOffReasons;
            if (_reason.Length == 0 && reasons.Length > 0)
                _reason = reasons[0];
            foreach (var r in reasons)
            {
                var chip = UiKit.Chip(this, r, r == _reason);
                chip.Margin = new Thickness(0, 0, 6, 6);
                var reason = r;
                chip.Click += (_, _) =>
                {
                    _reason = reason;
                    BuildModes();
                };
                _reasons.Children.Add(chip);
            }
        }
        UpdatePreview();
    }

    private double? ParsedQty() =>
        double.TryParse((_qty.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private void UpdatePreview()
    {
        _error.IsVisible = false;
        if (ParsedQty() is not { } q)
        {
            _preview.Text = "";
            return;
        }
        var result = _mode switch
        {
            "in" => _product.Quantity + q,
            "out" => _product.Quantity - q,
            _ => q,
        };
        _preview.Text = T($"Станет: {Qty(result)} {Unit} (остаток сверим с сервером при проведении)", $"Болот: {Qty(result)} {Unit} (калдык өткөрүүдө сервер менен текшерилет)",
            $"Will be: {Qty(result)} {Unit} (stock is checked with the server)", $"Olacak: {Qty(result)} {Unit} (stok sunucuyla kontrol edilir)",
            $"Bo'ladi: {Qty(result)} {Unit} (qoldiq server bilan tekshiriladi)");
    }

    private async Task ApplyAsync()
    {
        if (ParsedQty() is not { } q || (q <= 0 && _mode != "set"))
        {
            _error.Text = T("Укажите количество больше нуля.", "Нөлдөн көп санды көрсөтүңүз.", "Enter a quantity above zero.", "Sıfırdan büyük bir miktar girin.", "Noldan katta miqdorni kiriting.");
            _error.IsVisible = true;
            return;
        }
        var note = (_note.Text ?? "").Trim();
        var reason = _mode switch
        {
            "in" => T("Приход", "Кириш", "Receipt", "Giriş", "Kirim") + (note.Length > 0 ? ": " + note : ""),
            "out" => T("Списание", "Эсептен чыгаруу", "Write-off", "Düşüm", "Hisobdan chiqarish") + ": " + _reason + (note.Length > 0 ? " — " + note : ""),
            _ => T("Ревизия", "Ревизия", "Stock count", "Sayım", "Reviziya") + (note.Length > 0 ? ": " + note : ""),
        };
        _apply.IsEnabled = false;
        try
        {
            var result = await ProductActions.ChangeStockAsync(_product.Id,
                _mode == "in" ? q : _mode == "out" ? -q : null,
                _mode == "set" ? q : null,
                reason, NurMarketKassa.PosApp.CurrentUserDisplayName).ConfigureAwait(true);
            if (!result.Ok)
            {
                _error.Text = result.Message;
                _error.IsVisible = true;
                return;
            }
            (App.AppHost?.Services.GetService(typeof(NurMarketKassa.Core.Contracts.IUserPrompts)) as NurMarketKassa.Core.Contracts.IUserPrompts)?.ShowToast(result.Message);
            Close(true);
        }
        finally
        {
            _apply.IsEnabled = true;
        }
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
