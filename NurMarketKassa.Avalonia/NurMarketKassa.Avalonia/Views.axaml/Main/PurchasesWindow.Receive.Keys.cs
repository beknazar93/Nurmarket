using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (аудит «Закупки — удобство и дизайн», Ф0: БП-5, БП-6, БП-8, БП-9, БП-10): «Закупки → Приём товара» — горячие клавиши
/// (F2/Ctrl+F — поле скана, F9/Ctrl+Enter — «Оприходовать», Del — убрать строку с «Вернуть» 5 с и Ctrl+Z), строка подсказок клавиш, крупное «Итого приход»
/// у главной кнопки, кнопки страницы доступны с Tab, зоны касания в Touch-теме, после проведения — «Печать этикеток (N)».
/// Отдельный файл: в остальных — по одной строке подключения (OpenReceive, ReceiveRow, ReceiveFooter, ReceiveShowResultAsync).</summary>
public partial class PurchasesWindow
{
    /// <summary>Сколько держится «Строка убрана · Вернуть». 2026-11-12 (аудит У-6): было 5 с — кассир, сканирующий дальше, не успевал; теперь 15 с (плашка уходит и по следующему действию: новая убранная строка заменяет прежнюю).</summary>
    internal static readonly TimeSpan ReceiveUndoTime = TimeSpan.FromSeconds(15);

    private sealed class ReceiveKeysUi
    {
        public readonly TextBlock TotalValue = new() { FontSize = 22, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        public readonly TextBlock TotalCaption = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        public readonly TextBlock Hint = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        public Border UndoBar = null!;
        public readonly TextBlock UndoText = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        public (ReceivingLineVm Line, int Index)? Removed;
        public DispatcherTimer? UndoTimer;
    }

    private readonly ConditionalWeakTable<ReceiveState, ReceiveKeysUi> _receiveKeys = new();

    /// <summary>Строка подсказок клавиш (названия клавиш не переводятся).</summary>
    internal static string ReceiveKeysHintText => T("F2 — поиск товара · Enter — следующее поле · F9 / Ctrl+Enter — оприходовать · Del — убрать строку (Ctrl+Z — вернуть) · F4 — способ приёма · Esc — отменить правку",
        "F2 — товар издөө · Enter — кийинки талаа · F9 / Ctrl+Enter — кирешке алуу · Del — сапты алып салуу (Ctrl+Z — кайтаруу) · F4 — кабыл алуу ыкмасы · Esc — оңдоону жокко чыгаруу",
        "F2 — find a product · Enter — next field · F9 / Ctrl+Enter — post · Del — remove the line (Ctrl+Z — bring back) · F4 — receive mode · Esc — undo the edit",
        "F2 — ürün ara · Enter — sonraki alan · F9 / Ctrl+Enter — kaydet · Del — satırı kaldır (Ctrl+Z — geri al) · F4 — kabul yolu · Esc — düzenlemeyi geri al",
        "F2 — mahsulot qidirish · Enter — keyingi maydon · F9 / Ctrl+Enter — kirim qilish · Del — qatorni olib tashlash (Ctrl+Z — qaytarish) · F4 — qabul usuli · Esc — tahrirni bekor qilish");

    /// <summary>БП-9: в Touch-теме (высота элементов темы ≥ 48) — не меньше 44 px; в остальных — как было.</summary>
    private double ReceiveTouchMin(double normal) =>
        this.TryFindResource("AppControlHeight", ActualThemeVariant, out var v) && v is double h && h >= 48 ? Math.Max(normal, 44) : normal;

    /// <summary>Подключение к странице приёма (вызов из OpenReceive): итог у кнопки, строка клавиш, плашка «Вернуть», обработчик клавиш, Tab по кнопкам.</summary>
    private void ReceiveKeysAttach(ReceiveState s, Grid page, Grid footer, StackPanel toolsBox)
    {
        var ui = _receiveKeys.GetValue(s, _ => new ReceiveKeysUi());

        // БП-6: «Итого приход» крупно, акцентом, табличные цифры — между вторичными кнопками и «Оприходовать».
        ui.TotalValue.Classes.Add("num");
        Use(ui.TotalValue, TextBlock.ForegroundProperty, "BrushAccentText");
        ui.TotalCaption.Text = T("Итого приход", "Жалпы кирүү", "Receipt total", "Giriş toplamı", "Jami kirim");
        Use(ui.TotalCaption, TextBlock.ForegroundProperty, "BrushTextSoft");
        var total = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0), Children = { ui.TotalCaption, ui.TotalValue } };
        Avalonia.Automation.AutomationProperties.SetName(total, ui.TotalCaption.Text);
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(s.Post, footer.ColumnDefinitions.Count - 1);
        Grid.SetColumn(total, footer.ColumnDefinitions.Count - 2);
        footer.Children.Add(total);

