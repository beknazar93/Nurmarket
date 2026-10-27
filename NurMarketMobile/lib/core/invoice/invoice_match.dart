/// 2026-10-27: сопоставление разобранной накладной ([InvoiceParser]) со складом, справочником NurCRM и поставщиками,
/// защита от повторной загрузки. Склад — база телефона (как скан и приёмка), сеть нужна только для поставщиков.
library;

import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';

import '../api/api_client.dart';
import '../catalog/global_products.dart';
import '../db/database.dart';
import 'invoice_parser.dart';

// ----------------------------------------------------------------------------------------------- названия

/// Название для сравнения: нижний регистр, ё=е, без знаков препинания, одиночные пробелы.
String invoiceNameKey(String s) => s
    .toLowerCase()
    .replaceAll('ё', 'е')
    .replaceAll(RegExp(r'[^\p{L}\p{N}\s]', unicode: true), ' ')
    .replaceAll(RegExp(r'\s+'), ' ')
    .trim();

double _dice(String a, String b) {
  if (a.isEmpty || b.isEmpty) return 0;
  if (a == b) return 1;
  if (a.length < 2 || b.length < 2) return 0;
  final ma = <String, int>{};
  for (var i = 0; i < a.length - 1; i++) {
    final g = a.substring(i, i + 2);
    ma[g] = (ma[g] ?? 0) + 1;
  }
  var hit = 0;
  for (var i = 0; i < b.length - 1; i++) {
    final g = b.substring(i, i + 2);
    final c = ma[g] ?? 0;
    if (c > 0) {
      hit++;
      ma[g] = c - 1;
    }
  }
  return 2 * hit / ((a.length - 1) + (b.length - 1));
}

/// Похожесть двух названий 0..1: биграммы без пробелов и с отсортированными словами (порядок слов не важен); если числа в
/// названиях разные («2л» и «1л», «15.6» и «17») — оценка снижается.
double nameSimilarity(String a, String b) {
  final ka = invoiceNameKey(a), kb = invoiceNameKey(b);
  if (ka.isEmpty || kb.isEmpty) return 0;
  String sorted(String k) => (k.split(' ')..sort()).join();
  var s = max(_dice(ka.replaceAll(' ', ''), kb.replaceAll(' ', '')), _dice(sorted(ka), sorted(kb)));
  Set<String> nums(String k) => RegExp(r'\d+').allMatches(k).map((m) => m.group(0)!).toSet();
  final na = nums(ka), nb = nums(kb);
  if (na.isNotEmpty && nb.isNotEmpty && (na.difference(nb).isNotEmpty || nb.difference(na).isNotEmpty)) s *= 0.6;
  return s;
}

// ----------------------------------------------------------------------------------------------- товары склада

enum MatchKind { barcode, article, name, fuzzy, none }

class LineMatch {
  final MatchKind kind;

  /// Найденный товар (для fuzzy — кандидат).
  final Product? product;
  final double score;

  /// Название/категория из справочника NurCRM (для товара, которого нет на складе).
  final GlobalProduct? global;

  /// 2026-10-27 (ревью Ск-13): оценка второго по похожести товара — «Winston Blue» против «Winston Blue SS» не выбирается сам.
  final double second;
  const LineMatch(this.kind, {this.product, this.score = 0, this.global, this.second = 0});

  /// Похожий товар подставляется сам: оценка ≥ [fuzzyAuto] и он оторвался от второго кандидата не меньше чем на [fuzzyGap].
  bool get autoPick => kind == MatchKind.fuzzy && score >= fuzzyAuto && score - second >= fuzzyGap;
}

/// Порог, с которого похожий товар предлагается; с которого подставляется сам (иначе по умолчанию — новый товар).
const fuzzyOffer = 0.75;
const fuzzyAuto = 0.9;

/// Минимальный отрыв лучшего «похожего» от второго для автоподстановки (ревью Ск-13).
const fuzzyGap = 0.1;

class InvoiceMatcher {
  final AppDatabase db;
  final GlobalProducts? catalog;
  InvoiceMatcher(this.db, [this.catalog]);

