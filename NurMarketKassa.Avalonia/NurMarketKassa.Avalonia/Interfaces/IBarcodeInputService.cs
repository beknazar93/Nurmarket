using Avalonia.Input;

namespace NurMarketKassa.Interfaces;

public interface IBarcodeInputService
{
    event Action<string>? BarcodeScanned;
    void ProcessKeyDown(KeyEventArgs e);

    /// <summary>True, пока в буфере уже накоплены символы быстрого сканирования (штрихкод
    /// печатается сканером за миллисекунды) и они ещё не завершены символом Enter. Нужно другим
    /// window-level Tunnel-обработчикам Enter (например, "Оплатить по Enter"), чтобы отличить
    /// Enter-хвост активного скана от осознанного нажатия Enter кассиром — иначе Enter,
    /// которым сканер завершает штрихкод, мог бы случайно сработать как другое действие вместо
    /// добавления товара в корзину.</summary>
    bool HasBufferedInput { get; }
}
