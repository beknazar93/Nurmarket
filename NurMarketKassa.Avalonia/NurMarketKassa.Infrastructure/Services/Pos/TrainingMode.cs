namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (Н-1, «Первый час»): учебный режим кассы. Пока он включён, «Оплатить» проходит по шагам с подсказками, но продажа НЕ уходит на сервер,
/// НЕ пишется в смену, журналы, остатки и офлайн-очередь. Состояние живёт только в памяти процесса: перезапуск кассы = обычный режим (забыть «выйти» нельзя).
/// Страховка от ошибки в окне: служба оплаты и офлайн-очередь сами отказывают, пока режим включён (PosCheckoutService.CheckoutAsync, OfflinePendingSalesStore.Append).</summary>
public static class TrainingMode
{
    private static volatile bool _active;

    /// <summary>Учебный режим включён.</summary>
    public static bool IsActive => _active;

    /// <summary>Сколько учебных продаж прошло с момента включения (для подсказки «Готово» и тестов).</summary>
    public static int SalesDone { get; private set; }

    /// <summary>Режим включили или выключили, либо прошла учебная продажа (плашка под шапкой обновляет текст).</summary>
    public static event Action? Changed;

    public static void Enter()
    {
        if (_active)
            return;
        _active = true;
        SalesDone = 0;
        PosLogger.Log("Учебный режим включён: продажи не уходят на сервер, в смену и очередь.", "TRAINING");
        Changed?.Invoke();
    }

    public static void Exit()
    {
        if (!_active)
            return;
        _active = false;
        PosLogger.Log($"Учебный режим выключен (учебных продаж: {SalesDone}).", "TRAINING");
        Changed?.Invoke();
    }

    /// <summary>Учебная продажа «прошла» (чек очищен, ничего никуда не записано).</summary>
    public static void NoteSaleDone()
    {
        if (!_active)
            return;
        SalesDone++;
        Changed?.Invoke();
    }

    /// <summary>Текст отказа для мест, которые в учебном режиме работать не должны (оплата на сервер, очередь, отложенные чеки, списание).</summary>
    public static string BlockedText => Tr.T(
        "Учебный режим: это действие не выполняется, деньги и товары не учитываются. Выйдите из учебного режима, чтобы работать по-настоящему.",
        "Окуу режими: бул аракет аткарылбайт, акча жана товарлар эсепке алынбайт. Чындап иштөө үчүн окуу режиминен чыгыңыз.",
        "Training mode: this action is not performed, money and stock are not recorded. Leave training mode to work for real.",
        "Eğitim modu: bu işlem yapılmaz, para ve stok kaydedilmez. Gerçek çalışmak için eğitim modundan çıkın.",
        "O'quv rejimi: bu amal bajarilmaydi, pul va tovarlar hisobga olinmaydi. Haqiqiy ishlash uchun o'quv rejimidan chiqing.");
}
