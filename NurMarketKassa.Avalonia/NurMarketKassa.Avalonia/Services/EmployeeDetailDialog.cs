using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-08: карточка одного сотрудника открывается по клику из списка (владелец
/// просил список, а не все карточки развёрнутыми сразу) — здесь редактируются 4 персональных
/// кода доступа (см. EmployeeAccessGate) и, если сотрудник привязан к серверу (ServerId), можно
/// его удалить с сайта прямо отсюда. Правки полей применяются сразу к переданному объекту
/// EmployeeAccessCode "на лету" (как раньше в развёрнутой карточке) — сохранение на диск делает
/// кнопка "Сохранить" на самой странице Настройки → Сотрудники, эта форма её не вызывает.</summary>
public sealed class EmployeeDetailDialog : Window
{
    public bool DeleteRequested { get; private set; }

    private readonly Button _deleteButton;
    private readonly TextBlock _errorText;

    public EmployeeDetailDialog(EmployeeAccessCode employee, Func<Task>? onDelete)
    {
        Title = employee.Name.Length > 0 ? employee.Name : Tr.T("Сотрудник", "Кызматкер", "Employee", "Personel", "Xodim");
        // 2026-10-22 (модалки, пакет 4): общая рамка ModalShell (S 512) вместо системной 420×480. Раньше «Готово» и
        // «Удалить сотрудника» лежали в конце прокрутки — на 910×512 их не было видно; теперь закреплены внизу.
        // Esc/✕/Enter — «Готово», поля и кнопки 44 px, подсказки и ошибки — строкой под полем, цвета темы.
        SectionCards.AsModal(this, 512);

        var panel = new StackPanel { Spacing = 8 };

        TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            return label;
        }

        TextBlock Note()
        {
            var note = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            return note;
        }

        ModalShell? shell = null;
        var nameBox = new TextBox
        {
            Text = employee.Name,
            Watermark = Tr.T("Имя сотрудника", "Кызматкердин аты", "Employee name", "Personel adı", "Xodim ismi"),
            IsReadOnly = employee.ServerId != null,
        };
        nameBox.TextChanged += (_, _) =>
        {
            employee.Name = nameBox.Text ?? "";
            Title = employee.Name.Length > 0 ? employee.Name : Tr.T("Сотрудник", "Кызматкер", "Employee", "Personel", "Xodim");
            if (shell is not null)
                shell.Title = Title;
        };
        SectionCards.Field(nameBox);

        // 2026-10-18, аудит: коды хранятся только хэшем — прежний код не показать. Поле пустое: ввод нового
        // кода заменяет прежний (пустое поле — прежний остаётся), «Убрать» — код снимается совсем.
        // 2026-10-18, решение владельца: новый код — ровно 6 цифр; прежний короткий работает, но помечен.
        string CodeWatermark(string action) => EmployeeAccessGate.HasCode(action, employee)
            ? EmployeeAccessGate.IsShortCode(action, employee)
                ? Tr.T("Короткий код — замените на 6 цифр", "Кыска код — 6 цифрага алмаштырыңыз",
                    "Short code — replace it with 6 digits", "Kısa kod — 6 haneli bir kodla değiştirin",
                    "Qisqa kod — 6 raqamga almashtiring")
                : Tr.T("Код задан — введите новый (6 цифр), чтобы заменить", "Код коюлган — алмаштыруу үчүн жаңысын киргизиңиз (6 цифра)",
                    "Code is set — type a new one (6 digits) to replace it", "Kod ayarlı — değiştirmek için yenisini girin (6 hane)",
                    "Kod o'rnatilgan — almashtirish uchun yangisini kiriting (6 raqam)")
            : Tr.T("Код не задан (6 цифр)", "Код коюлган эмес (6 цифра)", "No code set (6 digits)", "Kod ayarlanmadı (6 hane)", "Kod o'rnatilmagan (6 raqam)");

        // 2026-10-22 (модалки, пакет 4): что с кодом сейчас — строкой под полем (длинная подсказка в самом поле
        // обрезалась на ky), в поле — только «6 цифр».
        string SixDigits() => Tr.T("6 цифр", "6 цифра", "6 digits", "6 hane", "6 raqam");

        void CodeField(string label, string action)
        {
            panel.Children.Add(Label(label));
            var box = SectionCards.Field(new TextBox { Text = "", Watermark = SixDigits(), MaxLength = 6 });
            Avalonia.Automation.AutomationProperties.SetName(box, label);
            var state = Note();
            state.Text = CodeWatermark(action);
            var clearButton = SectionCards.Secondary(Tr.T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"));
            clearButton.Margin = new Thickness(8, 0, 0, 0);
            clearButton.IsVisible = EmployeeAccessGate.HasCode(action, employee);
            var codeError = SectionCards.ErrorText(this);
            box.TextChanged += (_, _) =>
            {
                // 2026-10-18: принимается только код из 6 цифр; иначе прежний код остаётся, под полем — ошибка.
                var text = box.Text?.Trim() ?? "";
                var valid = EmployeeAccessGate.IsSixDigits(text);
                EmployeeAccessGate.SetPendingCode(action, employee, valid ? text : null);
                SectionCards.ShowError(codeError, text.Length > 0 && !valid
                    ? Tr.T("Код — 6 цифр", "Код — 6 цифра", "The code must be 6 digits", "Kod 6 haneli olmalı", "Kod — 6 raqam")
                    : null);
                clearButton.IsVisible = EmployeeAccessGate.HasCode(action, employee);
                // 2026-10-18: набран верный новый код — пометка «короткий код» в подсказке больше не нужна.
                state.Text = valid
                    ? Tr.T("Новый код (6 цифр)", "Жаңы код (6 цифра)", "New code (6 digits)", "Yeni kod (6 hane)", "Yangi kod (6 raqam)")
                    : CodeWatermark(action);
            };
            clearButton.Click += (_, _) =>
            {
                box.Text = "";
                EmployeeAccessGate.ClearCode(action, employee);
                state.Text = CodeWatermark(action);
                clearButton.IsVisible = false;
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(clearButton, 1);
            row.Children.Add(box);
            row.Children.Add(clearButton);
            panel.Children.Add(row);
            panel.Children.Add(state);
            panel.Children.Add(codeError);
        }

        panel.Children.Add(Label(Tr.T("Имя", "Аты", "Name", "Ad", "Ismi")));
        panel.Children.Add(nameBox);

        if (!string.IsNullOrWhiteSpace(employee.Email))
            panel.Children.Add(BuildCopyableField(Tr.T("Логин", "Логин", "Login", "Kullanıcı adı", "Login"), employee.Email));

        // 2026-10-18, аудит: пароль сотрудника больше не хранится — его показывают один раз при создании.

        CodeField(
            Tr.T("Код на удаление из корзины", "Себеттен өчүрүү коду", "Cart item removal code", "Sepetten silme kodu", "Savatdan o'chirish kodi"),
            EmployeeAccessGate.CartDelete);
        CodeField(
            Tr.T("Код на удаление со склада", "Кампадан өчүрүү коду", "Warehouse deletion code", "Depodan silme kodu", "Ombordan o'chirish kodi"),
            EmployeeAccessGate.WarehouseDelete);
        CodeField(
            Tr.T("Код на редактирование товара", "Товарды түзөтүү коду", "Product editing code", "Ürün düzenleme kodu", "Mahsulotni tahrirlash kodi"),
            EmployeeAccessGate.ProductEdit);
        CodeField(
            Tr.T("Код на добавление товара", "Товар кошуу коду", "Product adding code", "Ürün ekleme kodu", "Mahsulot qo'shish kodi"),
            EmployeeAccessGate.ProductAdd);
        // 2026-10-18: код на приём оплаты долга — нужен, если в «Оплате долга» включено «нужен код сотрудника».
        CodeField(
            Tr.T("Код на приём оплаты долга", "Карыз төлөмүн кабыл алуу коду", "Debt payment code", "Borç ödemesi kodu", "Qarz to'lovini qabul qilish kodi"),
            EmployeeAccessGate.DebtPayment);

        _errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, FontSize = 13 };
        panel.Children.Add(SectionCards.ErrorBox(this, _errorText));

        // Удаление — второстепенная опасная кнопка слева в подвале (красная обводка); «Готово» — главная справа.
        _deleteButton = SectionCards.Secondary(Tr.T("Удалить сотрудника", "Кызматкерди өчүрүү", "Delete employee", "Personeli sil", "Xodimni o'chirish"));
        _deleteButton.Classes.Add("danger");
        _deleteButton.Click += async (_, _) =>
        {
            // 2026-10-18, аудит: удаление без подтверждения — одно случайное нажатие удаляло сотрудника и на сайте.
            var who = employee.Name.Length > 0 ? employee.Name : Tr.T("сотрудника", "кызматкерди", "the employee", "personeli", "xodimni");
            var confirmed = await PosConfirmDialog.ShowModalAsync(this,
                Tr.T("Удалить сотрудника?", "Кызматкерди өчүрөсүзбү?", "Delete the employee?", "Personel silinsin mi?", "Xodim o'chirilsinmi?"),
                onDelete is null
                    ? Tr.T($"Точно удалить «{who}»? Его коды доступа на этой кассе пропадут.",
                        $"«{who}» чын эле өчүрүлсүнбү? Анын бул кассадагы кирүү коддору жоголот.",
                        $"Really delete “{who}”? Their access codes on this till will be lost.",
                        $"«{who}» gerçekten silinsin mi? Bu kasadaki erişim kodları kaybolacak.",
                        $"«{who}» rostdan o'chirilsinmi? Uning bu kassadagi kirish kodlari yo'qoladi.")
                    : Tr.T($"Точно удалить «{who}»? Сотрудник будет удалён и на сайте app.nurcrm.kg — войти под ним больше не получится. Это нельзя отменить.",
                        $"«{who}» чын эле өчүрүлсүнбү? Кызматкер app.nurcrm.kg сайтынан да өчүрүлөт — анын аккаунту менен мындан ары кирүүгө болбойт. Муну артка кайтарууга болбойт.",
                        $"Really delete “{who}”? The employee will also be deleted on app.nurcrm.kg and will no longer be able to sign in. This cannot be undone.",
                        $"«{who}» gerçekten silinsin mi? Personel app.nurcrm.kg sitesinden de silinecek ve artık giriş yapamayacak. Bu geri alınamaz.",
                        $"«{who}» rostdan o'chirilsinmi? Xodim app.nurcrm.kg saytidan ham o'chiriladi va endi kira olmaydi. Buni bekor qilib bo'lmaydi."),
                Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                PosConfirmAccent.Danger).ConfigureAwait(true);
            if (!confirmed)
                return;

            if (onDelete is null)
            {
                DeleteRequested = true;
                Close(true);
                return;
            }

            // 2026-10-22 (модалки, пакет 4): пока идёт запрос на сайт — окно занято (IsBusy): кнопки, ✕ и Esc выключены,
            // повторное нажатие не шлёт второй запрос, окно не закрывается посреди удаления.
            if (shell is null || !shell.TryBeginBusy())
                return;
            _errorText.IsVisible = false;
            try
            {
                await onDelete();
                DeleteRequested = true;
                shell.EndBusy();
                Close(true);
            }
            catch (Exception ex)
            {
                shell.EndBusy();
                _errorText.Text = ex.Message;
                _errorText.IsVisible = true;
                _errorText.BringIntoView();
            }
        };

        var btnClose = SectionCards.Primary(Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: true);
        // Правки применяются сразу (сохраняет страница «Сотрудники»), поэтому Esc и ✕ — то же «Готово».
        btnClose.IsCancel = true;
        btnClose.Click += (_, _) => Close(false);

        shell = SectionCards.Shell(Title,
            Tr.T("Коды доступа на этой кассе. Сохранит их кнопка «Сохранить» на странице «Сотрудники».",
                "Бул кассадагы кирүү коддору. Аларды «Кызматкерлер» барагындагы «Сактоо» баскычы сактайт.",
                "Access codes on this till. The “Save” button on the “Employees” page saves them.",
                "Bu kasadaki erişim kodları. «Personel» sayfasındaki «Kaydet» düğmesi bunları kaydeder.",
                "Shu kassadagi kirish kodlari. Ularni «Xodimlar» sahifasidagi «Saqlash» tugmasi saqlaydi."),
            panel, SectionCards.Buttons(btnClose), _deleteButton);
        shell.InitialFocus = employee.ServerId != null ? btnClose : nameBox;
        Content = shell;
    }

    private Grid BuildCopyableField(string label, string value)
    {
        // 2026-10-22 (модалки, пакет 4): подпись цветом темы, поле и «Копировать» 44 px.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };

        var caption = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 8) };
        caption.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        Grid.SetColumnSpan(caption, 2);
        var box = SectionCards.Field(new TextBox { Text = value, IsReadOnly = true });
        Grid.SetRow(box, 1);

        var copyButton = SectionCards.Secondary(Tr.T("Копировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash"));
        copyButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetRow(copyButton, 1);
        Grid.SetColumn(copyButton, 1);
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(value);
                copyButton.Content = Tr.T("Скопировано ✓", "Көчүрүлдү ✓", "Copied ✓", "Kopyalandı ✓", "Nusxalandi ✓");
            }
        };

        grid.Children.Add(caption);
        grid.Children.Add(box);
        grid.Children.Add(copyButton);
        return grid;
    }
}
