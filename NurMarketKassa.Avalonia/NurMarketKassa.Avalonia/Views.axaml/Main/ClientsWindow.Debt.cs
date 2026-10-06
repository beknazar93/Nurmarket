using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, ТЗ ч.12, п. 3.2 (сервер выложил 05.10): «Записать долг без продажи» в карточке клиента —
/// POST api/main/clients/{id}/debts/ {amount, comment, due_date}. Для долга, который был до кассы (тетрадь, другая
/// программа), без фиктивной продажи. Раньше такой долг можно было записать только продажей «в долг».</summary>
public partial class ClientsWindow
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private void InitDebtButton()
    {
        if (AddDebtButton == null)
            return;
        AddDebtButton.Content = T("＋ Записать долг без продажи", "＋ Сатуусуз карыз жазуу", "＋ Record a debt without a sale", "＋ Satışsız borç kaydet", "＋ Sotuvsiz qarz yozish");
        AddDebtButton.IsVisible = TariffGate.CanUseDebts;
    }

    private async void AddDebtButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client || string.IsNullOrWhiteSpace(client.Id))
            return;
        try
        {
            await AskDebtAsync(client).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долг без продажи: окно не открылось: {ex}", "WARNING");
        }
    }

    /// <summary>Окно «Долг без продажи». Возвращает сумму, если долг записан на сервере, иначе null.</summary>
    private async Task<string?> AskDebtAsync(ClientRow client)
    {
        var api = App.GetRequiredService<NurMarketApiClient>();
        var tcs = new TaskCompletionSource<string?>();
        var dialog = new Window
        {
            Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            Title = T("Долг без продажи", "Сатуусуз карыз", "Debt without a sale", "Satışsız borç", "Sotuvsiz qarz"),
        };
        dialog.Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 6 };
        var title = new TextBlock { Text = dialog.Title + " — " + client.FullName, FontSize = 18, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        panel.Children.Add(title);
        var hint = new TextBlock
        {
            Text = T("Для долга, который был до кассы (тетрадь, другая программа). Продажа не создаётся, товар со склада не списывается. Оплачивают его как обычный долг.",
                "Кассага чейинки карыз үчүн (дептер, башка программа). Сатуу түзүлбөйт, товар кампадан чыгарылбайт. Кадимки карыз сыяктуу төлөнөт.",
                "For a debt from before the till (notebook, another program). No sale is created and no stock is written off. It is paid like any other debt.",
                "Kasadan önceki bir borç için (defter, başka program). Satış oluşturulmaz, stoktan düşülmez. Normal borç gibi ödenir.",
                "Kassagacha bo'lgan qarz uchun (daftar, boshqa dastur). Sotuv yaratilmaydi, ombordan chiqarilmaydi. Oddiy qarz kabi to'lanadi."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(hint);

        TextBox Field(string label, string watermark, string value)
        {
            panel.Children.Add(UiKit.Label(this, label));
            var box = UiKit.Input(this, watermark, 42);
            box.Text = value;
            panel.Children.Add(box);
            return box;
        }

        var amount = Field(T("Сумма долга, сом *", "Карыздын суммасы, сом *", "Debt amount, som *", "Borç tutarı, som *", "Qarz summasi, so'm *"), "7000", "");
        var comment = Field(T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"),
            T("Например: долг из тетради за сентябрь", "Мисалы: сентябрдагы дептерден карыз", "E.g. September notebook debt", "Örn.: eylül defterinden borç", "Masalan: sentyabr daftaridagi qarz"), "");
        comment.MaxLength = 500;
        var due = Field(T("Вернуть до (ГГГГ-ММ-ДД)", "Кайтаруу мөөнөтү (ЖЖЖЖ-АА-КК)", "Due date (YYYY-MM-DD)", "Son ödeme (YYYY-AA-GG)", "Qaytarish muddati (YYYY-OO-KK)"),
            "2026-11-05", DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var error = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        panel.Children.Add(error);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => dialog.Close();
        var save = UiKit.Primary(this, T("Записать долг", "Карызды жазуу", "Record the debt", "Borcu kaydet", "Qarzni yozish"));
        save.Click += async (_, _) =>
        {
            void Fail(string text)
            {
                error.Text = text;
                error.IsVisible = true;
            }

            if (!decimal.TryParse((amount.Text ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var sum) || sum <= 0)
            {
                Fail(T("Введите сумму больше нуля.", "Нөлдөн чоң сумма жазыңыз.", "Enter an amount above zero.", "Sıfırdan büyük bir tutar girin.", "Noldan katta summa kiriting."));
                return;
            }
            if (!DateTime.TryParseExact((due.Text ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
            {
                Fail(T("Дату укажите в виде 2026-11-05.", "Датаны 2026-11-05 түрүндө жазыңыз.", "Enter the date as 2026-11-05.", "Tarihi 2026-11-05 biçiminde girin.", "Sanani 2026-11-05 ko'rinishida kiriting."));
                return;
            }
            var body = new Dictionary<string, object?>
            {
                ["amount"] = sum.ToString("0.00", CultureInfo.InvariantCulture),
                ["due_date"] = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            if ((comment.Text ?? "").Trim() is { Length: > 0 } note)
                body["comment"] = note;
            save.IsEnabled = false;
            error.IsVisible = false;
            try
            {
                await api.RequestAsync(HttpMethod.Post, $"api/main/clients/{Uri.EscapeDataString(client.Id)}/debts/", body, null, CancellationToken.None, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(true);
                var text = sum.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
                PosLogger.Log($"Долг без продажи: клиент {client.Id}, {text} сом, до {dueDate:dd.MM.yyyy}.", "INFO");
                tcs.TrySetResult(text);
                dialog.Close();
            }
            catch (Exception ex)
            {
                Fail(T("Не записано: ", "Жазылган жок: ", "Not recorded: ", "Kaydedilmedi: ", "Yozilmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
                PosLogger.Log($"Долг без продажи: не записан ({ex.Message}).", "WARNING");
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        row.Children.Add(cancel);
        row.Children.Add(save);
        panel.Children.Add(row);
        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        await dialog.ShowDialog(this).ConfigureAwait(true);
        var result = await tcs.Task.ConfigureAwait(true);
        if (result != null)
            await InfoAsync(T($"Долг {result} сом записан клиенту «{client.FullName}» в NurCRM.",
                $"«{client.FullName}» кардарына {result} сом карыз NurCRMге жазылды.",
                $"A debt of {result} som is recorded in NurCRM for \"{client.FullName}\".",
                $"\"{client.FullName}\" için {result} som borç NurCRM'e kaydedildi.",
                $"«{client.FullName}» mijozga {result} so'm qarz NurCRMga yozildi.")).ConfigureAwait(true);
        return result;
    }

    private async Task InfoAsync(string text)
    {
        var dialog = new Window { Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Title = Title };
        dialog.Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        panel.Children.Add(label);
        var ok = UiKit.Primary(this, "OK");
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(ok);
        dialog.Content = panel;
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }
}
