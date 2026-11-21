import '../../core/db/database.dart';

// 2026-11-20 (владелец: «если у товара в накладной нет штрихкода — при нажатии на товар сразу при добавлении сканировать штрихкод
// товара и привязать его»): чистая логика привязки штрихкода к строке накладной умной камеры — без камеры и экрана (проверяется
// тестами). Сам сканер — BarcodeBindPage (режим «по очереди»), запись на сервер — StockRepository.queueBarcodeBind.

/// У товара из каталога уже есть штрихкод (основной или дополнительный).
bool productHasCode(Product p) => (p.barcode ?? '').trim().isNotEmpty || p.altBarcodes.split('\n').any((l) => l.split('|').first.trim().isNotEmpty);

/// 2026-11-21 (ответ сервера, Умар): в `alternate_barcodes` и POST `products/{id}/barcodes/` поле `quantity` — «штук в упаковке»: целое > 0
/// или не передаётся (0 даёт ошибку); название ≤ 255, штрихкод ≤ 64. Одно место сборки тела на всё приложение.
Map<String, Object> altBarcodeBody(String barcode, {String name = '', num? qty}) {
  String cut(String s, int n) => s.length > n ? s.substring(0, n) : s;
  final n = qty == null ? 0 : qty.round();
  return {'barcode': cut(barcode.trim(), 64), 'name': cut(name.trim(), 255), if (n > 0) 'quantity': n};
}

/// Куда записать считанный код у существующего товара.
enum BindPlan {
  /// У товара нет основного кода — код становится основным.
  main,

  /// Основной код уже есть (другой) — считанный добавляется дополнительным (упаковка, второй вариант этикетки).
  alt,

  /// Код уже есть у этого товара — записывать нечего.
  already,
}

/// Один и тот же код может быть записан по-разному (ведущие нули, пробелы) — сравниваем по цифрам без ведущих нулей.
String sameCodeKey(String s) {
  final t = s.trim();
  if (t.isEmpty || !RegExp(r'^\d+$').hasMatch(t)) return t.toLowerCase();
  return t.replaceFirst(RegExp(r'^0+'), '');
}

BindPlan planBarcodeBind(Product p, String code) {
  final key = sameCodeKey(code);
  final codes = [(p.barcode ?? ''), ...p.altBarcodes.split('\n').map((l) => l.split('|').first)];
  if (codes.any((c) => c.trim().isNotEmpty && sameCodeKey(c) == key)) return BindPlan.already;
  return (p.barcode ?? '').trim().isEmpty ? BindPlan.main : BindPlan.alt;
}

/// Чем считанный код мешает: ничем, он уже у самого товара строки, у другого товара каталога или у другой строки этой накладной.
enum BindConflictKind { none, own, catalog, draft }

class BindConflict {
  final BindConflictKind kind;

  /// Товар каталога, у которого уже есть код ([BindConflictKind.catalog] / [BindConflictKind.own]).
  final Product? product;

  /// Название другой строки накладной ([BindConflictKind.draft]).
  final String? rowName;
  const BindConflict(this.kind, {this.product, this.rowName});
}

/// Код уникален в компании: сначала смотрим другие строки этой накладной (их коды ещё не на сервере и в базе телефона нет),
/// потом каталог. [catalogOwner] — товар каталога с этим кодом (null — свободен), [rowProductId] — товар строки (null — новый),
/// [draftRowName] — название другой строки накладной с тем же кодом (null — нет).
BindConflict checkBindConflict({Product? catalogOwner, String? rowProductId, String? draftRowName}) {
  if (draftRowName != null) return BindConflict(BindConflictKind.draft, rowName: draftRowName);
  if (catalogOwner == null) return const BindConflict(BindConflictKind.none);
  if (rowProductId != null && catalogOwner.id == rowProductId) return BindConflict(BindConflictKind.own, product: catalogOwner);
  return BindConflict(BindConflictKind.catalog, product: catalogOwner);
}

/// Дрожание камеры и «тот же код ещё в кадре»: один и тот же код, виденный менее [window] назад, не считается новым. Время
/// «последний раз видели» продлевается с каждым кадром — пока код в кадре, он не пройдёт вторым разом (как «камеру отвели» в
/// RepeatScanGuard, но без вопросов: для привязки повтор — всегда лишний).
class CodeDebounce {
  final Duration window;
  final DateTime Function() _clock;
  String? _code;
  DateTime _seen = DateTime(2000);
  CodeDebounce({this.window = const Duration(milliseconds: 1500), DateTime Function()? clock}) : _clock = clock ?? DateTime.now;

  /// true — код новый, обрабатывать; false — повтор, игнорировать.
  bool accept(String code) {
    final now = _clock();
    final repeat = code == _code && now.difference(_seen) < window;
    _code = code;
    _seen = now;
    return !repeat;
  }

  void reset() => _code = null;
}

/// Нужен ли строке накладной штрихкод: у товара из каталога его нет совсем, у нового — ни из накладной (с верной контрольной цифрой),
/// ни привязанного сканером.
bool rowNeedsCode({Product? product, required bool seenCodeValid, String? boundCode}) {
  if (boundCode != null && boundCode.isNotEmpty) return false;
  if (product != null) return !productHasCode(product);
  return !seenCodeValid;
}
