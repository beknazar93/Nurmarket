using System.Net.Http;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>
/// 2026-09-28: вид магазина уходит на сервер (PATCH /api/users/company/ {market_sphere}, BE-18),
/// и все кассы компании берут его при входе (MarketSphereSync). Раньше вид выбирали вручную на
/// каждом компьютере. Менять вид компании может только владелец: у кассира сервер отвечает 403 —
/// тогда вид остаётся только в этой кассе, и мы честно об этом пишем.
/// </summary>
public partial class OperationsSettingsView
{
    private int _sphereSaveVersion;

    private async Task SaveSphereOnServerAsync(string sphere)
    {
        var version = ++_sphereSaveVersion;
        var normalized = MarketSpheres.Normalize(sphere);

        if (MarketSphereSync.ServerSphere == normalized)
        {
            ShowSphereServerStatus(Tr.T(
                "Этот вид уже задан для всех касс компании.",
                "Бул түр компаниянын бардык кассалары үчүн мурунтан эле коюлган.",
                "This store type is already set for all of the company's tills.",
                "Bu mağaza türü şirketin tüm kasaları için zaten ayarlı.",
                "Bu tur kompaniyaning barcha kassalari uchun allaqachon o'rnatilgan."));
            return;
        }

        ShowSphereServerStatus(Tr.T(
            "Сохраняю вид магазина для всех касс…",
            "Дүкөндүн түрү бардык кассалар үчүн сакталууда…",
            "Saving the store type for all tills…",
            "Mağaza türü tüm kasalar için kaydediliyor…",
            "Do'kon turi barcha kassalar uchun saqlanmoqda…"));

        string message;
        try
        {
            await App.GetRequiredService<ClientDebtsApiService>()
                .SetCompanyMarketSphereAsync(normalized)
                .ConfigureAwait(true);
            MarketSphereSync.NoteSavedOnServer(normalized);
            PosLogger.Log($"Вид магазина сохранён на сервере: {normalized}.", "API");
            message = Tr.T(
                "Сохранено для всех касс компании: они перестроятся при следующем входе.",
                "Компаниянын бардык кассалары үчүн сакталды: кийинки киргенде алар өзгөрөт.",
                "Saved for all of the company's tills: they will switch at their next sign-in.",
                "Şirketin tüm kasaları için kaydedildi: bir sonraki girişte değişecekler.",
                "Kompaniyaning barcha kassalari uchun saqlandi: keyingi kirishda ular o'zgaradi.");
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            PosLogger.Log("Вид магазина на сервере не изменён: нет прав владельца (403).", "API");
            message = Tr.T(
                "Вид изменён только в этой кассе: для всех касс его может менять только владелец." +
                (MarketSphereSync.ServerSphere is null ? "" : " При следующем входе касса вернётся к виду, заданному владельцем."),
                "Түр ушул кассада гана өзгөрдү: бардык кассалар үчүн аны ээси гана өзгөртө алат." +
                (MarketSphereSync.ServerSphere is null ? "" : " Кийинки киргенде касса ээси койгон түргө кайтат."),
                "Changed on this till only: only the owner can change it for all tills." +
                (MarketSphereSync.ServerSphere is null ? "" : " At the next sign-in the till will return to the type set by the owner."),
                "Yalnızca bu kasada değişti: tüm kasalar için yalnızca sahibi değiştirebilir." +
                (MarketSphereSync.ServerSphere is null ? "" : " Bir sonraki girişte kasa, sahibin belirlediği türe döner."),
                "Faqat shu kassada o'zgardi: barcha kassalar uchun uni faqat egasi o'zgartira oladi." +
                (MarketSphereSync.ServerSphere is null ? "" : " Keyingi kirishda kassa egasi belgilagan turga qaytadi."));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            PosLogger.Log($"Вид магазина на сервер не сохранён: {ex.Message}", "WARNING");
            message = Tr.T(
                "Вид изменён в этой кассе, но на сервер не сохранён (нет связи или ошибка сервера). Выберите его ещё раз, когда появится интернет.",
                "Түр ушул кассада өзгөрдү, бирок серверге сакталган жок (байланыш жок же сервер катасы). Интернет пайда болгондо аны кайра тандаңыз.",
                "Changed on this till, but not saved to the server (no connection or a server error). Select it again once you are back online.",
                "Bu kasada değişti ama sunucuya kaydedilemedi (bağlantı yok veya sunucu hatası). İnternet geldiğinde tekrar seçin.",
                "Shu kassada o'zgardi, lekin serverga saqlanmadi (aloqa yo'q yoki server xatosi). Internet paydo bo'lganda uni qayta tanlang.");
        }

        // Пока шёл запрос, могли выбрать другой вид — тогда этот ответ уже не актуален.
        if (version == _sphereSaveVersion)
            ShowSphereServerStatus(message);
    }

    private void ShowSphereServerStatus(string text)
    {
        SphereServerStatus.Text = text;
        SphereServerStatus.IsVisible = !string.IsNullOrWhiteSpace(text);
    }
}