  /// Штрихкод → артикул → название (точное, без регистра и знаков) → нечёткое.
  Future<LineMatch> find(InvoiceLine l) async {
    if (l.barcode.isNotEmpty) {
      final variants = invoiceCodeVariants(l.barcode);
      final byCode = await db.productsByBarcodes(variants);
      for (final v in variants) {
        final p = byCode[v];
        if (p != null) return LineMatch(MatchKind.barcode, product: p, score: 1);
      }
    }
    if (l.article.isNotEmpty) {
      final p = (await db.productsByBarcodes([l.article]))[l.article];
      if (p != null) return LineMatch(MatchKind.article, product: p, score: 1);
    }
    final name = l.name.trim();
    if (name.isNotEmpty) {
      final key = invoiceNameKey(name);
      final rows = await db.searchProducts(name, limit: 40);
      for (final p in rows) {
        if (p.name.trim() == name) return LineMatch(MatchKind.name, product: p, score: 1);
      }
      final flat = key.replaceAll(' ', '');
      for (final p in rows) {
        if (invoiceNameKey(p.name).replaceAll(' ', '') == flat) return LineMatch(MatchKind.name, product: p, score: 1);
      }
      // нечётко: товары, где встречается хотя бы одно из длинных слов названия
      final pool = <String, Product>{for (final p in rows) p.id: p};
      final words = key.split(' ').where((w) => w.length >= 3 && !RegExp(r'^\d+$').hasMatch(w)).toList()..sort((a, b) => b.length.compareTo(a.length));
      for (final w in words.take(3)) {
        for (final p in await db.searchProducts(w, limit: 40)) {
          pool[p.id] = p;
        }
      }
      Product? best;
      var bestScore = 0.0, secondScore = 0.0;
      for (final p in pool.values) {
        if (p.isService) continue;
        // то же название с другим регистром, знаками и пробелами («Кока-Кола 2 л» = «кока кола 2л»)
        if (invoiceNameKey(p.name).replaceAll(' ', '') == flat) return LineMatch(MatchKind.name, product: p, score: 1);
        final s = nameSimilarity(name, p.name);
        if (s > bestScore) {
          secondScore = bestScore;
          bestScore = s;
          best = p;
        } else if (s > secondScore) {
          secondScore = s;
        }
      }
      if (best != null && bestScore >= fuzzyOffer) return LineMatch(MatchKind.fuzzy, product: best, score: bestScore, second: secondScore);
    }
    // нового товара подсказка из встроенного справочника (без сети, быстро)
    GlobalProduct? g;
    if (l.barcode.isNotEmpty && catalog != null) g = await catalog!.local(l.barcode);
    return LineMatch(MatchKind.none, global: g);
  }
}

// ----------------------------------------------------------------------------------------------- поставщики

class SupplierRef {
  final String id;
  final String name;
  final String inn;
  const SupplierRef(this.id, this.name, [this.inn = '']);
}

enum SupplierBy { inn, name, none }

Future<List<SupplierRef>> fetchSuppliers(ApiClient api) async {
  final data = await api.get('api/main/clients/', query: {'type': 'suppliers', 'page_size': 500});
  return [
    for (final m in asList(data))
      if ((str(m, 'id') ?? '').isNotEmpty)
        SupplierRef(
          str(m, 'id')!,
          str(m, 'full_name') ?? str(m, 'name') ?? str(m, 'llc') ?? str(m, 'phone') ?? '',
          (str(m, 'inn') ?? '').replaceAll(RegExp(r'\D'), ''),
        ),
  ];
}

/// Новый поставщик — тело как «+ Создать поставщика» кассы (full_name, phone, type=suppliers, + inn).
Future<SupplierRef> createSupplier(ApiClient api, {required String name, String phone = '', String inn = ''}) async {
  final body = {
    'full_name': name.trim(),
    'phone': phone.trim(),
    'type': 'suppliers',
    if (inn.trim().isNotEmpty) 'inn': inn.trim(),
  };
  final m = ((await api.post('api/main/clients/', body: body)) as Map).cast<String, dynamic>();
  return SupplierRef(str(m, 'id') ?? '', str(m, 'full_name') ?? name.trim(), str(m, 'inn') ?? inn.trim());
}

/// Название организации без «ООО», «ОсОО», «ИП», кавычек и регистра.
String supplierNameKey(String s) {
  var k = invoiceNameKey(s);
  k = k.replaceAll(RegExp(r'(^| )(ооо|осоо|оао|зао|ао|тоо|ип|чп|ooo|llc|ltd|inc|co|mchj|xk|ok)( |$)'), ' ');
  return k.replaceAll(RegExp(r'\s+'), ' ').trim();
}

/// Поставщик из списка по ИНН, затем по названию (одно совпадение; если названию отвечают несколько — не угадываем).
({SupplierRef? supplier, SupplierBy by}) matchSupplier(List<SupplierRef> list, {required String name, required String inn}) {
  final digits = inn.replaceAll(RegExp(r'\D'), '');
  if (digits.isNotEmpty) {
    final byInn = list.where((s) => s.inn == digits).toList();
    if (byInn.isNotEmpty) return (supplier: byInn.first, by: SupplierBy.inn);
  }
  final key = supplierNameKey(name);
  if (key.length >= 3) {
    final exact = list.where((s) => supplierNameKey(s.name) == key).toList();
    if (exact.length == 1) return (supplier: exact.first, by: SupplierBy.name);
    if (exact.isEmpty) {
      final part = list.where((s) {
        final k = supplierNameKey(s.name);
        return k.length >= 4 && (k.contains(key) || key.contains(k));
      }).toList();
      if (part.length == 1) return (supplier: part.first, by: SupplierBy.name);
    }
  }
  return (supplier: null, by: SupplierBy.none);
}

// ----------------------------------------------------------------------------------------------- защита от дубля

