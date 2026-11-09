import '../../core/units.dart';
import '../../core/util/ui.dart';

// 2026-11-08 (владелец: «на складе 3 л, пришло 10 л, а приёмка ведёт как штучную»): единица товара из карточки видна в каждой
// строке приёмки (умная камера, «Приёмка» со сканером, приход от поставщика), а прибавка к остатку показана числами: 3 + 10 = 13 л.
// Только подписи — количество, остаток и запрос на сервер считаются как раньше (остаток + принятое, дробные допустимы).

/// «10 л», «2,5 кг».
String qtyWithUnit(double qty, String? unit) => '${qtyText(qty)} ${unitOrPcs(unit)}';

/// «3 + 10 = 13 л» — сколько станет на складе после прихода.
String receiveSumText(double before, double qty, String? unit) => '${qtyText(before)} + ${qtyText(qty)} = ${qtyText(stockAfter(before, qty))} ${unitOrPcs(unit)}';

/// Итог по строкам, по единицам отдельно: «10 л · 5 шт» (литры со штуками в одно число не складываем).
String receiveTotalsText(Iterable<({String unit, double qty})> lines) {
  final byUnit = <String, double>{};
  for (final x in lines) {
    final u = unitOrPcs(x.unit);
    byUnit[u] = (byUnit[u] ?? 0) + x.qty;
  }
  return byUnit.entries.map((e) => qtyWithUnit(e.value, e.key)).join(' · ');
}

/// Все строки — штуки (тогда годятся прежние подписи «N шт»).
bool receiveAllPieces(Iterable<String?> units) => units.every((u) => unitOrPcs(u) == 'шт');
