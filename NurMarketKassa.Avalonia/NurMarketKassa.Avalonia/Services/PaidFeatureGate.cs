using System;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Платные функции оформления и печати, которые открывают окна с ценой/покупкой.</summary>
public enum PaidFeature
{
    /// <summary>Редактор этикеток (<see cref="UserPreferences.LabelEditorUnlocked"/>).</summary>
    LabelEditor,
    /// <summary>Массовая печать штрихкодов и ценников (<see cref="UserPreferences.BulkPriceTagUnlocked"/>).</summary>
    BulkPriceTag,
}

/// <summary>2026-10-27 (аудит редакторов, Б-2): ОДНА проверка покупки платных окон печати. Раньше «Печать этикетки» → «Изменить шаблон»,
/// «Печать штрихкодов» → редактор и редактор → «Печать штрихкодов…» открывали платное окно мимо проверки; проверка была только в Складе.
/// Теперь все входы идут сюда: покупка есть — окно открывается; нет — просьба ввести серийный номер (тот же диалог и те же ключи,
/// что в Маркетплейсе и Складе; систему ключей это не меняет). Настоящая проверка покупки — на сервере (ТЗ бэкенду, Б-96).</summary>
public static class PaidFeatureGate
{
    /// <summary>Окно ввода серийного номера. Стенд проверок подменяет его отражением (без реального окна); в программе всегда <see cref="SerialActivationDialog.Show"/>.</summary>
    public static Func<Window?, string, string?> SerialPrompt = SerialActivationDialog.Show;

    /// <summary>Купить/открыть можно: флаг покупки включён (с учётом окончания временного доступа по мастер-ключу).</summary>
    public static bool IsUnlocked(PaidFeature feature)
    {
        try
        {
            LicenseKeys.ExpireIfDue();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Проверка срока временного доступа не удалась: {ex.Message}", "WARNING");
        }
        var prefs = UserPreferences.Instance;
        return feature switch
        {
            PaidFeature.LabelEditor => prefs.LabelEditorUnlocked,
            PaidFeature.BulkPriceTag => prefs.BulkPriceTagUnlocked,
            _ => false,
        };
    }

    /// <summary>true — функция куплена (или только что активирована серийным номером) и окно можно открывать.
    /// Не куплена — открывает ввод серийного номера; отмена или неверный номер — false.</summary>
    public static bool EnsureUnlocked(PaidFeature feature, Window? owner)
    {
        if (IsUnlocked(feature))
            return true;

        var (slug, name) = feature == PaidFeature.LabelEditor
            ? ("labeleditor", Tr.T("Редактор этикетки", "Этикетка редактору", "Label editor", "Etiket düzenleyici", "Yorliq muharriri"))
            : ("bulktag", Tr.T("Массовая печать ценников", "Ценниктерди массалык басып чыгаруу", "Bulk price tag printing", "Toplu fiyat etiketi yazdırma", "Narx yorliqlarini ommaviy chop etish"));

        var serial = SerialPrompt(owner, name);
        if (serial == null)
            return false;

        var isPermanent = LicenseKeys.IsPermanentSerial(slug, serial);
        if (!isPermanent && !LicenseKeys.IsMasterSerial(serial))
        {
            PosAlertDialog.Show(owner,
                Tr.T("Неверный серийный номер", "Серия номери туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return false;
        }

        if (feature == PaidFeature.LabelEditor)
            UserPreferences.Instance.LabelEditorUnlocked = true;
        else
            UserPreferences.Instance.BulkPriceTagUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor(slug);

        if (feature == PaidFeature.LabelEditor)
            PosAlertDialog.Show(owner,
                Tr.T("Редактор этикетки активирован", "Этикетка редактору иштетилди", "Label editor activated", "Etiket düzenleyici etkinleştirildi", "Yorliq muharriri faollashtirildi"),
                Tr.T("Теперь доступен редактор этикеток.", "Эми этикетка редактору жеткиликтүү.", "The label editor is now available.", "Etiket düzenleyici artık kullanılabilir.", "Endi yorliq muharriri mavjud."),
                PosAlertKind.Success);
        else
            PosAlertDialog.Show(owner,
                Tr.T("Массовая печать ценников активирована", "Ценниктерди массалык басып чыгаруу иштетилди", "Bulk price tag printing activated", "Toplu fiyat etiketi yazdırma etkinleştirildi", "Narx yorliqlarini ommaviy chop etish faollashtirildi"),
                Tr.T(
                    "Теперь доступна кнопка «🏷 Массовая печать ценников» в Склад → Товары — выберите нужные товары и распечатайте ценники сразу для всех.",
                    "Эми Склад → Товарлар бетинде «🏷 Ценниктерди массалык басып чыгаруу» баскычы жеткиликтүү — керектүү товарларды тандап, баарынын ценниктерин бир жолу басып чыгарыңыз.", "The “🏷 Bulk print price tags” button is now available in Warehouse → Products — select the products you need and print price tags for all of them at once.", "Depo → Ürünler bölümünde artık «🏷 Toplu fiyat etiketi yazdır» düğmesi var — gerekli ürünleri seçin ve hepsinin fiyat etiketlerini tek seferde yazdırın.", "Endi Ombor → Mahsulotlar bo'limida «🏷 Narx yorliqlarini ommaviy chop etish» tugmasi mavjud — kerakli mahsulotlarni tanlang va barchasi uchun narx yorliqlarini birdaniga chop eting."),
                PosAlertKind.Success);
        return true;
    }
}
