import 'package:flutter/widgets.dart';

import 'ui.dart';

/// 2026-11-11, ответ Умара (сервер NurCRM): пределы денежных полей — единое место (как ServerPriceLimits кассы).
/// Цена продажи и оптовая — до 9 999 999,999 сом (3 знака); закупка — до 9 999 999 999,9999 (4 знака); строка чека — до 9 999 999 999,99.
/// Цена выше предела даёт ошибку сервера — проверяем заранее и показываем понятный текст.
class PriceLimits {
  const PriceLimits._();

  static const double saleMax = 9999999.999;
  static const double purchaseMax = 9999999999.9999;
  static const double lineUnitMax = 9999999999.99;

  /// Допуск на шум double: пол-шага последнего знака (предел сам — можно, предел + 0,001 — нельзя).
  static const double _saleEps = 0.0005;
  static const double _purchaseEps = 0.00005;

  static bool saleExceeds(double v) => v.isNaN || v.isInfinite || v > saleMax + _saleEps;

  static bool purchaseExceeds(double v) => v.isNaN || v.isInfinite || v > purchaseMax + _purchaseEps;

  /// Какое поле превышено: 'sale' (продажа/опт), 'purchase' (закупка) или null — всё в пределах.
  static String? problemKind({double? sale, double? wholesale, double? purchase}) {
    if ((sale != null && saleExceeds(sale)) || (wholesale != null && saleExceeds(wholesale))) return 'sale';
    if (purchase != null && purchaseExceeds(purchase)) return 'purchase';
    return null;
  }

  static String saleText(BuildContext c) => c.t5(
        'Цена не может быть больше 9 999 999,999 сом',
        'Баа 9 999 999,999 сомдон ашпашы керек',
        "Narx 9 999 999,999 so'mdan oshmasligi kerak",
        'The price cannot be more than 9 999 999,999 som',
        'Fiyat 9 999 999,999 somdan fazla olamaz',
      );

  static String purchaseText(BuildContext c) => c.t5(
        'Закупочная цена не может быть больше 9 999 999 999,9999 сом',
        'Сатып алуу баасы 9 999 999 999,9999 сомдон ашпашы керек',
        "Xarid narxi 9 999 999 999,9999 so'mdan oshmasligi kerak",
        'The purchase price cannot be more than 9 999 999 999,9999 som',
        'Alış fiyatı 9 999 999 999,9999 somdan fazla olamaz',
      );

  /// Текст про первое превышение (null — всё в пределах). Для проверок перед сохранением и проведением.
  static String? problem(BuildContext c, {double? sale, double? wholesale, double? purchase}) {
    return switch (problemKind(sale: sale, wholesale: wholesale, purchase: purchase)) {
      'sale' => saleText(c),
      'purchase' => purchaseText(c),
      _ => null,
    };
  }
}
