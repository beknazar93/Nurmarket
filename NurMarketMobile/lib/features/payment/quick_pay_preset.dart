import 'sales_repository.dart';

/// 2026-11-20 (Простой режим, М3): заготовка оплаты для экрана оплаты — способ, сколько получено, клиент и срок долга уже выбраны.
/// [auto] — оплата проводится сразу при открытии экрана (кнопка «Оплатить N сом» в Простом режиме): способ выбирать не нужно.
/// Деньги проводит ТОТ ЖЕ код оплаты (`PaymentScreen._pay`): сверка цен, ключ идемпотентности, очередь без связи, долг, журнал.
class QuickPayPreset {
  final PayMethod method;

  /// Получено наличными; null — «ровно» (без сдачи).
  final double? received;
  final Client? client;
  final DateTime? debtDue;
  final bool auto;
  const QuickPayPreset({this.method = PayMethod.cash, this.received, this.client, this.debtDue, this.auto = true});
}
