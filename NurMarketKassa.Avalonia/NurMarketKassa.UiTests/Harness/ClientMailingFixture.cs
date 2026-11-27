using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-26 («Клиенты → Рассылка»): готовые клиенты для тестов окна — без сервера и без записи в настоящие файлы. Есть должники, давно не покупавшие, VIP, именинник
/// на этой неделе, один номер у двух клиентов, клиент без имени, с очень длинным именем и без телефона.</summary>
public static class ClientMailingFixture
{
    public static readonly string LongName = "Жумабаев Нурсултан Бакытбекович-уулу Токтогулов";

    public static ClientMailingData.Snapshot Snapshot()
    {
        var today = DateTime.Today;
        var birthday = new DateTime(2000, today.AddDays(3).Month, today.AddDays(3).Day);
        var list = new List<ClientMailing.Contact>
        {
            Contact("c1", "Айбек Асанов", "0700 111 222", 12500, today.AddDays(-5), 34000, 6, birthday),
            Contact("c2", "Бермет Токтогулова", "0555 333 444", 0, today.AddDays(-60), 5200, 2, null),
            Contact("c3", LongName, "0770 555 666", 480, today.AddDays(-45), 120000, 14, null),
            Contact("c4", "Айбек (второй номер)", "0700 111 222", 100, today.AddDays(-2), 800, 1, null),
            Contact("c5", "Без телефона", "", 300, today.AddDays(-9), 700, 1, null),
            Contact("c6", "", "0999 000 111", 700, today.AddDays(-90), 25000, 5, null),
            Contact("c7", "Гульнара Исаева", "0505 123 456", 0, today.AddDays(-31), 41000, 8, null),
            Contact("c8", "Данияр Орозов", "0772 654 321", 2300, today.AddDays(-120), 3000, 2, null), // 2026-11-26: был городской 0312… — у городских WhatsApp нет
            Contact("c9", "Элина Бакирова", "+7 701 123 45 67", 0, today.AddDays(-200), 22000, 4, null),
            Contact("c10", "Жаныбек Абдыров", "0701 987 654", 0, today.AddDays(-12), 600, 1, null),
        };
        return new ClientMailingData.Snapshot(list, DateTime.Now, true, true, false);
    }

    private static ClientMailing.Contact Contact(string id, string name, string phone, double debt, DateTime? last, double spent, int receipts, DateTime? birthday) =>
        new(id, name, phone, ClientMailing.WhatsAppNumber(phone), debt, last, spent, receipts, birthday); // 2026-11-26: то же правило, что ClientMailingData

    /// <summary>Окно рассылки с готовым списком; файл настроек не пишется на диск.</summary>
    public static ClientMailingWindow CreateWindow()
    {
        ClientMailing.ResetForTests();
        ClientMailingData.SeedForTests(Snapshot());
        return new ClientMailingWindow();
    }
}
