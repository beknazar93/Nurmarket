import '../../core/ai/vision.dart';

// 2026-11-20 (владелец: «при загрузке накладных через наш ИИ создавай один товар и доп. штрихкоды: есть «Асу» и «Асу со вкусом
// клубники» — это один товар, цена одна, марка одна, отличается только вкус»). Чистая логика без экрана и сети (проверяется тестами):
// ИИ отдаёт у каждой строки базовое название (base), вариант (variant), бренд; строки с одним base+брендом, близкой ценой и разными
// вариантами сливаются в ОДНУ позицию [SeenItem.variants]; код первого варианта — основной, остальные — дополнительные с названием
// варианта; количество каждого варианта хранится отдельно, а в приход идёт сумма.

/// Слово для сравнения: нижний регистр, ё = е, без кавычек и лишних пробелов.
String groupNorm(String s) => s.toLowerCase().replaceAll('ё', 'е').replaceAll(RegExp('[«»"\'`]'), ' ').replaceAll(RegExp(r'\s+'), ' ').trim();

/// Ключ группы: базовое название (если ИИ его не дал — название строки). Пустое название — null. Бренд сверяется отдельно
/// ([brandsCompatible]): ИИ мог назвать его не у всех строк.
String? groupKeyOf(SeenItem s) {
  final base = groupNorm(s.base.isNotEmpty ? s.base : s.name);
  return base.isEmpty ? null : base;
}

/// Бренды позиций не противоречат: названы не больше чем один разный (пустой — «не назван»).
bool brandsCompatible(Iterable<SeenItem> items) => {for (final s in items) if (groupNorm(s.brand).isNotEmpty) groupNorm(s.brand)}.length <= 1;

/// Цены «одинаковые»: разница не больше 0,5 сома и не больше 2 %. Нулевая цена (не прочитана) не мешает.
bool pricesClose(double a, double b) {
  if (a <= 0 || b <= 0) return true;
  final d = (a - b).abs();
  final lo = a < b ? a : b;
  return d <= 0.5 || d <= lo * 0.02;
}

/// Цены всех позиций близки друг к другу.
bool allPricesClose(Iterable<double> prices) {
  final p = [for (final x in prices) if (x > 0) x];
  if (p.length < 2) return true;
  p.sort();
  return pricesClose(p.first, p.last);
}

/// Варианты позиции: у слитой — её список, у обычной строки — один вариант (свой вариант, штрихкод и количество).
List<SeenVariant> variantsOf(SeenItem s) => s.variants.isNotEmpty
    ? s.variants
    : [SeenVariant(label: s.variant, barcode: s.barcode, qty: s.amount)];

/// Одинаковый вариант: то же название варианта и тот же штрихкод (или код у одного из них не указан).
bool _sameVariant(SeenVariant a, SeenVariant b) {
  if (groupNorm(a.label) != groupNorm(b.label)) return false;
  return a.barcode.isEmpty || b.barcode.isEmpty || a.barcode == b.barcode;
}

/// Слить позиции [items] в одну: название — базовое, бренд и единица — первые непустые, цена — первая ненулевая, количество — сумма.
/// Повторы того же варианта (та же накладная на двух фото) складываются по количеству.
SeenItem mergeItems(List<SeenItem> items) {
  final vars = <SeenVariant>[];
  for (final it in items) {
    for (final v in variantsOf(it)) {
      final i = vars.indexWhere((x) => _sameVariant(x, v));
      if (i >= 0) {
        vars[i] = SeenVariant(label: vars[i].label.isEmpty ? v.label : vars[i].label, barcode: vars[i].barcode.isEmpty ? v.barcode : vars[i].barcode, qty: vars[i].qty + v.qty);
      } else {
        vars.add(v);
      }
    }
  }
  final first = items.first;
  String pick(String Function(SeenItem s) f) => items.map(f).firstWhere((x) => x.trim().isNotEmpty, orElse: () => '');
  // название: базовое; нет — самое короткое из названий (в нём нет вкуса)
  var name = pick((s) => s.base);
  if (name.isEmpty) {
    final names = [for (final s in items) if (s.name.trim().isNotEmpty) s.name.trim()]..sort((a, b) => a.length.compareTo(b.length));
    name = names.isEmpty ? first.name : names.first;
  }
  final sizes = {for (final s in items) groupNorm(s.size)};
  final total = vars.fold<double>(0, (a, v) => a + v.qty);
  final price = items.map((s) => s.labelPrice).firstWhere((p) => p > 0, orElse: () => 0);
  return SeenItem(
    name: name,
    base: name,
    brand: pick((s) => s.brand),
    size: sizes.length == 1 ? first.size : '',
    barcode: vars.map((v) => v.barcode).firstWhere((b) => b.isNotEmpty, orElse: () => ''),
    count: total.round().clamp(1, 99999),
    qty: total == total.roundToDouble() ? null : total,
    unit: pick((s) => s.unit),
    labelPrice: price,
    variants: vars.length >= 2 ? vars : const [],
  );
}

