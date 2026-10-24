using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class CashOperationsDialog : Window
{
    public Func<decimal, Task>? OpenShiftAction { get; set; }
    public Func<decimal?, Task>? CloseShiftAction { get; set; }

    public CashOperationsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => ApplyShiftState();
    }

    /// <summary>2026-10-22 (модалки, пакет 2): кнопки по состоянию смены — раньше обе были доступны всегда.
    /// Сессию не удалось получить (стенд, тесты) — как раньше, обе доступны.</summary>
    private void ApplyShiftState()
    {
        bool? open = null;
        try { open = App.GetRequiredService<IAppSession>().IsShiftOpen; }
        catch { /* сессии нет — обе кнопки, как было */ }

        OpenShiftButton.IsEnabled = open != true;
        CloseShiftButton.IsEnabled = open != false;
        // Жёлтая — та, что доступна; вторая — обводкой.
        SetPrimary(OpenShiftButton, open != true);
        SetPrimary(CloseShiftButton, open == true);
        StateText.IsVisible = open is not null;
        StateText.Text = open == true
            ? Tr.T("Смена открыта.", "Смена ачык.", "The shift is open.", "Vardiya açık.", "Smena ochiq.")
            : ServerShiftClosedState.Current is { } closedByServer ? ServerShiftTexts.Block(closedByServer) // 2026-10-22 (аудит новой сборки, П1)
            : Tr.T("Смена закрыта.", "Смена жабык.", "The shift is closed.", "Vardiya kapalı.", "Smena yopiq.");
        (open == true ? CloseShiftButton : OpenShiftButton).Focus();
    }

    private static void SetPrimary(Button button, bool primary)
    {
        button.Classes.Set("modal-primary", primary);
        button.Classes.Set("modal-secondary", !primary);
    }

    private async void OpenShift_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-22 (модалки, пакет 2): двойное нажатие — одно окно открытия смены (было — второе поверх первого).
        if (!Shell.TryBeginBusy())
            return;
        try
        {
            var dlg = App.GetRequiredService<OpenShiftDialog>();
            // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
            if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true || OpenShiftAction == null)
                return;

            await OpenShiftAction(dlg.OpeningCash).ConfigureAwait(true);
        }
        finally
        {
            Shell.EndBusy();
            ApplyShiftState();
        }
    }

    private async void CloseShift_Click(object? sender, RoutedEventArgs e)
    {
        if (!Shell.TryBeginBusy())
            return;
        try
        {
            var dlg = App.GetRequiredService<CloseShiftDialog>();
            if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true || CloseShiftAction == null)
                return;

            await CloseShiftAction(dlg.ClosingCash).ConfigureAwait(true);
        }
        finally
        {
            Shell.EndBusy();
            ApplyShiftState();
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);
}
