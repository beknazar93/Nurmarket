using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (склад «10 из 10», К-5): «Срок годности и партия» строки прихода — срок годности ИЛИ дата выпуска + срок хранения (дней), и номер партии.
/// Вызывается кнопкой в строке «Склад → Приёмка» и «Закупки → Приём товара» ТОЛЬКО при признаке сервера expiry_batches / stock_batches (<see cref="ServerStock.ExpiryFieldsOn"/>).
/// Результат ShowDialog: true — поля строки изменены. Поля строки пишутся только после проверки (даты дд.мм.гггг, срок хранения — целое больше нуля).</summary>
public sealed class ReceivingBatchDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly string[] DateFormats = { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" };
    private readonly ReceivingLineVm _line;
    private readonly TextBox _expires = new() { Name = "BatchExpires" };
    private readonly TextBox _produced = new() { Name = "BatchProduced" };
    private readonly TextBox _shelf = new() { Name = "BatchShelfDays" };
    private readonly TextBox _lot = new() { Name = "BatchLotNo", MaxLength = 64 };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public ReceivingBatchDialog(ReceivingLineVm line)
    {
        _line = line;
        var title = T("Срок годности и партия", "Жарактуулук мөөнөтү жана партия", "Expiry date and batch", "Son kullanma tarihi ve parti", "Yaroqlilik muddati va partiya");
        Title = title;
        Width = 592;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        _expires.Text = line.ExpiresAt?.ToString("dd.MM.yyyy") ?? "";
        _produced.Text = line.ProducedAt?.ToString("dd.MM.yyyy") ?? "";
        _shelf.Text = line.ShelfLifeDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        _lot.Text = line.LotNo;
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("BrushDanger"));

        var cancel = new Button { Name = "BatchCancel", Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Name = "BatchSave", Content = T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), IsDefault = true };
        ok.Classes.Add("modal-primary");
        ok.Click += (_, _) => Save();

        var body = new StackPanel { Spacing = 6 };
        AddField(body, T("Срок годности (дд.мм.гггг)", "Жарактуулук мөөнөтү (кк.аа.жжжж)", "Expiry date (dd.mm.yyyy)", "Son kullanma tarihi (gg.aa.yyyy)", "Yaroqlilik muddati (kk.oo.yyyy)"), _expires);
        body.Children.Add(Note(T("или считаем сами: дата выпуска и срок хранения", "же өзүбүз эсептейбиз: чыгарылган күнү жана сактоо мөөнөтү", "or we calculate it: production date and shelf life", "ya da biz hesaplarız: üretim tarihi ve raf ömrü", "yoki o'zimiz hisoblaymiz: ishlab chiqarilgan sana va saqlash muddati")));
        AddField(body, T("Дата выпуска (дд.мм.гггг)", "Чыгарылган күнү (кк.аа.жжжж)", "Production date (dd.mm.yyyy)", "Üretim tarihi (gg.aa.yyyy)", "Ishlab chiqarilgan sana (kk.oo.yyyy)"), _produced);
        AddField(body, T("Срок хранения, дней", "Сактоо мөөнөтү, күн", "Shelf life, days", "Raf ömrü, gün", "Saqlash muddati, kun"), _shelf);
        AddField(body, T("Партия №", "Партия №", "Batch no.", "Parti no.", "Partiya №"), _lot);
        body.Children.Add(_error);

        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = title, Subtitle = line.ProductName,
            Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            Content = body, InitialFocus = _expires,
        };
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static void AddField(StackPanel host, string label, TextBox box)
    {
        var l = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("BrushTextSoft"));
        box.MinHeight = 40;
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        host.Children.Add(l);
        host.Children.Add(box);
    }

    private static bool TryDate(string? text, out DateTime? value)
    {
        value = null;
        var s = (text ?? "").Trim();
        if (s.Length == 0)
            return true;
        if (!DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return false;
        value = d;
        return true;
    }

    private void Fail(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private void Save()
    {
        if (!TryDate(_expires.Text, out var expires) || !TryDate(_produced.Text, out var produced))
        {
            Fail(T("Дата — в виде дд.мм.гггг.", "Күнү — кк.аа.жжжж түрүндө.", "Enter the date as dd.mm.yyyy.", "Tarihi gg.aa.yyyy olarak girin.", "Sanani kk.oo.yyyy ko'rinishida kiriting."));
            return;
        }
        int? shelf = null;
        var shelfText = (_shelf.Text ?? "").Trim();
        if (shelfText.Length > 0)
        {
            if (!int.TryParse(shelfText, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days <= 0 || days > 36500)
            {
                Fail(T("Срок хранения — целое число дней больше нуля.", "Сактоо мөөнөтү — нөлдөн чоң бүтүн күн саны.", "Shelf life is a whole number of days above zero.", "Raf ömrü sıfırdan büyük tam gün sayısı olmalı.", "Saqlash muddati — noldan katta butun kun soni."));
                return;
            }
            shelf = days;
        }
        if (expires is null && (produced is null) != (shelf is null))
        {
            Fail(T("Укажите и дату выпуска, и срок хранения (или сразу срок годности).", "Чыгарылган күндү да, сактоо мөөнөтүн да көрсөтүңүз (же жарактуулук мөөнөтүн).", "Enter both the production date and the shelf life (or the expiry date).",
                "Hem üretim tarihini hem raf ömrünü girin (ya da son kullanma tarihini).", "Ishlab chiqarilgan sana va saqlash muddatini birga kiriting (yoki yaroqlilik muddatini)."));
            return;
        }
        _line.ExpiresAt = expires;
        _line.ProducedAt = expires is null ? produced : null;
        _line.ShelfLifeDays = expires is null ? shelf : null;
        _line.LotNo = (_lot.Text ?? "").Trim();
        Close(true);
    }

    /// <summary>Короткая подпись строки для кнопки и подсказки: «Срок до 01.12.2026 · партия A-12»; пусто — ничего не задано.</summary>
    public static string Summary(ReceivingLineVm line)
    {
        var parts = new List<string>();
        if (line.ExpiresAt is { } e)
            parts.Add(T($"Срок до {e:dd.MM.yyyy}", $"Мөөнөтү {e:dd.MM.yyyy} чейин", $"Expires {e:dd.MM.yyyy}", $"SKT {e:dd.MM.yyyy}", $"Muddati {e:dd.MM.yyyy} gacha"));
        else if (line.ProducedAt is { } p && line.ShelfLifeDays is { } d)
            parts.Add(T($"Выпуск {p:dd.MM.yyyy} + {d} дн.", $"Чыгарылган {p:dd.MM.yyyy} + {d} күн", $"Made {p:dd.MM.yyyy} + {d} d", $"Üretim {p:dd.MM.yyyy} + {d} gün", $"Chiqarilgan {p:dd.MM.yyyy} + {d} kun"));
        if (line.LotNo.Length > 0)
            parts.Add(T($"партия {line.LotNo}", $"партия {line.LotNo}", $"batch {line.LotNo}", $"parti {line.LotNo}", $"partiya {line.LotNo}"));
        return string.Join(" · ", parts);
    }
}
