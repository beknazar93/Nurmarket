// Программа сотрудника поддержки NurMarket (2026-10-16). Подключение к кассам клиентов через ретранслятор NurRemoteSupport
// (../PROTOCOL.md, v1.1): очередь касс, ждущих помощи, экран и управление, чат с кассиром, сведения и диагностика кассы,
// журналы, файлы, буфер обмена, общие заметки операторов (история обращений), журнал сеансов.
using Avalonia;

namespace NurRemoteSupport.Operator;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
