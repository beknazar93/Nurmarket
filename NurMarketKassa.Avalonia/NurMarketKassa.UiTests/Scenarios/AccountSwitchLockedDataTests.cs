using FluentAssertions;
using Microsoft.Data.Sqlite;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.UiTests.Scenarios;

/// <summary>2026-11-27 (жалоба клиента: «Не удалось сменить компанию… не удалось убрать: data (Access to the path '…\NurMarketKassa\data' is denied.)»).
/// Причина: программа владельца читала базу кассы (SiblingTillQueue) соединением из пула и держала pos_local.db открытым до выхода — папку data целиком
/// не переименовать. Проверяем на стенде (AccountDataIsolation.RootOverride, временная папка): база, открытая «другой программой» на чтение, — копия в карантин
/// и очистка на месте, смена проходит; файл, открытый без общего доступа, — всё возвращено, ошибка с «Повторить»; файл «только чтение» не мешает;
/// SiblingTillQueue больше не держит базу; текст окна без путей и сообщений ОС на 5 языках.</summary>
[Collection("UiScenarios")]
public sealed class AccountSwitchLockedDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "switch-lock-" + Guid.NewGuid().ToString("N")[..8]);
    private string Data => Path.Combine(_root, "data");
    private string Db => Path.Combine(Data, "pos_local.db");

    public AccountSwitchLockedDataTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "backups"));
        File.WriteAllText(Path.Combine(Data, "backups", "pos_local.db.bak-1"), "old-company-backup");
        File.WriteAllText(Path.Combine(Data, "no-factory-pending.json"), "[]");
        File.WriteAllText(Path.Combine(_root, "state.json"), "{\"cart\":\"прежняя компания\"}");
        using (var c = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Products(id TEXT PRIMARY KEY, name TEXT); INSERT INTO Products VALUES('1','Хлеб'),('2','Молоко');"
                              + "CREATE TABLE OfflineSales(id INTEGER PRIMARY KEY, sync_status TEXT, json_data TEXT); INSERT INTO OfflineSales(sync_status, json_data) VALUES('pending','{}');"
                              + "CREATE INDEX ix_products_name ON Products(name); CREATE VIEW v_products AS SELECT * FROM Products;";
            cmd.ExecuteNonQuery();
        }
        AccountDataIsolation.RootOverride = _root;
    }

    public void Dispose()
    {
        AccountDataIsolation.RootOverride = null;
        SiblingTillQueue.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* временная папка */ }
    }

    private static long CountRows(string db, string table)
    {
        using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }

    private static long CountTables(string db)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%';";
        return (long)cmd.ExecuteScalar()!;
    }

    private string QuarantineSet() => Directory.GetDirectories(Path.Combine(_root, AccountDataIsolation.PreviousFolderName)).Single();

    [Fact]
    public void Database_open_by_other_reader_is_copied_to_quarantine_and_cleared_in_place()
    {
        // «Программа владельца»: соединение только для чтения держит pos_local.db (+ -wal/-shm) открытым — как пул SiblingTillQueue до исправления.
        using var holder = new SqliteConnection($"Data Source={Db};Mode=ReadOnly;Pooling=False");
        holder.Open();
        using (var read = holder.CreateCommand()) { read.CommandText = "SELECT COUNT(*) FROM Products;"; read.ExecuteScalar(); }

        AccountDataIsolation.QuarantineNowForStand().Should().BeNull("занятая на чтение база не должна срывать смену компании");

        var set = QuarantineSet();
        var copy = Path.Combine(set, "data", "pos_local.db");
        File.Exists(copy).Should().BeTrue("целая копия базы прежней компании лежит в карантине");
        CountRows(copy, "Products").Should().Be(2);
        CountRows(copy, "OfflineSales").Should().Be(1, "неотправленный чек прежней компании не потерян");
        File.Exists(Path.Combine(set, "data", "backups", "pos_local.db.bak-1")).Should().BeTrue("копии базы прежней компании унесены, иначе «восстановление из копии» вернуло бы её данные");
        File.Exists(Path.Combine(set, "data", "no-factory-pending.json")).Should().BeTrue();
        File.Exists(Path.Combine(set, "state.json")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "state.json")).Should().BeFalse();

        holder.Close();
        File.Exists(Db).Should().BeTrue("база осталась на месте (её держали), но пустая");
        CountTables(Db).Should().Be(0, "в базе новой компании не осталось таблиц прежней");
        Directory.Exists(Path.Combine(Data, "backups")).Should().BeFalse();
    }

    [Fact]
    public void File_locked_without_sharing_rolls_everything_back_and_offers_retry()
    {
        // Антивирус/чужая программа: файл открыт без общего доступа — ни перенести, ни скопировать.
        var locked = Path.Combine(Data, "no-factory-pending.json");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = AccountDataIsolation.QuarantineNowForStand();
            failure.Should().NotBeNull();
            failure!.CanRetry.Should().BeTrue("занятый файл — повод для «Повторить», а не тупик");
            failure.Details.Should().Contain("no-factory-pending.json");
            if (OperatingSystem.IsWindows())
                failure.Holders.Should().Contain(h => h.IsSelf, "Restart Manager называет держателя файла (здесь — сам процесс тестов)");
        }

        // Всё на месте: смесь двух компаний не осталась, база с данными прежней компании цела.
        File.Exists(Path.Combine(_root, "state.json")).Should().BeTrue();
        File.Exists(Path.Combine(Data, "backups", "pos_local.db.bak-1")).Should().BeTrue();
        CountRows(Db, "Products").Should().Be(2);
        CountRows(Db, "OfflineSales").Should().Be(1);
        var previous = Path.Combine(_root, AccountDataIsolation.PreviousFolderName);
        (Directory.Exists(previous) ? Directory.GetDirectories(previous) : []).Should().BeEmpty("пустой набор после отката убран — повтор в ту же секунду не упрётся в него");

        // Файл освободили — «Повторить» проходит.
        AccountDataIsolation.QuarantineNowForStand().Should().BeNull();
        File.Exists(Path.Combine(QuarantineSet(), "data", "pos_local.db")).Should().BeTrue();
        Directory.Exists(Data).Should().BeFalse("свободная папка data уносится целиком, как раньше");
    }

    [Fact]
    public void Read_only_files_do_not_block_the_switch()
    {
        var file = Path.Combine(Data, "no-factory-pending.json");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(_root, "state.json"), FileAttributes.ReadOnly);

        AccountDataIsolation.QuarantineNowForStand().Should().BeNull();

        var moved = Path.Combine(QuarantineSet(), "data", "no-factory-pending.json");
        File.Exists(moved).Should().BeTrue();
        File.SetAttributes(moved, FileAttributes.Normal);
        File.SetAttributes(Path.Combine(QuarantineSet(), "state.json"), FileAttributes.Normal);
    }

    [Fact]
    public void Owner_program_reading_till_queue_does_not_keep_till_database_open()
    {
        AccountDataIsolation.RootOverride = null;
        SiblingTillQueue.RootOverride = _root;
        SiblingTillQueue.CountUnsent().Should().Be(1);

        // Раньше соединение оставалось в пуле и держало файл: переименование папки data падало с «Access … is denied».
        var moved = Path.Combine(_root, "data-moved");
        var act = () => Directory.Move(Data, moved);
        act.Should().NotThrow("после чтения очереди кассы программа владельца не держит pos_local.db");
    }

    /// <summary>2026-11-27 (check-store-registry, хранилища 1.17.96): входы кассиров по QR-бейджу, рассылка клиентам и выгрузка в 1С — данные компании:
    /// при смене уходят в карантин (новая компания их не видит), при сорванной смене возвращаются на место. Попытки входа по бейджу — журнал компании в %LOCALAPPDATA%.</summary>
    [Fact]
    public void Badge_sessions_mailing_and_onec_files_leave_with_the_company_and_come_back_on_rollback()
    {
        string[] names = ["cashier_badge_sessions.dat", "client-mailing.json", "onec-export.json"];
        foreach (var n in names)
        {
            File.WriteAllText(Path.Combine(_root, n), "test-" + n); // не настоящий секрет — метка теста
            CompanyStoreRegistry.Classify(n).Should().Be(CompanyStoreRegistry.StoreKind.Company, n);
        }
        CompanyStoreRegistry.Classify("badge_attempts.json").Should().Be(CompanyStoreRegistry.StoreKind.Company);
        CompanyStoreRegistry.CompanyLocalItems.Should().Contain("badge_attempts.json", "файл лежит в %LOCALAPPDATA% — уносится вместе с журналами кассы");

        // Сорванная смена (файл занят) — всё на месте, прежняя компания продолжает работать со своими входами кассиров.
        using (new FileStream(Path.Combine(Data, "no-factory-pending.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            AccountDataIsolation.QuarantineNowForStand().Should().NotBeNull();
        foreach (var n in names)
            File.ReadAllText(Path.Combine(_root, n)).Should().Be("test-" + n, $"{n} возвращён после отката");

        // Смена прошла — в папке данных новой компании их нет, копия в карантине.
        AccountDataIsolation.QuarantineNowForStand().Should().BeNull();
        var set = QuarantineSet();
        foreach (var n in names)
        {
            File.Exists(Path.Combine(_root, n)).Should().BeFalse($"{n} не достаётся другой компании");
            File.ReadAllText(Path.Combine(set, n)).Should().Be("test-" + n);
        }
    }

    [Fact]
    public void Message_has_no_paths_or_os_text_in_all_languages_and_names_the_owner_program()
    {
        var details = @"не удалось убрать: data (Access to the path 'C:\Users\admin\AppData\Roaming\NurMarketKassa\data' is denied.)";
        var sibling = new FileLockHolders.Holder(Environment.ProcessId + 100_000, System.Diagnostics.Process.GetCurrentProcess().ProcessName, "NurCRM");
        var owner = new AccountSwitchFailure(details, [sibling], CanRetry: true);
        var antivirus = new AccountSwitchFailure(details, [new FileLockHolders.Holder(Environment.ProcessId + 100_001, "MsMpEng", "Antimalware Service Executable")], CanRetry: true);
        var unknown = new AccountSwitchFailure(details, [], CanRetry: true);
        owner.HeldByOwnerProgram.Should().BeTrue();
        antivirus.OtherProgramNames.Should().Equal("Antimalware Service Executable");

        var saved = UserPreferences.Instance.Language;
        try
        {
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
            {
                UserPreferences.Instance.Language = lang;
                foreach (var f in new[] { owner, antivirus, unknown })
                {
                    var text = UnsentDataDialog.SwitchBlockedMessage(f);
                    text.Should().NotBeNullOrWhiteSpace();
                    text.Should().NotContain(@"C:\").And.NotContain("Access to the path").And.NotContain("denied", $"{lang}: технические подробности — только в журнал");
                }
            }
            UserPreferences.Instance.Language = AppLanguage.Russian;
            UnsentDataDialog.SwitchBlockedMessage(owner).Should().Contain("программе владельца").And.Contain("Закрыть программу владельца и повторить");
            UnsentDataDialog.SwitchBlockedMessage(antivirus).Should().Contain("Antimalware Service Executable").And.Contain("Повторить");
            UnsentDataDialog.SwitchBlockedMessage(unknown).Should().Contain("Ничего не потеряно");
        }
        finally
        {
            UserPreferences.Instance.Language = saved;
        }
    }
}
