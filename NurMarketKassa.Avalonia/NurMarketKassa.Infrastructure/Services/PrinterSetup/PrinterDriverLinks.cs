using System.Diagnostics;

namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>2026-11-13 (кнопка «Скачать драйвер Xprinter»): только ССЫЛКА на официальный сайт производителя — драйвер в установщик кассы не кладём.
/// Страница «DOWNLOAD-Xprinter» (проверено 13.11.2026): разделы «POS» (чековые XP-58IIH, XP-E200L, XP-V3…) и «Label» (этикеточные XP-420B, XP-470B…).</summary>
public static class PrinterDriverLinks
{
    public const string XprinterDownloadUrl = "https://www.xprintertech.com/download.html";
    public const string XprinterReceiptDriversUrl = XprinterDownloadUrl + "#POS";
    public const string XprinterLabelDriversUrl = XprinterDownloadUrl + "#Label";

    public static string UrlFor(PrinterRole role) => role == PrinterRole.Label ? XprinterLabelDriversUrl : XprinterReceiptDriversUrl;

    /// <summary>Когда драйвер производителя нужен — пояснение рядом с кнопкой.</summary>
    public static string WhenNeededText => Tr.T(
        "Драйвер производителя нужен только для печати из других программ (Word, Excel, браузер, 1С). Касса NurCRM печатает и без него — через мастер «Подключить принтер». На сайте выберите свою модель (например XP-58IIH, XP-80C, XP-365B) и скачайте драйвер для Windows.",
        "Өндүрүүчүнүн драйвери башка программалардан (Word, Excel, браузер, 1С) басуу үчүн гана керек. NurCRM кассасы ансыз да басат — «Принтерди туташтыруу» чебери аркылуу. Сайттан өз моделиңизди (мисалы XP-58IIH, XP-80C, XP-365B) тандап, Windows үчүн драйверди жүктөп алыңыз.",
        "The manufacturer's driver is needed only to print from other programs (Word, Excel, a browser, 1C). NurCRM prints without it through the “Connect printer” wizard. On the site, choose your model (e.g. XP-58IIH, XP-80C, XP-365B) and download the Windows driver.",
        "Üretici sürücüsü yalnızca diğer programlardan (Word, Excel, tarayıcı, 1C) yazdırmak için gerekir. NurCRM kasası onsuz da «Yazıcıyı bağla» sihirbazıyla yazdırır. Sitede modelinizi seçin (ör. XP-58IIH, XP-80C, XP-365B) ve Windows sürücüsünü indirin.",
        "Ishlab chiqaruvchi drayveri faqat boshqa dasturlardan (Word, Excel, brauzer, 1C) chop etish uchun kerak. NurCRM kassasi usiz ham «Printerni ulash» ustasi orqali chop etadi. Saytda o'z modelingizni tanlang (masalan XP-58IIH, XP-80C, XP-365B) va Windows uchun drayverni yuklab oling.");

    /// <summary>Подмена открытия ссылки для тестов (в программе null — открывается браузер).</summary>
    internal static Action<string>? OpenOverride { get; set; }

    /// <summary>Открыть страницу в браузере. false — не открылась (нет браузера); ссылку тогда показать текстом.</summary>
    public static bool Open(string url)
    {
        PosLogger.Log($"Мастер принтера: открыта страница драйверов {url}.", "PRINTER");
        if (OpenOverride is { } fake)
        {
            fake(url);
            return true;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Driver page not opened: {ex.GetType().Name}", "WARNING");
            return false;
        }
    }
}
