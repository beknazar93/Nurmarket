// 2026-10-26 (голос-управление, В3а): «отмени последнее» — стек действий ИИ этого звонка (до 20), снимки корзины и старые
// значения (раздел 2.5 б ТЗ). Отмена только последнего действия ИИ этого пользователя: не ручных действий и не чужих.
// Необратимое (возврат, погашение долга, закрытие смены, проведённый приход, выплата) ИИ называет честно и не трогает
// предыдущие. Файл без Flutter и без сети.
import '../../../features/sale/cart.dart';

enum UndoStatus { ok, conflict, failed }

/// Итог отката. [say] — готовая фраза на языке пользователя (её пишет само действие).
class UndoResult {
  final UndoStatus status;
  final String? say;
  const UndoResult(this.status, [this.say]);
  static const ok = UndoResult(UndoStatus.ok);
  static const conflict = UndoResult(UndoStatus.conflict);
  static const failed = UndoResult(UndoStatus.failed);
}

/// Запись о действии ИИ в стеке. [undo] есть — отменить можно; нет — [whyNot] объясняет на языке пользователя, почему
/// (и предлагает компенсирующее действие отдельной просьбой, которое ИИ САМ не выполняет).
class UndoEntry {
  final String id;
  final String userId;
  final DateTime at;
  final String tool;

  /// Что сделано, кратко, для «отменила: …» и «последние действия» (на языке реплики).
  final String Function(String lang) label;

  /// Старые значения и ссылки для журнала `ai_actions.jsonl` (товар, цена было/стало, документ). Без ключей и телефонов.
  final Map<String, dynamic> data;
  final Future<UndoResult> Function(String lang)? undo;
  final String Function(String lang)? whyNot;
  const UndoEntry({
    required this.id,
    required this.userId,
    required this.at,
    required this.tool,
    required this.label,
    this.data = const {},
    this.undo,
    this.whyNot,
  });

  bool get reversible => undo != null;
}

/// Стек действий ИИ: новые сверху. До [limit] записей.
class UndoStack {
  final int limit;
  final _items = <UndoEntry>[];
  UndoStack({this.limit = 20});

  void push(UndoEntry e) {
    _items.add(e);
    while (_items.length > limit) {
      _items.removeAt(0);
    }
  }

  /// Последнее действие ИИ этого пользователя; null — нет.
  UndoEntry? lastFor(String userId) {
    for (var i = _items.length - 1; i >= 0; i--) {
      if (_items[i].userId == userId) return _items[i];
    }
    return null;
  }

  void remove(UndoEntry e) => _items.remove(e);

  /// Новые первыми.
  List<UndoEntry> recent(String userId, [int n = 5]) => [
    for (final e in _items.reversed)
      if (e.userId == userId) e,
  ].take(n).toList();

  bool get isEmpty => _items.isEmpty;
  int get length => _items.length;
  void clear() => _items.clear();
}

/// Глубокая копия чека: строки меняются на месте (`Cart.add` прибавляет количество в существующую строку), поэтому снимок
/// обязан быть отдельным объектом.
Cart cloneCart(Cart c) => Cart(
  orderDiscountPercent: c.orderDiscountPercent,
  orderDiscountSum: c.orderDiscountSum,
  lines: [
    for (final l in c.lines)
      CartLine(
        productId: l.productId,
        name: l.name,
        price: l.price,
        qty: l.qty,
        isWeight: l.isWeight,
        unit: l.unit,
        custom: l.custom,
        discount: l.discount,
        retailPrice: l.retailPrice,
        wholesalePrice: l.wholesalePrice,
        wholesale: l.wholesale,
        variantId: l.variantId,
        promo: l.promo,
      ),
  ],
);

/// Отпечаток состояния чека: по нему видно, что чек менялся после действия ИИ (вручную) — тогда откат снимком затёр бы
/// чужую правку, и его не делаем.
String cartSignature(Cart c) =>
    '${c.orderDiscountPercent}|${c.orderDiscountSum}|${[for (final l in c.lines) '${l.productId}:${l.qty}:${l.price}:${l.discount}:${l.wholesale}:${l.variantId ?? ''}'].join(',')}';
