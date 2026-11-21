import '../sale/cart.dart';

/// 2026-11-20 (Простой режим, М7/М8): чистые расчёты торга и сдачи — без интерфейса, чтобы их можно было проверить таблицей.

/// Скидка чека до применения: сумма строк минус скидки строк (как база в «Скидка на чек»).
double cartBase(Cart c) => roundMoney(c.subtotal - c.lineDiscounts);

/// «До 10 / 50 / 100»: итог округляется ВНИЗ до кратного [step] (в пользу покупателя). null — округлять нечего: итог меньше шага
/// (получился бы ноль) или уже кратен шагу.
double? roundDownTotal(double base, int step) {
  if (step <= 0 || base < step) return null;
  final v = (base / step).floorToDouble() * step;
  return v >= base - 0.004 ? null : v;
}

/// «Договорились за …»: скидка суммой = база − договорённая сумма. null — сумма не подходит (ноль, не меньше базы).
double? agreedDiscount(double base, double agreed) {
  if (!(agreed > 0) || agreed >= base - 0.004) return null;
  return roundMoney(base - agreed);
}

/// Доля скидки от базы, % (для предупреждения о крупной скидке).
double discountPercentOf(double base, double discount) => base <= 0 ? 0 : discount / base * 100;

/// Скидка больше этой доли требует подтверждения (ТЗ Мини, М7: «предел 5 % без PIN»). PIN в мобилке нет — вместо него вопрос владельцу.
const haggleWarnPercent = 5.0;

/// Быстрые суммы «Получено»: точная сумма («Без сдачи») и ближайшие кратные 100 / 500 / 1000 / 5000 (и 10 000 для больших чеков) выше итога (без повторов, по возрастанию).
List<double> receivedQuickAmounts(double total) {
  if (!(total > 0)) return const [];
  final out = <double>{total};
  for (final step in const [100.0, 500.0, 1000.0, 5000.0, 10000.0]) {
    final v = (total / step).ceil() * step;
    if (v > total + 0.004) out.add(v);
  }
  return out.toList()..sort();
}

/// Размер скидки на чек в виде, который ждёт CartController.setOrderDiscount: сумма в сомах.
double haggleSumFor(double base, double newTotal) => roundMoney(base - newTotal);
