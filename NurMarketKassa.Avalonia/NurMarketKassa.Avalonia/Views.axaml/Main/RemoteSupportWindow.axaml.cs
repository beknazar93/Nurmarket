using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «Тех поддержка NurCRM» (этап 4 бэклога «Доработки») — запускает СТАНДАРТНЫЙ клиент AnyDesk
/// (устанавливать/поддерживать собственный протокол удалённого рабочего стола с нуля внутри
/// кассы — отдельная и очень рискованная для боевой POS-системы задача, пользователь явно
/// выбрал переиспользовать готовый проверенный инструмент вместо этого). Заранее настроенного
/// ID техподдержки NurMarket здесь нет — для этого нужен отдельный бизнес-аккаунт AnyDesk с
/// собственной сборкой клиента, это вне рамок правки кода; сейчас открывается ОБЫЧНЫЙ AnyDesk,
/// который сам генерирует одноразовый адрес подключения — кассир называет его оператору по
/// телефону/WhatsApp.
/// </summary>
public partial class RemoteSupportWindow : Window
{
    private static readonly string[] KnownAnyDeskPaths =
    {
        // В комплекте с самой кассой (см. AnyDesk\AnyDesk.exe в проекте, CopyToOutputDirectory —
        // копируется рядом с NurMarketKassa.Avalonia.exe при публикации) — проверяется первым,
        // так что установленная отдельно копия AnyDesk не обязательна.
        Path.Combine(AppContext.BaseDirectory, "AnyDesk", "AnyDesk.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyDesk", "AnyDesk.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AnyDesk", "AnyDesk.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AnyDesk", "AnyDesk.exe"),
    };

    private const string AnyDeskDownloadUrl = "https://anydesk.com/en/downloads/windows";

    public RemoteSupportWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private static string? FindInstalledAnyDesk()
    {
        foreach (var path in KnownAnyDeskPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private void RefreshStatus()
    {
        var found = FindInstalledAnyDesk();
        if (found != null)
        {
            StatusText.Text = Tr.T(
                "AnyDesk найден на этом компьютере.",
                "AnyDesk бул компьютерде табылды.");
            DownloadButton.IsVisible = false;
        }
        else
        {
            StatusText.Text = Tr.T(
                "AnyDesk не установлен. Нажмите «Запустить AnyDesk» — если он ещё не скачан, откроется страница загрузки.",
                "AnyDesk орнотулган эмес. «AnyDesk иштетүү» баскычын басыңыз — эгер али жүктөлбөсө, жүктөө барагы ачылат.");
            DownloadButton.IsVisible = true;
        }
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = FindInstalledAnyDesk();
        if (path == null)
        {
            OpenDownloadPage();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            StatusText.Text = Tr.T(
                "AnyDesk запущен. Назовите оператору адрес (ID), который появится в его окне.",
                "AnyDesk иштетилди. Анын терезесинде чыккан дарек (ID) операторго айтыңыз.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось запустить AnyDesk: {ex}", "WARNING");
            StatusText.Text = Tr.T(
                $"Не удалось запустить AnyDesk: {ex.Message}",
                $"AnyDesk иштетилген жок: {ex.Message}");
            DownloadButton.IsVisible = true;
        }
    }

    private void DownloadButton_Click(object? sender, RoutedEventArgs e) => OpenDownloadPage();

    private void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AnyDeskDownloadUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось открыть страницу загрузки AnyDesk: {ex}", "WARNING");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
