using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «База знаний» (этап 5 бэклога «Доработки») — справочник вопрос/ответ, содержание пишется
/// прямо в XAML (статичный, курируемый текст, не требует построения списка в коде). Ответы
/// основаны на реально проверенном поведении кассы (обновления, права кассира, логи, тех
/// поддержка — всё уже построено в этой же сессии), НЕ на выдуманных догадках — контакты
/// поддержки намеренно не указаны конкретным номером/адресом, т.к. это не подтверждено
/// пользователем (см. отдельный плейсхолдер для WhatsApp QR в другом бэклоге).
/// </summary>
public partial class KnowledgeBaseWindow : Window
{
    public KnowledgeBaseWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
