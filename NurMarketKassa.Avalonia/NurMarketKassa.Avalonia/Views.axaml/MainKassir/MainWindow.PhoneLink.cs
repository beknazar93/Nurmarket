using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>2026-10-27, пакет К-П1 (ТЗ «Мобилка и касса»): чеки телефона печатаются на этой кассе — запуск наблюдателя за сервером
/// и временное управление, пока нет окна настроек (пакет К-П3). Ctrl+Shift+F9 открывает небольшое меню: состояние, включить/выключить
/// на этой кассе, «Обновить сейчас» и список «Не напечатаны» (отказ принтера, чек старше 3 минут, неясный исход после
/// перезапуска) с кнопкой «Напечатать». Нового окна нет — обычное контекстное меню. Всё остальное делает
/// <see cref="PhoneSalePrintWatcher"/>; по умолчанию выключено.</summary>
public partial class MainWindow
{
    private bool _phoneLinkStarted;

    /// <summary>Вызывается один раз при старте кассы (после проверки смены). Повторный вызов и программа владельца — ничего не делают.</summary>
    private void StartPhoneSalePrint()
    {
        if (_phoneLinkStarted || AppMode.IsOwner)
            return;
        _phoneLinkStarted = true;
        try
        {
            PhoneSalePrintWatcher.StartDefault();
            AddHandler(KeyDownEvent, OnPhoneLinkKeyTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        catch (Exception ex)
        {
            // Печать чеков телефона не должна мешать запуску кассы.
            PosLogger.Log($"Чеки с телефона: наблюдатель не запущен: {ex.Message}", "WARNING");
        }
    }

    private void OnPhoneLinkKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F9 || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift))
            return;
        e.Handled = true;
        try
        {
            ShowPhoneLinkMenu();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: меню не открылось: {ex.Message}", "WARNING");
        }
    }

    private static string PhoneT(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private string DescribePhoneLink(PhoneSalePrintWatcher watcher)
    {
        var settings = watcher.Settings;
        if (!settings.Enabled)
            return PhoneT("Чеки с телефонов: выключено", "Телефон чектери: өчүк", "Phone receipts: off", "Telefon fişleri: kapalı", "Telefon cheklari: o'chiq");
        if (settings.MyCashboxes().Count == 0)
        {
            var other = settings.AssignedTo(PosApp.PosCashboxId);
            return !settings.RoutingKnown
                ? PhoneT("Включено, назначение печатающей кассы ещё не получено с сервера",
                    "Күйгүзүлгөн, басма кассанын дайындалышы серверден алына элек",
                    "On, the printing till assignment has not been received from the server yet",
                    "Açık, yazdıran kasa ataması henüz sunucudan alınmadı",
                    "Yoqilgan, chop etuvchi kassa tayinlanishi serverdan hali olinmagan")
                : string.IsNullOrWhiteSpace(other)
                    ? PhoneT("Включено, но печатающая касса не назначена — нажмите «Назначить эту кассу»",
                        "Күйгүзүлгөн, бирок басма касса дайындалган эмес — «Ушул кассаны дайындоо» басыңыз",
                        "On, but no printing till is assigned — press “Assign this till”",
                        "Açık, ancak yazdıran kasa atanmamış — «Bu kasayı ata» düğmesine basın",
                        "Yoqilgan, lekin chop etuvchi kassa tayinlanmagan — «Shu kassani tayinlash» tugmasini bosing")
                    : PhoneT("Включено, но чеки печатает другая касса",
                        "Күйгүзүлгөн, бирок чектерди башка касса басат",
                        "On, but another till prints the receipts",
                        "Açık, ancak fişleri başka bir kasa yazdırıyor",
                        "Yoqilgan, lekin cheklarni boshqa kassa chop etadi");
        }

        var note = watcher.LastNote switch
        {
            "offline" => PhoneT(" (нет связи с сервером)", " (сервер менен байланыш жок)", " (no connection to the server)", " (sunucu bağlantısı yok)", " (server bilan aloqa yo'q)"),
            "pause" => PhoneT(" (сервер просит паузу)", " (сервер тыным сурады)", " (the server asks for a pause)", " (sunucu ara istiyor)", " (server pauza so'ramoqda)"),
            _ => "",
        };
        return PhoneT("Чеки с телефонов печатаются на этой кассе", "Телефон чектери ушул кассада басылат", "Phone receipts are printed on this till",
            "Telefon fişleri bu kasada yazdırılır", "Telefon cheklari shu kassada chop etiladi") + note;
    }

    private void ShowPhoneLinkMenu()
    {
        var watcher = PhoneSalePrintWatcher.Current;
        if (watcher is null)
            return;
        var settings = watcher.Settings;
        var canManage = _permissions.HasPermission(PosPermissions.ViewSettings);

        var items = new List<Control>
        {
            new MenuItem { Header = DescribePhoneLink(watcher), IsEnabled = false },
        };

        if (canManage)
        {
            var enabled = settings.Enabled;
            var toggle = new MenuItem
            {
                Header = enabled
                    ? PhoneT("Выключить печать чеков с телефонов на этой кассе", "Ушул кассада телефон чектерин басууну өчүрүү",
                        "Turn off phone receipts on this till", "Bu kasada telefon fişlerini kapat", "Shu kassada telefon cheklarini o'chirish")
                    : PhoneT("Включить печать чеков с телефонов на этой кассе", "Ушул кассада телефон чектерин басууну күйгүзүү",
                        "Turn on phone receipts on this till", "Bu kasada telefon fişlerini aç", "Shu kassada telefon cheklarini yoqish"),
            };
            toggle.Click += async (_, _) => await TogglePhoneLinkAsync(watcher, !enabled).ConfigureAwait(true);
            items.Add(toggle);

            if (enabled && settings.MyCashboxes().Count == 0)
            {
                var assign = new MenuItem
                {
                    Header = PhoneT("Назначить эту кассу печатающей", "Ушул кассаны басма касса кылып дайындоо",
                        "Assign this till as the printing one", "Bu kasayı yazdıran olarak ata", "Shu kassani chop etuvchi qilib tayinlash"),
                };
                assign.Click += async (_, _) => await AssignPhoneLinkAsync(watcher).ConfigureAwait(true);
                items.Add(assign);
            }
        }

        if (settings.Enabled)
        {
            var refresh = new MenuItem
            {
                Header = PhoneT("Проверить сервер сейчас", "Серверди азыр текшерүү", "Check the server now", "Sunucuyu şimdi kontrol et", "Serverni hozir tekshirish"),
            };
            refresh.Click += async (_, _) =>
            {
                try
                {
                    await watcher.Settings.RefreshRoutingAsync(true).ConfigureAwait(true);
                    await watcher.PollOnceAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Чеки с телефона: проверка не удалась: {ex.Message}", "WARNING");
                }
            };
            items.Add(refresh);
        }

        var unprinted = watcher.Unprinted();
        items.Add(new Separator());
        items.Add(new MenuItem
        {
            Header = PhoneT($"Не напечатаны: {unprinted.Count}", $"Басылбаганы: {unprinted.Count}", $"Not printed: {unprinted.Count}",
                $"Yazdırılmayanlar: {unprinted.Count}", $"Chop etilmaganlar: {unprinted.Count}"),
            IsEnabled = false,
        });
        foreach (var entry in unprinted.Take(12))
        {
            var time = (entry.SaleTimeUtc ?? entry.DiscoveredUtc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            var title = $"{(string.IsNullOrWhiteSpace(entry.Number) ? "—" : "№" + entry.Number.TrimStart('№'))} · {time} · {entry.Total.ToString("N2", CultureInfo.CurrentCulture)}"
                        + (string.IsNullOrWhiteSpace(entry.Staff) ? "" : " · " + entry.Staff)
                        + "  —  " + PhoneT("Напечатать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish");
            var item = new MenuItem { Header = title };
            if (!string.IsNullOrWhiteSpace(entry.Error))
                ToolTip.SetTip(item, entry.Error);
            var id = entry.SaleId;
            item.Click += async (_, _) => await RetryPhonePrintAsync(watcher, id).ConfigureAwait(true);
            items.Add(item);
        }

        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Center };
        menu.Open(this);
    }

    private async Task TogglePhoneLinkAsync(PhoneSalePrintWatcher watcher, bool on)
    {
        try
        {
            var settings = watcher.Settings;
            if (!settings.SetEnabled(on))
            {
                _prompts.ShowWarning(PhoneT("Не удалось сохранить настройку или компания ещё не определена.",
                    "Жөндөөнү сактоо мүмкүн болгон жок же компания аныкталала элек.",
                    "Could not save the setting or the company is not known yet.",
                    "Ayar kaydedilemedi veya şirket henüz belirlenmedi.",
                    "Sozlamani saqlab bo'lmadi yoki kompaniya hali aniqlanmagan."));
                return;
            }

            if (!on)
            {
                // Снять назначение с сервера (если не получится — не страшно: локально выключено, печатать не будем).
                if (!string.IsNullOrWhiteSpace(PosApp.PosCashboxId))
                    await settings.SetAssignmentAsync(PosApp.PosCashboxId!, false).ConfigureAwait(true);
                _prompts.ShowToast(PhoneT("Печать чеков с телефонов выключена.", "Телефон чектерин басуу өчүрүлдү.", "Phone receipt printing is off.",
                    "Telefon fişi yazdırma kapatıldı.", "Telefon cheklarini chop etish o'chirildi."));
                return;
            }

            await AssignPhoneLinkAsync(watcher).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: переключение не удалось: {ex.Message}", "WARNING");
        }
    }

    private async Task AssignPhoneLinkAsync(PhoneSalePrintWatcher watcher)
    {
        try
        {
            var cashbox = PosApp.PosCashboxId;
            if (string.IsNullOrWhiteSpace(cashbox))
            {
                _prompts.ShowWarning(PhoneT("Касса ещё не выбрана — откройте смену и повторите.", "Касса азырынча тандалган эмес — сменаны ачып, кайра аракет кылыңыз.",
                    "The till is not selected yet — open a shift and try again.", "Kasa henüz seçilmedi — vardiyayı açıp tekrar deneyin.",
                    "Kassa hali tanlanmagan — smenani ochib, qayta urinib ko'ring."));
                return;
            }

            var (outcome, error) = await watcher.Settings.SetAssignmentAsync(cashbox!, true).ConfigureAwait(true);
            switch (outcome)
            {
                case PhonePrintSettings.SaveOutcome.Saved:
                    _prompts.ShowToast(PhoneT("Готово: чеки с телефонов будет печатать эта касса.", "Даяр: телефон чектерин ушул касса басат.",
                        "Done: this till will print phone receipts.", "Tamam: telefon fişlerini bu kasa yazdıracak.", "Tayyor: telefon cheklarini shu kassa chop etadi."));
                    break;
                case PhonePrintSettings.SaveOutcome.Forbidden:
                    _prompts.ShowWarning(PhoneT("Назначить печатающую кассу может только владелец или администратор. Войдите под ним на этой кассе и повторите.",
                        "Басма кассаны ээси же администратор гана дайындай алат. Ушул кассага ал катары кирип, кайра аракет кылыңыз.",
                        "Only the owner or an administrator can assign the printing till. Sign in as one on this till and try again.",
                        "Yazdıran kasayı yalnızca sahip veya yönetici atayabilir. Bu kasada onunla giriş yapıp tekrar deneyin.",
                        "Chop etuvchi kassani faqat ega yoki administrator tayinlay oladi. Shu kassada u bilan kirib, qayta urinib ko'ring."));
                    break;
                case PhonePrintSettings.SaveOutcome.Offline:
                    _prompts.ShowWarning(PhoneT("Нет связи с сервером — назначение не сохранено.", "Сервер менен байланыш жок — дайындоо сакталган жок.",
                        "No connection to the server — the assignment was not saved.", "Sunucu bağlantısı yok — atama kaydedilmedi.", "Server bilan aloqa yo'q — tayinlash saqlanmadi."));
                    break;
                default:
                    _prompts.ShowWarning(PhoneT("Не удалось сохранить назначение: ", "Дайындоону сактоо мүмкүн болгон жок: ", "Could not save the assignment: ",
                        "Atama kaydedilemedi: ", "Tayinlashni saqlab bo'lmadi: ") + error);
                    break;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: назначение не удалось: {ex.Message}", "WARNING");
        }
    }

    private async Task RetryPhonePrintAsync(PhoneSalePrintWatcher watcher, string saleId)
    {
        try
        {
            var error = await watcher.RetryAsync(saleId).ConfigureAwait(true);
            if (error is null)
                _prompts.ShowToast(PhoneT("Чек напечатан.", "Чек басылып чыкты.", "Receipt printed.", "Fiş yazdırıldı.", "Chek chop etildi."));
            else
                _prompts.ShowWarning(error);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: повтор печати не удался: {ex.Message}", "WARNING");
        }
    }
}