/// «Накладная уже загружалась». 2026-10-27 (ревью Ск-2): ключи НЕ зависят от поставщика в окне (раньше смена «без поставщика» →
/// «＋ создан» меняла ключ, и та же накладная проходила второй раз — приход и долг удваивались). Формула ТА ЖЕ, что у кассы
/// (NurMarketKassa.Infrastructure/Services/InvoiceImport/InvoiceImportRegistry.cs, Keys). Значения — нижний регистр, обрезаны по
/// краям; sha256 hex строчными (UTF-8): num — номер, d — «Г-М-Д» без нулей или пусто, inn — цифры ИНН из ФАЙЛА (9–14 цифр) или
/// пусто, tot — итог файла в тыйынах (round(100·итог)) или пусто, если итога нет или он ≤ 0.
///   K1 = sha256("file|" + sha256-файла)               — всегда;
///   K2 = sha256("doc|" + num + "|" + d + "|" + inn)    — если num не пусто и (inn не пусто или tot пусто);
///   K3 = sha256("sum|" + num + "|" + d + "|" + tot)    — если num не пусто и tot не пусто.
/// Загружена, если совпал ЛЮБОЙ ключ. Проведённые хранятся в настройках телефона (JSON, 300 последних, схема базы не менялась);
/// добавленные в приход, но ещё не проведённые — в памяти до закрытия приложения.
class InvoiceGuard {
  InvoiceGuard._();

  static const setting = 'invoice.loaded';
  static const keep = 300;
  static final Set<String> _pending = {};

  static String _h(String s) => sha256.convert(utf8.encode(s)).toString();

  /// ИНН как у кассы (`InvoiceText.CleanInn`): только цифры, 9–14 знаков, иначе пусто.
  static String cleanInn(String raw) {
    final digits = raw.replaceAll(RegExp(r'\D'), '');
    return digits.length >= 9 && digits.length <= 14 ? digits : '';
  }

  /// Ключи накладной K1/K2/K3 (формула — в описании класса); от поставщика в окне не зависят.
  static List<String> keys(ParsedInvoice inv) {
    final num = inv.number.trim().toLowerCase();
    final d = inv.date == null ? '' : '${inv.date!.year}-${inv.date!.month}-${inv.date!.day}';
    final inn = cleanInn(inv.supplierInn);
    final t = inv.fileTotal;
    final tot = t != null && t > 0 ? (t * 100).round().toString() : '';
    return [
      _h('file|${inv.fileHash.toLowerCase()}'),
      if (num.isNotEmpty && (inn.isNotEmpty || tot.isEmpty)) _h('doc|$num|$d|$inn'),
      if (num.isNotEmpty && tot.isNotEmpty) _h('sum|$num|$d|$tot'),
    ];
  }

  /// Ключи прежней формулы (с поставщиком) — только чтобы узнать накладную, проведённую до этого обновления.
  static List<String> legacyKeys(ParsedInvoice inv, String supplierKey) {
    final sup = supplierKey.trim().toLowerCase();
    final num = inv.number.trim().toLowerCase();
    final d = inv.date == null ? '' : '${inv.date!.year}-${inv.date!.month}-${inv.date!.day}';
    return [
      _h('file|${inv.fileHash}|$sup|$num'),
      if (num.isNotEmpty) _h('doc|$sup|$num|$d'),
    ];
  }

  static Future<List<Map<String, dynamic>>> _load(AppDatabase db) async {
    try {
      final raw = await db.getSetting(setting);
      if (raw == null || raw.isEmpty) return [];
      return [for (final e in jsonDecode(raw) as List) (e as Map).cast<String, dynamic>()];
    } catch (_) {
      return [];
    }
  }

  /// null — не загружалась; 'posted' — уже проведена; 'pending' — добавлена в приход, который ещё не проведён.
  static Future<String?> status(AppDatabase db, List<String> keys) async {
    for (final e in await _load(db)) {
      final ks = (e['k'] as List?)?.cast<String>() ?? const [];
      if (ks.any(keys.contains)) return 'posted';
    }
    return keys.any(_pending.contains) ? 'pending' : null;
  }

  /// Накладная ушла в приход (ещё не проведён).
  static void markPending(List<String> keys) => _pending.addAll(keys);

  /// Накладную не стали добавлять в приход — отметка «в приходе» снимается.
  static void unmarkPending(List<String> keys) => _pending.removeAll(keys);

  /// Накладная уже ПРОВЕДЕНА (только проведённые, без «в приходе») — для проверки прямо перед проведением прихода.
  static Future<bool> isPosted(AppDatabase db, List<String> keys) async => (await status(db, keys)) == 'posted';

  /// Приход проведён — накладная считается загруженной. 2026-10-27 (ревью Ск-2): ставится уже при ПЕРВОМ проведённом пункте
  /// (частичное проведение тоже), повторный вызов запись не дублирует.
  static Future<void> markPosted(AppDatabase db, List<String> keys, {String number = '', String supplier = ''}) async {
    _pending.removeAll(keys);
    final list = await _load(db);
    if (list.any((e) => ((e['k'] as List?)?.cast<String>() ?? const []).any(keys.contains))) return;
    list.add({'k': keys, 'at': DateTime.now().toIso8601String(), 'n': number, 's': supplier});
    final tail = list.length > keep ? list.sublist(list.length - keep) : list;
    await db.setSetting(setting, jsonEncode(tail));
  }

  /// Для тестов.
  static void resetPending() => _pending.clear();
}
