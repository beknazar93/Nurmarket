using Avalonia.Controls;
using Avalonia.Threading;

// 2026-10-04: WebView2 (встроенный Edge) есть только в Windows. В переносимой сборке (Linux/Android)
// окно «NurCRM» собирается без изменений, а вместо браузера внутри программы показывает свою панель
// ошибки с кнопкой «Открыть в браузере» — тот же путь, что и при сбое WebView2 в Windows.

namespace Microsoft.Web.WebView2.Core
{
    public sealed class CoreWebView2
    {
        public event EventHandler<EventArgs>? NavigationCompleted;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void Navigate(string url) => NavigationCompleted?.Invoke(this, EventArgs.Empty);
    }
}

namespace NurMarketKassa.AvaloniaHost.Controls
{
    using Microsoft.Web.WebView2.Core;

    public sealed class WebView2Host : Border
    {
        public event Action<CoreWebView2>? WebViewReady;
        public event Action<string>? InitializationFailed;

        public void Navigate(string url) =>
            Dispatcher.UIThread.Post(() => InitializationFailed?.Invoke(Tr.T(
                "Сайт внутри программы работает только в Windows. Откройте его в браузере.",
                "Программанын ичиндеги сайт Windows'то гана иштейт. Аны браузерде ачыңыз.",
                "The built-in site works only on Windows. Open it in the browser.",
                "Program içi site yalnızca Windows'ta çalışır. Tarayıcıda açın.",
                "Dastur ichidagi sayt faqat Windows'da ishlaydi. Uni brauzerda oching.")));

        // Событие объявлено ради совместимости с окном NurCRM; здесь оно не наступает.
        internal void RaiseReadyForCompat(CoreWebView2 view) => WebViewReady?.Invoke(view);
    }
}