/// Сгруппировать строки накладной. [merge] = false («не объединяй») — строки остаются как есть. Порядок — по первой строке группы.
/// Не сливаются: разные бренды (названные у разных строк) или базовые названия; разные цены (их спрашивают у человека, см. [priceConflicts]); строки без вариантов
/// (одно и то же название с разными кодами — это разные товары). Повтор той же строки (тот же вариант и код) складывается по количеству.
List<SeenItem> groupInvoiceItems(List<SeenItem> items, {bool merge = true}) {
  if (!merge) return List.of(items);
  final buckets = <String, List<int>>{};
  for (var i = 0; i < items.length; i++) {
    final k = groupKeyOf(items[i]);
    if (k != null) (buckets[k] ??= []).add(i);
  }
  final out = <SeenItem?>[...items];
  for (final idx in buckets.values) {
    if (idx.length < 2) continue;
    final members = [for (final i in idx) items[i]];
    if (!brandsCompatible(members)) continue; // разные бренды — разные товары
    final labels = {for (final m in members) for (final v in variantsOf(m)) groupNorm(v.label)};
    final sameRow = members.every((m) => m.variants.isEmpty) && labels.length == 1; // один и тот же вариант несколько раз
    if (labels.length < 2 && !sameRow) continue;
    if (!allPricesClose(members.map((m) => m.labelPrice))) continue; // цены разные — не объединяем сами
    if (sameRow && members.map((m) => m.barcode).where((b) => b.isNotEmpty).toSet().length > 1) continue; // разные коды без вариантов — разные товары
    out[idx.first] = mergeItems(members);
    for (final i in idx.skip(1)) {
      out[i] = null;
    }
  }
  return [for (final s in out) ?s];
}

/// Группы, которые не слиты только из-за разницы в цене: ключ → позиции (≥ 2 с разными вариантами). Для вопроса «Объединить? Цены разные».
Map<String, List<SeenItem>> priceConflicts(List<SeenItem> items) {
  final buckets = <String, List<SeenItem>>{};
  for (final s in items) {
    final k = groupKeyOf(s);
    if (k != null) (buckets[k] ??= []).add(s);
  }
  buckets.removeWhere((k, v) {
    if (v.length < 2 || !brandsCompatible(v)) return true;
    final labels = {for (final m in v) for (final x in variantsOf(m)) groupNorm(x.label)};
    return labels.length < 2 || allPricesClose(v.map((m) => m.labelPrice));
  });
  return buckets;
}

/// «Своя команда ИИ»: true — просит объединять, false — не объединять, null — про это ничего не сказано.
bool? commandWantsGroup(String command) {
  final t = groupNorm(command);
  if (t.isEmpty) return null;
  if (RegExp(r'не (объединя|группир|склады)|без (объединени|группиров)|отдельн(ые|ыми|ыми товар)').hasMatch(t)) return false;
  if (RegExp(r'объедин|группир|один товар').hasMatch(t)) return true;
  return null;
}

/// Настройки ИИ для накладной (экран «Умная камера»): переключатель «Объединять вкусы/варианты в один товар» и «Своя команда ИИ».
class InvoiceAiOptions {
  final bool group;
  final String command;
  const InvoiceAiOptions({this.group = true, this.command = ''});

  /// Объединять ли на самом деле: слова в команде («не объединяй», «объединяй вкусы») главнее переключателя.
  bool get merge => effectiveGroup(toggle: group, command: command);
}

/// Итоговое решение: слова в команде главнее переключателя.
bool effectiveGroup({required bool toggle, String command = ''}) => commandWantsGroup(command) ?? toggle;

/// Сколько всего вариантов и дополнительных кодов: для строки «товаров: N, вариантов/доп. кодов: M».
({int products, int extra}) groupTotals(Iterable<SeenItem> items) {
  var n = 0, extra = 0;
  for (final s in items) {
    n++;
    if (s.variants.length >= 2) extra += s.variants.length - 1;
  }
  return (products: n, extra: extra);
}