        // БП-5: строка подсказок клавиш внизу мелким текстом.
        footer.RowDefinitions = new RowDefinitions("Auto,Auto");
        ui.Hint.Text = ReceiveKeysHintText;
        Use(ui.Hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        Grid.SetRow(ui.Hint, 1);
        Grid.SetColumnSpan(ui.Hint, footer.ColumnDefinitions.Count);
        footer.Children.Add(ui.Hint);

        // Плашка «Строка убрана · Вернуть» (5 с), как отмена в корзине кассы.
        var undoButton = UiKit.Ghost(this, T("Вернуть", "Кайтаруу", "Bring back", "Geri al", "Qaytarish"));
        undoButton.Height = 36;
        undoButton.Focusable = true;
        undoButton.Margin = new Thickness(12, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(undoButton, T("Вернуть убранную строку", "Алынган сапты кайтаруу", "Bring back the removed line", "Kaldırılan satırı geri al", "Olib tashlangan qatorni qaytarish"));
        undoButton.Click += (_, _) => ReceiveUndoRemove(s);
        var undoGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { ui.UndoText, undoButton } };
        Grid.SetColumn(undoButton, 1);
        ui.UndoBar = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 0, 8), IsVisible = false, Child = undoGrid, BorderThickness = new Thickness(1) };
        Use(ui.UndoBar, Border.BackgroundProperty, "BrushPanelSoft");
        Use(ui.UndoBar, Border.BorderBrushProperty, "BrushBorder");
        Use(ui.UndoText, TextBlock.ForegroundProperty, "BrushText");
        toolsBox.Children.Add(ui.UndoBar);

        // БП-8: кнопки страницы доступны с Tab (UiKit создаёт их без фокуса); рамка фокуса — FocusAdorner темы.
        foreach (var b in page.GetLogicalDescendantsOfButtons())
            b.Focusable = true;

        page.AddHandler(InputElement.KeyDownEvent, (_, e) => ReceiveHotKey(s, e), RoutingStrategies.Tunnel);
        ReceiveTotalRefresh(s);
    }

    /// <summary>F2/Ctrl+F, F9/Ctrl+Enter, Del (Ctrl+Del в поле), Ctrl+Z. Скан сканером в поле обрабатывает ReceiveFieldScanKey (раньше по порядку подписки).</summary>
    private void ReceiveHotKey(ReceiveState s, KeyEventArgs e)
    {
        if (e.Handled || _receive != s)
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None || e.Key == Key.F && ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            ReceiveFocusScan(s);
            s.Scan.SelectAll();
            return;
        }
        if (e.Key == Key.F9 && e.KeyModifiers == KeyModifiers.None || e.Key == Key.Enter && ctrl)
        {
            e.Handled = true;
            if (s.Post.IsEnabled && !s.Busy)
                _ = ReceivePostAsync();
            return;
        }
        // Ctrl+Z — вернуть убранную строку: вне полей и в ПУСТОМ поле скана (после «убрать» курсор стоит там); в остальных полях Ctrl+Z — отмена набора текста.
        if (e.Key == Key.Z && ctrl && (focused is not TextBox || ReferenceEquals(focused, s.Scan) && string.IsNullOrEmpty(s.Scan.Text))
            && _receiveKeys.TryGetValue(s, out var ui) && ui.Removed is not null)
        {
            e.Handled = true;
            ReceiveUndoRemove(s);
            return;
        }
        if (e.Key == Key.Delete && (focused is not TextBox || ctrl) && ReceiveRowOf(s, focused) is { } line)
        {
            e.Handled = true;
            ReceiveRemoveLine(s, line);
        }
    }

    /// <summary>Строка таблицы, внутри которой стоит фокус (null — фокус не в таблице).</summary>
    private static ReceivingLineVm? ReceiveRowOf(ReceiveState s, Control? focused)
    {
        if (focused is null)
            return null;
        foreach (var (line, parts) in s.RowMap)
            if (ReferenceEquals(parts.Box, focused) || focused.GetVisualAncestors().Any(a => ReferenceEquals(a, parts.Box)))
                return line;
        return null;
    }

    /// <summary>Убрать строку (✕ или Del) — с плашкой «Вернуть» на 5 с; фокус — в поле скана.</summary>
    private void ReceiveRemoveLine(ReceiveState s, ReceivingLineVm line)
    {
        var index = s.Lines.IndexOf(line);
        if (index < 0)
            return;
        s.Lines.Remove(line);
        ReceiveRender();
        if (!_receiveKeys.TryGetValue(s, out var ui))
            return;
        ui.Removed = (line, index);
        var name = string.IsNullOrWhiteSpace(line.ProductName) ? line.Barcode : line.ProductName;
        ui.UndoText.Text = T($"Строка «{name}» убрана.", $"«{name}» сабы алынды.", $"The line “{name}” was removed.", $"«{name}» satırı kaldırıldı.", $"«{name}» qatori olib tashlandi.");
        ui.UndoBar.IsVisible = true;
        ui.UndoTimer?.Stop();
        ui.UndoTimer = new DispatcherTimer { Interval = ReceiveUndoTime };
        ui.UndoTimer.Tick += (_, _) => ReceiveForgetRemoved(s);
        ui.UndoTimer.Start();
        Dispatcher.UIThread.Post(() => { if (_receive == s) ReceiveFocusScan(s); }, DispatcherPriority.Background);
    }

    /// <summary>«Вернуть» / Ctrl+Z: строка встаёт на прежнее место.</summary>
    private void ReceiveUndoRemove(ReceiveState s)
    {
        if (!_receiveKeys.TryGetValue(s, out var ui) || ui.Removed is not { } removed)
            return;
        ReceiveForgetRemoved(s);
        if (!s.Lines.Contains(removed.Line))
            s.Lines.Insert(Math.Clamp(removed.Index, 0, s.Lines.Count), removed.Line);
        ReceiveRender();
    }

    private void ReceiveForgetRemoved(ReceiveState s)
    {
        if (!_receiveKeys.TryGetValue(s, out var ui))
            return;
        ui.UndoTimer?.Stop();
        ui.UndoTimer = null;
        ui.Removed = null;
        ui.UndoBar.IsVisible = false;
    }

    /// <summary>БП-6: сумма закупки по строкам с количеством — крупно у кнопки (вызов из ReceiveFooter).</summary>
    private void ReceiveTotalRefresh(ReceiveState s)
    {
        if (!_receiveKeys.TryGetValue(s, out var ui))
            return;
        var active = s.Lines.Where(l => l.Quantity > 0).ToList();
        ui.TotalValue.Text = MoneyText.Som(active.Sum(l => l.LineTotal));
        ui.TotalValue.FontSize = s.Narrow ? 19 : 22;
    }

    /// <summary>БП-10: после проведения — «Печать этикеток (N)» на принятые товары с карточкой (та же печать, что «Этикетки на приход» Склада). Только с правом печати и где печать работает.</summary>
    private async Task ReceiveOfferLabelsAsync(IReadOnlyList<JobLine> posted)
    {
        if (!BarcodePrintAccess.IsAllowed() || !LabelPrinterSelector.PrintingSupported)
            return;
        var tiles = new Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in CatalogCacheService.Products)
                tiles[t.Id] = t;
        }
        catch (InvalidOperationException) { /* каталог обновлялся — весовые узнаем по единице */ }
        var items = new List<(string ProductId, string? Barcode, int Copies)>();
        foreach (var l in posted.Where(l => !string.IsNullOrWhiteSpace(l.ProductId) && l.Quantity > 0 && l.VariantQuantities is not { Count: > 0 }))
        {
            var mustWeigh = tiles.TryGetValue(l.ProductId!, out var tile) && tile.MustWeigh;
            var copies = BarcodePrintCatalog.CopiesForQuantity(l.Quantity, l.Unit, mustWeigh, out var capped);
            items.Add((l.ProductId!, string.IsNullOrWhiteSpace(l.Barcode) ? null : l.Barcode.Trim(), capped ? 100 : copies));
        }
        if (items.Count == 0)
            return;
        var copiesTotal = items.Sum(i => Math.Min(i.Copies, 99));
        var print = await PosConfirmDialog.ShowModalAsync(this,
            T("Этикетки на приход", "Кирешеге этикеткалар", "Labels for this receipt", "Bu giriş için etiketler", "Kirim uchun yorliqlar"),
            T($"Напечатать этикетки на принятые товары? Товаров: {items.Count}, этикеток: {copiesTotal} (по количеству; копии можно поменять в окне печати).",
                $"Кабыл алынган товарларга этикетка басып чыгарабы? Товарлар: {items.Count}, этикеткалар: {copiesTotal} (саны боюнча; нускаларды басып чыгаруу терезесинде өзгөртсө болот).",
                $"Print labels for the received products? Products: {items.Count}, labels: {copiesTotal} (by quantity; copies can be changed in the print window).",
                $"Alınan ürünler için etiket yazdırılsın mı? Ürün: {items.Count}, etiket: {copiesTotal} (miktara göre; kopyalar yazdırma penceresinde değiştirilebilir).",
                $"Qabul qilingan mahsulotlarga yorliq chop etilsinmi? Mahsulotlar: {items.Count}, yorliqlar: {copiesTotal} (miqdor bo'yicha; nusxalarni chop etish oynasida o'zgartirish mumkin)."),
            T($"Печать этикеток ({items.Count})", $"Этикетка басуу ({items.Count})", $"Print labels ({items.Count})", $"Etiket yazdır ({items.Count})", $"Yorliq chop etish ({items.Count})"),
            T("Не сейчас", "Азыр эмес", "Not now", "Şimdi değil", "Hozir emas"), PosConfirmAccent.Primary, enterCancels: true).ConfigureAwait(true);
        if (print)
            await BarcodePrintWindow.OpenWith(this, items).ConfigureAwait(true);
    }
}

/// <summary>2026-11-11: все кнопки внутри элемента (для БП-8 — Tab по кнопкам страницы приёма).</summary>
internal static class ReceiveKeysExtensions
{
    public static IEnumerable<Button> GetLogicalDescendantsOfButtons(this Control root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>();
}
