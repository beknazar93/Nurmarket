using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>Касса как главное окно после входа (см. IMainShell). PlaceOnPrimaryScreen у кассы
/// внутренний — наружу отдаётся через явную реализацию.</summary>
public partial class MainWindow : IMainShell
{
    void IMainShell.PlaceOnPrimaryScreen() => PlaceOnPrimaryScreen();
}
