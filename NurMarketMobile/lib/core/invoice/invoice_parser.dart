/// 2026-10-27 (владелец: «в мобилке и в десктопе добавь возможность загрузки электронных накладных»): разбор файла
/// накладной поставщика — Excel (.xlsx), CSV/TSV, XML (электронная счёт-фактура / накладная произвольной структуры и
/// CommerceML). Чистый Dart: без интерфейса, без сети, без базы — разбор отдельно от экрана и проверяется тестами.
///
/// ФОРМАТ (одинаковый на десктопе и в мобилке):
///  * лимиты: файл до 10 МБ, до 5000 строк товаров; XML без DOCTYPE/ENTITY (защита от внешних сущностей), вложенность до 100;
///  * таблица: строка заголовка ищется среди первых 60 строк по названиям столбцов (ru/ky/en/uz), столбцы — в любом порядке;
///  * шапка свободным текстом «Ключ: значение» над таблицей: номер, дата, поставщик, ИНН (покупатель не путается с поставщиком),
///    также «Накладная № 12 от 05.10.2026» без двоеточия; дата — 27.10.2026, 2026-10-27, «27 октября 2026 г.» (ru/ky/uz/en);
///  * закупка = «Цена за ед.»; сумма строки — «Сумма без НДС» / «Всего с НДС»; НДС по умолчанию без НДС (переключатель в экране);
///  * итог файла: строка «Итого/Всего…» — число в ячейке суммы, число в тексте или сумма прописью (ru);
///  * валюта (руб./тенге/доллар/сом) только определяется, цены не пересчитываются.
library;

import 'dart:convert';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:xml/xml.dart';

import '../../features/scan/barcode_lookup.dart';
import '../catalog/product_matcher.dart' show isValidEan;

enum InvoiceFormat { xlsx, csv, xml }

enum InvoiceError { tooBig, empty, pdf, image, xls, unsupported, broken, noTable, unsafeXml, tooMany }

class InvoiceParseException implements Exception {
  final InvoiceError error;
  final String detail;
  const InvoiceParseException(this.error, [this.detail = '']);
  @override
  String toString() => 'InvoiceParseException($error${detail.isEmpty ? '' : ': $detail'})';
}

/// Почему строка не годится к приходу.
enum InvoiceLineIssue { noName, noQty }

class InvoiceLine {
  /// Номер строки в файле (с 1) — для сообщений.
  final int row;
  final String name;

  /// Штрихкод, только цифры (6–14); пусто — в файле его нет.
  final String barcode;

  /// Артикул/код поставщика.
  final String article;
  final String unit;
  final double qty;

  /// Закупочная цена за единицу как в файле (без НДС, если файл различает).
  final double cost;

  /// Цена за единицу с НДС (0 — в файле нет).
  final double costGross;

  /// Сумма строки без НДС (0 — нет).
  final double sum;

  /// Сумма строки с НДС (0 — нет).
  final double sumGross;

  /// Ставка НДС в процентах; -1 — в файле не указана.
  final double vat;

  /// Розничная цена из файла (0 — нет).
  final double retail;

  /// 2026-10-27 (ревью Ск-12): в файле были только цены «с НДС» — закупка посчитана без НДС по ставке строки (ставки нет — взята
  /// цена с НДС как есть). Экран предупреждает: закупку надо проверить.
  final bool costFromGross;
  const InvoiceLine({
    required this.row,
    this.name = '',
    this.barcode = '',
    this.article = '',
    this.unit = '',
    this.qty = 0,
    this.cost = 0,
    this.costGross = 0,
    this.sum = 0,
    this.sumGross = 0,
    this.vat = -1,
    this.retail = 0,
    this.costFromGross = false,
  });

  InvoiceLineIssue? get issue => name.trim().isEmpty && barcode.isEmpty && article.isEmpty
      ? InvoiceLineIssue.noName
      : qty <= 0
      ? InvoiceLineIssue.noQty
      : null;
  bool get valid => issue == null;

  /// Закупка за единицу: без НДС или с НДС (если в файле нет цены с НДС — из суммы с НДС или по ставке).
  double unitCost({bool withVat = false}) {
    if (!withVat) return cost;
    if (costGross > 0) return costGross;
    if (sumGross > 0 && qty > 0) return _round(sumGross / qty, 4);
    return vat > 0 ? _round(cost * (1 + vat / 100), 4) : cost;
  }

  /// Сумма строки.
  double lineSum({bool withVat = false}) {
    if (withVat) {
      if (sumGross > 0) return sumGross;
      return _round(qty * unitCost(withVat: true), 2);
    }
    return sum > 0 ? sum : _round(qty * cost, 2);
  }
}

class ParsedInvoice {
  final InvoiceFormat format;
  final String number;
  final DateTime? date;
  final String supplierName;
  final String supplierInn;
  final List<InvoiceLine> lines;

  /// Итог из файла (null — не найден).
  final double? fileTotal;

  /// KGS, RUB, KZT, USD, EUR, UZS; пусто — не определена.
  final String currency;

  /// SHA-256 файла (hex) — защита от повторной загрузки.
  final String fileHash;
  const ParsedInvoice({
    required this.format,
    this.number = '',
    this.date,
    this.supplierName = '',
    this.supplierInn = '',
    required this.lines,
    this.fileTotal,
    this.currency = '',
    this.fileHash = '',
  });

  double linesTotal({bool withVat = false}) => lines.where((l) => l.valid).fold(0.0, (s, l) => s + l.lineSum(withVat: withVat));

  /// 2026-10-27 (ревью Ск-12): у каких-то строк закупка посчитана из цены «с НДС» (в файле нет цены без НДС).
  bool get costFromGross => lines.any((l) => l.valid && l.costFromGross);

  /// В файле есть НДС сверх цены без НДС (тогда экрану нужен переключатель «с НДС»).
  bool get hasVat => lines.any((l) => l.costGross > l.cost + 0.0001 || l.sumGross > l.sum + 0.005 || l.vat > 0);
}

double _round(double v, int digits) {
  var m = 1.0;
  for (var i = 0; i < digits; i++) {
    m *= 10;
  }
  return (v * m).round() / m;
}

// ----------------------------------------------------------------------------------------------- числа, коды, даты

/// Число из ячейки накладной: пробелы, неразрывные пробелы, апострофы-разделители, запятая/точка, «1 234,56», «1,234.56»,
/// «12 %», «5 шт», «(12)», «−3». null — не число.
double? parseInvoiceNumber(String? raw) {
  if (raw == null) return null;
  var s = raw.trim();
  if (s.isEmpty) return null;
  s = s.replaceAll(RegExp('[     ​\']'), '').replaceAll('’', '').replaceAll('−', '-').replaceAll('–', '-');
  var neg = false;
  if (s.startsWith('(') && s.endsWith(')')) {
    neg = true;
    s = s.substring(1, s.length - 1);
  }
  if (RegExp(r'^\d{1,2}[./]\d{1,2}[./]\d{2,4}$').hasMatch(s)) return null; // дата, не число
  // хвост-единица или валюта: «5шт», «12%», «100сом», «45000руб.»
  final m = RegExp(r'^([+-]?[\d.,]+(?:[eE][+-]?\d+)?)\s*(?:%|[A-Za-zА-Яа-яЁё₽₸€$]+\.?)?$').firstMatch(s);
  if (m == null) return null;
  s = m.group(1)!;
  if (s.startsWith('+')) s = s.substring(1);
  if (s.startsWith('-')) {
    neg = !neg;
    s = s.substring(1);
  }
  if (s.isEmpty || !RegExp(r'\d').hasMatch(s)) return null;
  final hasExp = RegExp(r'[eE]').hasMatch(s);
  if (!hasExp) {
    final lastComma = s.lastIndexOf(','), lastDot = s.lastIndexOf('.');
    if (lastComma >= 0 && lastDot >= 0) {
      // последний из двух знаков — десятичный, другой — разделитель тысяч
      final dec = lastComma > lastDot ? ',' : '.';
      final th = dec == ',' ? '.' : ',';
      s = s.replaceAll(th, '').replaceAll(dec, '.');
    } else if (lastComma >= 0) {
      s = s.indexOf(',') != lastComma ? s.replaceAll(',', '') : s.replaceAll(',', '.');
    } else if (lastDot >= 0 && s.indexOf('.') != lastDot) {
      s = s.replaceAll('.', '');
    }
  }
  final v = double.tryParse(s);
  if (v == null || v.isNaN || v.isInfinite) return null;
  return neg ? -v : v;
}

/// Код из ячейки: «4870200221261», «4.870200221261E12», «4870200221261.0», «'4870…», «4870 2002 21261» → только цифры;
/// не цифровой код (артикул «AB-12») возвращается как есть. Excel теряет ведущие нули — это учитывает сопоставление
/// ([invoiceCodeVariants]).
String normalizeInvoiceCode(String? raw) {
  if (raw == null) return '';
  var s = raw.replaceAll(RegExp(r'[\x00-\x1F\x7F   ]'), ' ').trim();
  if (s.startsWith("'")) s = s.substring(1).trim();
  if (s.isEmpty) return '';
  final sci = s.replaceAll(',', '.'); // Excel на русской раскладке пишет «4,87E+12»
  if (RegExp(r'^\d+(\.\d+)?[eE]\+?\d+$').hasMatch(sci)) {
    final v = double.tryParse(sci);
    if (v != null && v < 1e15) return v.toStringAsFixed(0);
  }
  s = s.replaceAll(RegExp(r'\.0+$'), '');
  final compact = s.replaceAll(RegExp(r'[\s-]'), '');
  return RegExp(r'^\d+$').hasMatch(compact) ? compact : s;
}

/// 2026-10-27 (ревью Ск-16): Excel срезает ведущий 0 у UPC-A — 11 цифр. Если с нулём впереди (12 цифр) контрольная цифра верна, код
/// достраивается; иначе остаётся как был (не угадываем).
String restoreLostZero(String code) {
  if (!RegExp(r'^\d{11}$').hasMatch(code)) return code;
  final full = '0$code';
  return isValidEan(full) ? full : code;
}

/// Штрихкод — только цифры 6–14.
bool isBarcodeLike(String code) => RegExp(r'^\d{6,14}$').hasMatch(code);

/// Все написания кода для поиска: EAN/UPC-варианты кассы и версии с потерянными Excel'ем нулями впереди (9–11 цифр → 12/13).
List<String> invoiceCodeVariants(String code) {
  final c = normalizeInvoiceCode(code);
  if (c.isEmpty) return const [];
  final out = <String>[...barcodeVariants(c)];
  if (RegExp(r'^\d{9,11}$').hasMatch(c)) {
    for (final n in [12, 13]) {
      final p = c.padLeft(n, '0');
      if (!out.contains(p)) out.add(p);
    }
  }
  return out;
}

const _monthPrefixes = <String, int>{
  'январ': 1, 'янв': 1, 'жанвар': 1, 'yanvar': 1, 'jan': 1,
  'феврал': 2, 'фев': 2, 'fevral': 2, 'feb': 2,
  'март': 3, 'мар': 3, 'mart': 3, 'mar': 3,
  'апрел': 4, 'апр': 4, 'aprel': 4, 'apr': 4,
  'мая': 5, 'май': 5, 'may': 5,
  'июн': 6, 'iyun': 6, 'jun': 6,
  'июл': 7, 'iyul': 7, 'jul': 7,
  'август': 8, 'авг': 8, 'avgust': 8, 'aug': 8,
  'сентябр': 9, 'сен': 9, 'sentabr': 9, 'sep': 9,
  'октябр': 10, 'окт': 10, 'oktabr': 10, 'oct': 10,
  'ноябр': 11, 'ноя': 11, 'noyabr': 11, 'nov': 11,
  'декабр': 12, 'дек': 12, 'dekabr': 12, 'dec': 12,
};

DateTime? _ymd(int y, int m, int d) {
  if (y < 100) y += 2000;
  if (y < 1990 || y > 2100 || m < 1 || m > 12 || d < 1 || d > 31) return null;
  final dt = DateTime(y, m, d);
  return dt.month == m && dt.day == d ? dt : null;
}

/// Дата накладной: 27.10.2026, 27/10/26, 2026-10-27(T…), 20261027, «27 октября 2026 г.», «October 27, 2026», серийный номер Excel.
DateTime? parseInvoiceDate(String? raw) {
  if (raw == null) return null;
  final s = raw.trim().toLowerCase().replaceAll('ё', 'е');
  if (s.isEmpty) return null;
  var m = RegExp(r'(\d{4})[-./](\d{1,2})[-./](\d{1,2})').firstMatch(s);
  if (m != null) return _ymd(int.parse(m[1]!), int.parse(m[2]!), int.parse(m[3]!));
  m = RegExp(r'(\d{1,2})[-./](\d{1,2})[-./](\d{4}|\d{2})(?!\d)').firstMatch(s);
  if (m != null) return _ymd(int.parse(m[3]!), int.parse(m[2]!), int.parse(m[1]!));
  m = RegExp(r'^(\d{4})(\d{2})(\d{2})$').firstMatch(s);
  if (m != null) return _ymd(int.parse(m[1]!), int.parse(m[2]!), int.parse(m[3]!));
  // «27 октября 2026» / «27-okt-2026» / «october 27, 2026»
  final words = RegExp(r'[a-zа-я]+').allMatches(s).map((x) => x.group(0)!).toList();
  int? month;
  for (final w in words) {
    for (final e in _monthPrefixes.entries) {
      if (w.startsWith(e.key) && (e.key.length >= 3 || w == e.key)) {
        month = e.value;
        break;
      }
    }
    if (month != null) break;
  }
  if (month != null) {
    final nums = RegExp(r'\d+').allMatches(s).map((x) => x.group(0)!).toList();
    final year = nums.where((n) => n.length == 4).firstOrNull;
    final day = nums.where((n) => n.length <= 2).firstOrNull;
    if (year != null && day != null) return _ymd(int.parse(year), month, int.parse(day));
  }
  // серийная дата Excel
  final serial = double.tryParse(s);
  if (serial != null && serial > 30000 && serial < 80000) {
    return DateTime(1899, 12, 30).add(Duration(days: serial.floor()));
  }
  return null;
}

/// Единица измерения под выбор приёмки: шт / кг / л / упак; иное — как в файле (коротко), пусто — шт.
String normalizeInvoiceUnit(String? raw) {
  final u = (raw ?? '').trim().toLowerCase().replaceAll('.', '').replaceAll(RegExp(r'\s+'), ' ');
  if (u.isEmpty) return 'шт';
  if (const {'шт', 'штука', 'штук', 'pcs', 'pc', 'piece', 'дана', 'dona', 'ед', 'unit', 'units', '796'}.contains(u)) return 'шт';
  if (const {'кг', 'килограмм', 'kg', 'килограм', '166'}.contains(u)) return 'кг';
  if (const {'л', 'литр', 'l', 'lt', 'litr', 'liter', '112'}.contains(u)) return 'л';
  if (const {'упак', 'уп', 'упаковка', 'пач', 'пачка', 'pack', 'upak', 'коробка', 'кор', '778'}.contains(u)) return 'упак';
  return u.length <= 8 ? u : 'шт';
}

/// 2026-10-27 (ревью М-5 / Ск-11): единица — УПАКОВКА (коробка, упаковка, блок, ящик, пачка), а не штука. В накладной такая строка
/// считает упаковки по цене упаковки; на складе товар чаще ведётся в штуках.
bool isPackUnit(String? raw) {
  final u = (raw ?? '').trim().toLowerCase().replaceAll('.', '').replaceAll(RegExp(r'\s+'), ' ');
  return const {
    'упак', 'уп', 'упаковка', 'упаковки', 'пач', 'пачка', 'pack', 'package', 'upak', 'коробка', 'коробки', 'короб', 'кор', 'box', 'quti', 'блок', 'block',
    'ящик', 'ящ', 'yashik', 'ящики', '778',
  }.contains(u);
}

const _okei = {'796': 'шт', '166': 'кг', '112': 'л', '778': 'упак', '006': 'м', '6': 'м', '163': 'г', '625': 'лист', '839': 'компл', '704': 'набор'};

// ----------------------------------------------------------------------------------------------- названия столбцов

String _normLabel(String s) => s
    .toLowerCase()
    .replaceAll('ё', 'е')
    .replaceAll(RegExp("[ʻʼ’'`]"), '')
    .replaceAll(RegExp(r'[^\p{L}\p{N}\s]', unicode: true), ' ')
    .replaceAll(RegExp(r'\s+'), ' ')
    .trim();

/// Имя элемента XML / поля: camelCase и «ЦенаЗаЕдиницу» → слова.
String _normName(String s) => _normLabel(s.replaceAllMapped(RegExp(r'(?<=[a-zа-яё0-9])(?=[A-ZА-ЯЁ])'), (m) => ' ').replaceAll('_', ' '));

const _rawSynonyms = <String, List<String>>{
  'barcode': [
    'штрихкод', 'штрих код', 'шк', 'ш к', 'баркод', 'штрих', 'barcode', 'bar code', 'ean', 'ean 13', 'ean13', 'gtin', 'upc', 'штрих коду',
    'штрих код товара', 'shtrix kod', 'shtrixkod', 'barkod',
  ],
  'article': [
    'артикул', 'арт', 'артикул поставщика', 'код товара', 'код номенклатуры', 'каталожный номер', 'номенклатурный номер', 'sku', 'article',
    'vendor code', 'part number', 'part no', 'item code', 'product code', 'товардун коду', 'artikul', 'mahsulot kodi', 'идентификатор',
  ],
  'code': ['код'],
  'okei': ['код по океи', 'океи', 'код ед изм', 'код единицы', 'okei'],
  'name': [
    'наименование', 'наименование товара', 'наименование товара описание выполненных работ оказанных услуг', 'товар', 'товары', 'название',
    'название товара', 'номенклатура', 'описание', 'описание товара', 'продукция', 'товар работа услуга', 'наименование продукции', 'name',
    'product name', 'product', 'item', 'item name', 'description', 'goods', 'goods name', 'аталышы', 'товардун аталышы', 'аталыш', 'nomi',
    'nomlanishi', 'mahsulot nomi', 'tovar nomi', 'mahsulot', 'tovar', 'tovar nomlanishi', 'товар наименование',
  ],
  'unit': [
    'ед изм', 'ед', 'единица', 'единица измерения', 'единицы измерения', 'е и', 'unit', 'uom', 'unit of measure', 'бирдик', 'өлчөө бирдиги', 'бирдиги',
    'о б', 'olchov birligi', 'birlik', 'birligi', 'unit nomenclature', 'базовая единица',
  ],
  'qty': [
    'количество', 'кол во', 'колво', 'кол', 'к во', 'qty', 'quantity', 'саны', 'количество отпущено', 'отпущено', 'принято',
    'количество принято', 'soni', 'miqdor', 'miqdori',
  ],
  'cost': [
    'цена', 'цена закупки', 'закупочная цена', 'закупка', 'цена за единицу', 'цена за ед', 'цена без ндс', 'цена поставщика', 'цена опт',
    'оптовая цена', 'цена прихода', 'закупочная', 'баасы', 'бир бирдиктин баасы', 'сатып алуу баасы', 'бирдиктин баасы', 'price', 'unit price',
    'purchase price', 'cost', 'price without vat', 'price without tax', 'narx', 'narxi', 'birlik narxi', 'sotib olish narxi',
    'цена за единицу товара', 'цена в рублях', 'ценазаединицу',
  ],
  'costGross': [
    'цена с ндс', 'цена с учетом ндс', 'цена брутто', 'price with vat', 'price with tax', 'narxi qqs bilan', 'баасы ндс менен', 'цена с налогом',
  ],
  'sum': [
    'сумма', 'сумма без ндс', 'стоимость', 'стоимость без ндс', 'стоимость товаров', 'стоимость без учета ндс', 'итого', 'всего',
    'сумма по строке', 'суммасы', 'жалпы', 'жалпы суммасы', 'amount', 'total', 'line total', 'sum', 'summa', 'jami', 'qiymati', 'summasi',
    'стоимость товара', 'turnover size',
  ],
  'sumGross': [
    'сумма с ндс', 'всего с ндс', 'стоимость с ндс', 'сумма с учетом ндс', 'итого с ндс', 'всего с учетом ндс', 'сумма брутто',
    'total with vat', 'amount with vat', 'суммасы ндс менен', 'стоимость с учетом ндс',
  ],
  'vat': ['ндс', 'ставка ндс', 'ндс %', 'налог', 'ставка налога', 'ставка', 'vat', 'vat rate', 'tax', 'tax rate', 'qqs', 'qqs stavkasi', 'nds rate', 'nds'],
  'vatSum': ['сумма ндс', 'ндс сумма', 'сумма налога', 'налог сумма', 'vat amount', 'tax amount', 'qqs summasi', 'ндс суммасы', 'nds amount', 'sum nds'],
  'retail': [
    'розничная цена', 'цена розница', 'цена продажи', 'цена розничная', 'розница', 'продажа', 'сатуу баасы', 'retail price', 'sale price',
    'chakana narx', 'sotuv narxi',
  ],
  'discount': ['скидка', 'скидка %', 'discount', 'чегирма'],
  'idx': ['п п', 'номер п п', 'номер', 'no', 'n', 'nr', 'тартиб номери', 'т р', 'пп'],
};

final Map<String, List<String>> _syn = {
  for (final e in _rawSynonyms.entries) e.key: [for (final s in e.value) _normLabel(s)],
};

class _FieldHit {
  final String field;
  final int score;
  final int synLen;
  const _FieldHit(this.field, this.score, this.synLen);
}

/// Все поля, подходящие названию столбца/элемента, с оценкой: точное совпадение 3, начало слов 2, слова внутри короткого
/// названия 1 (только для синонимов от 4 букв; «код» — только точно).
List<_FieldHit> _hits(String normalized, {bool loose = true}) {
  final h = normalized;
  if (h.isEmpty) return const [];
  final tokens = h.split(' ').length;
  final out = <_FieldHit>[];
  for (final e in _syn.entries) {
    _FieldHit? best;
    for (final s in e.value) {
      if (s.isEmpty) continue;
      int score = 0;
      if (h == s) {
        score = 3;
      } else if (e.key != 'code' && s.length >= 3 && h.startsWith('$s ')) {
        score = 2;
      } else if (loose && e.key != 'code' && s.length >= 4 && tokens <= 8 && h.length <= 40 && ' $h '.contains(' $s ')) {
        score = 1;
      }
      if (score > 0 && (best == null || score > best.score || (score == best.score && s.length > best.synLen))) best = _FieldHit(e.key, score, s.length);
    }
    if (best != null) out.add(best);
  }
  return out;
}

const _tableFields = {'name', 'barcode', 'article', 'code', 'unit', 'qty', 'cost', 'costGross', 'sum', 'sumGross', 'vat', 'retail'};

/// Столбцы по заголовку: поле → индекс столбца. Каждый столбец и каждое поле — один раз.
Map<String, int> _columns(List<String> header) {
  final cand = <(int col, _FieldHit hit)>[];
  for (var i = 0; i < header.length; i++) {
    final raw = header[i].trim();
    if (raw.isEmpty) continue;
    if (raw == '№') {
      cand.add((i, const _FieldHit('idx', 3, 1)));
      continue;
    }
    final nl = _normLabel(raw);
    for (final h in _hits(nl)) {
      // 2026-10-27 (ревью Ск-10): «Сумма скидки» / «Цена со скидкой» — не сумма и не цена строки
      if (h.field != 'discount' && RegExp(r'скидк|discount|чегирма').hasMatch(nl)) continue;
      if (h.field == 'qty') {
        // ТОРГ-12: «Количество в одном месте» и «мест, штук» — не количество товара; «…масса нетто» — оно
        if (RegExp(r'в одном месте|количество мест|мест штук').hasMatch(nl)) continue;
        if (nl.contains('нетто')) {
          cand.add((i, _FieldHit('qty', h.score + 1, h.synLen)));
          continue;
        }
      }
      cand.add((i, h));
    }
  }
  cand.sort((a, b) {
    final c = b.$2.score.compareTo(a.$2.score);
    if (c != 0) return c;
    final d = b.$2.synLen.compareTo(a.$2.synLen);
    return d != 0 ? d : a.$1.compareTo(b.$1);
  });
  final map = <String, int>{};
  final usedCols = <int>{};
  for (final c in cand) {
    if (map.containsKey(c.$2.field) || usedCols.contains(c.$1)) continue;
    map[c.$2.field] = c.$1;
    usedCols.add(c.$1);
  }
  return map;
}

bool _isHeaderMap(Map<String, int> m) {
  final ids = m.keys.where((k) => const {'name', 'barcode', 'article', 'code'}.contains(k)).isNotEmpty;
  final nums = m.keys.where((k) => const {'qty', 'cost', 'costGross', 'sum', 'sumGross'}.contains(k)).isNotEmpty;
  return ids && nums && m.keys.where(_tableFields.contains).length >= 2;
}

// ----------------------------------------------------------------------------------------------- разбор

class InvoiceParser {
  InvoiceParser._();

  static const maxBytes = 10 * 1024 * 1024;
  static const maxRows = 5000;
  static const _maxXmlDepth = 100;
  static const _headerScanRows = 60;

  /// Разобрать файл. Исключение — [InvoiceParseException] с причиной.
  static ParsedInvoice parse(Uint8List bytes, {String fileName = ''}) {
    if (bytes.isEmpty) throw const InvoiceParseException(InvoiceError.empty);
    if (bytes.length > maxBytes) throw const InvoiceParseException(InvoiceError.tooBig);
    final hash = sha256.convert(bytes).toString();
    bool starts(List<int> sig) => bytes.length >= sig.length && [for (var i = 0; i < sig.length; i++) bytes[i] == sig[i]].every((x) => x);
    if (starts([0x25, 0x50, 0x44, 0x46])) throw const InvoiceParseException(InvoiceError.pdf);
    if (starts([0x89, 0x50, 0x4E, 0x47]) || starts([0xFF, 0xD8, 0xFF]) || starts([0x47, 0x49, 0x46, 0x38])) {
      throw const InvoiceParseException(InvoiceError.image);
    }
    if (starts([0xD0, 0xCF, 0x11, 0xE0])) throw const InvoiceParseException(InvoiceError.xls);
    try {
      if (starts([0x50, 0x4B, 0x03, 0x04])) {
        final sheets = _readXlsx(bytes);
        return _fromTables(sheets, InvoiceFormat.xlsx, hash);
      }
      final text = _decodeText(bytes);
      final head = text.trimLeft();
      if (head.startsWith('<')) return _parseXml(text, hash);
      return _fromTables([readCsv(text)], InvoiceFormat.csv, hash);
    } on InvoiceParseException {
      rethrow;
    } catch (e) {
      throw InvoiceParseException(InvoiceError.broken, '$e');
    }
  }

  // --------------------------------------------------------------------------------------------- кодировки

  static String _decodeText(Uint8List b) {
    if (b.length >= 2 && b[0] == 0xFF && b[1] == 0xFE) {
      return String.fromCharCodes([for (var i = 2; i + 1 < b.length; i += 2) b[i] | (b[i + 1] << 8)]);
    }
    if (b.length >= 2 && b[0] == 0xFE && b[1] == 0xFF) {
      return String.fromCharCodes([for (var i = 2; i + 1 < b.length; i += 2) (b[i] << 8) | b[i + 1]]);
    }
    var data = b;
    if (b.length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) data = Uint8List.sublistView(b, 3);
    try {
      return utf8.decode(data);
    } catch (_) {
      return decodeWindows1251(data);
    }
  }

  /// windows-1251 → строка (Excel «CSV» и 1С на русских Windows пишут именно так).
  static String decodeWindows1251(List<int> b) {
    final sb = StringBuffer();
    for (final c in b) {
      if (c < 0x80) {
        sb.writeCharCode(c);
      } else if (c >= 0xC0) {
        sb.writeCharCode(0x410 + (c - 0xC0));
      } else {
        sb.writeCharCode(switch (c) {
          0xA8 => 0x401,
          0xB8 => 0x451,
          0xB9 => 0x2116,
          0xAB => 0xAB,
          0xBB => 0xBB,
          0xA0 => 0xA0,
          0x96 => 0x2013,
          0x97 => 0x2014,
          0xAD => 0xAD,
          _ => 0xFFFD,
        });
      }
    }
    return sb.toString();
  }

  // --------------------------------------------------------------------------------------------- CSV

  /// CSV/TSV: разделитель «;», «,», табуляция или «|» (что чаще вне кавычек в первых строках); кавычки, «""», перевод строки в ячейке.
  static List<List<String>> readCsv(String text) {
    final src = text.replaceFirst('﻿', '');
    // выбор разделителя по первым строкам
    final counts = {';': 0, ',': 0, '\t': 0, '|': 0};
    var q = false, lines = 0;
    for (var i = 0; i < src.length && lines < 30 && i < 20000; i++) {
      final ch = src[i];
      if (ch == '"') {
        q = !q;
      } else if (!q) {
        if (ch == '\n') {
          lines++;
        } else if (counts.containsKey(ch)) {
          counts[ch] = counts[ch]! + 1;
        }
      }
    }
    var sep = ';';
    var best = -1;
    for (final e in counts.entries) {
      if (e.value > best) {
        best = e.value;
        sep = e.key;
      }
    }
    final rows = <List<String>>[];
    var row = <String>[];
    final cell = StringBuffer();
    q = false;
    void endCell() {
      row.add(cell.toString());
      cell.clear();
    }

    void endRow() {
      endCell();
      if (row.any((c) => c.trim().isNotEmpty)) rows.add(row);
      row = <String>[];
      if (rows.length > maxRows + _headerScanRows + 200) throw const InvoiceParseException(InvoiceError.tooMany);
    }

    for (var i = 0; i < src.length; i++) {
      final ch = src[i];
      if (q) {
        if (ch == '"') {
          if (i + 1 < src.length && src[i + 1] == '"') {
            cell.write('"');
            i++;
          } else {
            q = false;
          }
        } else {
          cell.write(ch);
        }
      } else if (ch == '"' && cell.isEmpty) {
        q = true;
      } else if (ch == sep) {
        endCell();
      } else if (ch == '\n') {
        endRow();
      } else if (ch == '\r') {
        // перед \n или одиночный — конец строки
        if (i + 1 < src.length && src[i + 1] == '\n') i++;
        endRow();
      } else {
        cell.write(ch);
      }
    }
    if (cell.isNotEmpty || row.isNotEmpty) endRow();
    return rows;
  }

  // --------------------------------------------------------------------------------------------- XLSX

  static List<List<List<String>>> _readXlsx(Uint8List bytes) {
    final Archive zip;
    try {
      zip = ZipDecoder().decodeBytes(bytes);
    } catch (_) {
      throw const InvoiceParseException(InvoiceError.broken, 'zip');
    }
    if (zip.files.length > 2000) throw const InvoiceParseException(InvoiceError.broken, 'files');
    String? file(String name) {
      final f = zip.findFile(name);
      if (f == null) return null;
      // защита от «бомбы»: распакованный лист/строки — не больше 60 МБ
      if (f.size > 60 * 1024 * 1024) throw const InvoiceParseException(InvoiceError.tooBig);
      return utf8.decode(f.content as List<int>, allowMalformed: true);
    }

    final shared = <String>[];
    final ss = file('xl/sharedStrings.xml');
    if (ss != null) {
      _rejectUnsafeXml(ss);
      for (final si in XmlDocument.parse(ss).findAllElements('si')) {
        final sb = StringBuffer();
        for (final ch in si.childElements) {
          if (ch.name.local == 't') {
            sb.write(ch.innerText);
          } else if (ch.name.local == 'r') {
            for (final t in ch.childElements.where((e) => e.name.local == 't')) {
              sb.write(t.innerText);
            }
          }
        }
        shared.add(sb.toString());
      }
    }
    final names = zip.files.map((f) => f.name).where((n) => RegExp(r'^xl/worksheets/sheet\d+\.xml$').hasMatch(n)).toList()
      ..sort((a, b) => int.parse(RegExp(r'(\d+)\.xml').firstMatch(a)!.group(1)!).compareTo(int.parse(RegExp(r'(\d+)\.xml').firstMatch(b)!.group(1)!)));
    if (names.isEmpty) throw const InvoiceParseException(InvoiceError.broken, 'нет листов');
    final out = <List<List<String>>>[];
    for (final n in names.take(5)) {
      final xml = file(n)!;
      _rejectUnsafeXml(xml);
      final sheet = XmlDocument.parse(xml);
      int col(String ref) {
        var k = 0;
        for (final c in ref.codeUnits) {
          if (c < 65 || c > 90) break;
          k = k * 26 + (c - 64);
        }
        return k - 1;
      }

      final rows = <List<String>>[];
      for (final row in sheet.findAllElements('row')) {
        final cells = <String>[];
        var next = 0;
        for (final c in row.findElements('c')) {
          var i = col(c.getAttribute('r') ?? '');
          if (i < 0) i = next;
          if (i > 500) continue;
          final t = c.getAttribute('t');
          final v = c.getElement('v')?.innerText ?? '';
          final text = switch (t) {
            's' => (int.tryParse(v) != null && int.parse(v) < shared.length) ? shared[int.parse(v)] : '',
            'inlineStr' => c.findAllElements('t').map((e) => e.innerText).join(),
            'e' => '',
            _ => v,
          };
          while (cells.length < i) {
            cells.add('');
          }
          if (cells.length == i) {
            cells.add(text);
          } else {
            cells[i] = text;
          }
          next = i + 1;
        }
        // номер строки листа: пустые строки между данными сохраняют место (номер в сообщениях совпадает с Excel)
        final rn = int.tryParse(row.getAttribute('r') ?? '');
        while (rn != null && rn <= 200000 && rows.length < rn - 1) {
          rows.add(const []);
        }
        rows.add(cells);
        if (rows.length > maxRows + _headerScanRows + 200) throw const InvoiceParseException(InvoiceError.tooMany);
      }
      out.add(rows);
    }
    return out;
  }

  // --------------------------------------------------------------------------------------------- таблицы (Excel/CSV)

  static ParsedInvoice _fromTables(List<List<List<String>>> sheets, InvoiceFormat fmt, String hash) {
    InvoiceParseException? last;
    for (final t in sheets) {
      try {
        return _fromTable(t, fmt, hash);
      } on InvoiceParseException catch (e) {
        if (e.error == InvoiceError.tooMany) rethrow;
        last = e;
      }
    }
    throw last ?? const InvoiceParseException(InvoiceError.noTable);
  }

  static String _cell(List<String> r, int? i) => i == null || i >= r.length ? '' : r[i].replaceAll(' ', ' ').trim();

  static const _totalWords = [
    'итого', 'итог', 'всего', 'total', 'grand total', 'жыйынтыгы', 'баары', 'жалпы', 'jami', 'жами', 'сумма по накладной', 'к оплате', 'всего к оплате',
  ];

  static bool _isTotalLabel(String norm) => _totalWords.any((w) => norm == w || norm.startsWith('$w '));

  static ParsedInvoice _fromTable(List<List<String>> rows, InvoiceFormat fmt, String hash) {
    // 1. строка заголовка среди первых 60 строк
    var headerAt = -1;
    var bestScore = 0;
    Map<String, int> cols = {};
    for (var i = 0; i < rows.length && i < _headerScanRows; i++) {
      final m = _columns(rows[i]);
      if (!_isHeaderMap(m)) continue;
      final score = m.keys.where(_tableFields.contains).length;
      if (score > bestScore) {
        bestScore = score;
        headerAt = i;
        cols = m;
      }
    }
    if (headerAt < 0) throw const InvoiceParseException(InvoiceError.noTable);
    final headerNorm = [for (final c in rows[headerAt]) _normLabel(c)];

    // 2. шапка над таблицей
    final head = _scanHeader(rows.take(headerAt).toList());

    // 3. строки
    final lines = <InvoiceLine>[];
    final hasNumbers = <bool>[];
    final totals = <double>[];
    var currency = _currencyOf(rows[headerAt].join(' '));
    final barcodeCol = cols['barcode'];
    final codeCol = cols['code'];
    for (var i = headerAt + 1; i < rows.length; i++) {
      final r = rows[i];
      final nonEmpty = [for (final c in r) c.trim()].where((c) => c.isNotEmpty).toList();
      if (nonEmpty.isEmpty) continue;
      // строка нумерации столбцов «1 2 3 4 5…»
      if (nonEmpty.length >= 3 && nonEmpty.every((c) => RegExp(r'^\d{1,2}$').hasMatch(c))) {
        final nums = nonEmpty.map(int.parse).toList();
        var seq = true;
        for (var k = 1; k < nums.length; k++) {
          if (nums[k] <= nums[k - 1]) seq = false;
        }
        if (seq && nums.first <= 2) continue;
      }
      // повтор шапки на новой странице
      if (nonEmpty.length >= 3 && [for (final c in r) _normLabel(c)].where((c) => c.isNotEmpty && headerNorm.contains(c)).length >= nonEmpty.length * 0.7) continue;
      // итог
      final first = _normLabel(nonEmpty.first);
      final nameText = _normLabel(_cell(r, cols['name']));
      if (_isTotalLabel(first) || (nameText.isNotEmpty && _isTotalLabel(nameText))) {
        final t = _totalOf(r, cols, nonEmpty);
        if (t != null) totals.add(t);
        currency = currency.isEmpty ? _currencyOf(nonEmpty.join(' ')) : currency;
        continue;
      }
      final name = _cell(r, cols['name']);
      var barcode = '', article = '';
      final bRaw = normalizeInvoiceCode(_cell(r, barcodeCol));
      final aRaw = normalizeInvoiceCode(_cell(r, cols['article']));
      final cRaw = normalizeInvoiceCode(_cell(r, codeCol));
      if (bRaw.isNotEmpty) {
        if (isBarcodeLike(bRaw)) {
          barcode = restoreLostZero(bRaw);
        } else {
          article = bRaw;
        }
      }
      if (aRaw.isNotEmpty) {
        if (barcode.isEmpty && cols['barcode'] == null && isBarcodeLike(aRaw) && aRaw.length >= 8) {
          barcode = aRaw;
        } else {
          article = aRaw;
        }
      }
      if (cRaw.isNotEmpty) {
        // «Код» — штрихкод, если похож на EAN/UPC, иначе артикул
        if (barcode.isEmpty && cols['barcode'] == null && RegExp(r'^\d{8}$|^\d{12,14}$').hasMatch(cRaw)) {
          barcode = cRaw;
        } else if (article.isEmpty) {
          article = cRaw;
        }
      }
      var qty = parseInvoiceNumber(_cell(r, cols['qty'])) ?? 0;
      var cost = parseInvoiceNumber(_cell(r, cols['cost'])) ?? 0;
      final costGross = parseInvoiceNumber(_cell(r, cols['costGross'])) ?? 0;
      final sum = parseInvoiceNumber(_cell(r, cols['sum'])) ?? 0;
      final sumGross = parseInvoiceNumber(_cell(r, cols['sumGross'])) ?? 0;
      final retail = parseInvoiceNumber(_cell(r, cols['retail'])) ?? 0;
      final vatText = _normLabel(_cell(r, cols['vat']));
      final vat = vatText.isEmpty ? -1.0 : (parseInvoiceNumber(vatText) ?? (vatText.contains('без') || vatText.contains('не обл') ? 0.0 : -1.0));
      if (cost == 0 && qty > 0) {
        final base = sum > 0 ? sum : 0.0;
        if (base > 0) cost = _round(base / qty, 4);
      }
      var fromGross = false;
      if (cost == 0 && costGross > 0) {
        // 2026-10-27 (ревью Ск-12): только цена «с НДС» — закупка без НДС по ставке строки, иначе как есть (с предупреждением)
        cost = vat > 0 ? _round(costGross / (1 + vat / 100), 4) : costGross;
        fromGross = true;
      } else if (cost == 0 && sumGross > 0 && sum == 0 && qty > 0) {
        final g = _round(sumGross / qty, 4);
        cost = vat > 0 ? _round(g / (1 + vat / 100), 4) : g;
        fromGross = true;
      }
      if (qty == 0 && cost > 0 && (sum > 0 || sumGross > 0)) qty = _round((sum > 0 ? sum : sumGross) / cost, 3);
      var unit = _cell(r, cols['unit']);
      if (unit.isEmpty) unit = _okei[_cell(r, cols['okei'])] ?? '';
      lines.add(
        InvoiceLine(
          row: i + 1,
          name: name,
          barcode: barcode,
          article: article,
          unit: unit,
          qty: _round(qty, 3),
          cost: cost,
          costGross: costGross,
          sum: sum,
          sumGross: sumGross,
          vat: vat,
          retail: retail,
          costFromGross: fromGross,
        ),
      );
      hasNumbers.add(qty > 0 || cost > 0 || sum > 0 || sumGross > 0 || barcode.isNotEmpty);
    }
    // подписи под таблицей («Отпустил… Получил…») — строки без чисел после последней строки с числами
    while (lines.isNotEmpty && !hasNumbers.last) {
      lines.removeLast();
      hasNumbers.removeLast();
    }
    // 2026-10-27: пустая шапка колонок «Код по ОКЕИ» и т.п. не мешает; только название без чисел внутри таблицы — «не распознана»
    if (lines.isEmpty) throw const InvoiceParseException(InvoiceError.noTable);
    if (lines.length > maxRows) throw const InvoiceParseException(InvoiceError.tooMany);
    if (currency.isEmpty) currency = head.currency;
    final inv = ParsedInvoice(
      format: fmt,
      number: head.number,
      date: head.date,
      supplierName: head.supplierName,
      supplierInn: head.supplierInn,
      lines: lines,
      currency: currency,
      fileHash: hash,
    );
    return _withTotal(inv, totals);
  }

  /// Итог файла — кандидат, ближайший к итогу по строкам (сумма без НДС или с НДС).
  static ParsedInvoice _withTotal(ParsedInvoice inv, List<double> candidates) {
    final c = [for (final t in candidates) if (t > 0) t];
    if (c.isEmpty) return inv;
    final net = inv.linesTotal(), gross = inv.linesTotal(withVat: true);
    double dist(double t) => [(t - net).abs(), (t - gross).abs()].reduce((a, b) => a < b ? a : b);
    c.sort((a, b) => dist(a).compareTo(dist(b)));
    return ParsedInvoice(
      format: inv.format,
      number: inv.number,
      date: inv.date,
      supplierName: inv.supplierName,
      supplierInn: inv.supplierInn,
      lines: inv.lines,
      fileTotal: c.first,
      currency: inv.currency,
      fileHash: inv.fileHash,
    );
  }

  static double? _totalOf(List<String> r, Map<String, int> cols, List<String> nonEmpty) {
    for (final f in ['sumGross', 'sum']) {
      final v = parseInvoiceNumber(_cell(r, cols[f]));
      if (v != null && v > 0) return v;
    }
    // несколько чисел в строке — берём последнее (итог справа); одно число — подпись вроде «Всего мест: 4» не итог
    final nums = <double>[];
    for (final c in nonEmpty.skip(1)) {
      final v = parseInvoiceNumber(c);
      if (v != null) nums.add(v);
    }
    if (nums.length >= 2) return nums.last;
    return amountInText(nonEmpty.join(' '));
  }

  /// Сумма из текста строки итога: «на сумму 301 500,00», «Итого: 12 345.60» или прописью («Триста одна тысяча пятьсот рублей»).
  static double? amountInText(String text) {
    final m = RegExp(r'(?:на сумму|сумма|итого|всего|total)\s*[:：]?\s*(\d[\d\s .,]*\d|\d)', caseSensitive: false).firstMatch(text);
    if (m != null) {
      final v = parseInvoiceNumber(m.group(1));
      if (v != null && v > 0) return v;
    }
    return _wordsToNumber(text);
  }

  static const _w1 = {
    'ноль': 0, 'один': 1, 'одна': 1, 'одно': 1, 'одну': 1, 'два': 2, 'две': 2, 'три': 3, 'четыре': 4, 'пять': 5, 'шесть': 6, 'семь': 7,
    'восемь': 8, 'девять': 9, 'десять': 10, 'одиннадцать': 11, 'двенадцать': 12, 'тринадцать': 13, 'четырнадцать': 14, 'пятнадцать': 15,
    'шестнадцать': 16, 'семнадцать': 17, 'восемнадцать': 18, 'девятнадцать': 19, 'двадцать': 20, 'тридцать': 30, 'сорок': 40,
    'пятьдесят': 50, 'шестьдесят': 60, 'семьдесят': 70, 'восемьдесят': 80, 'девяносто': 90, 'сто': 100, 'двести': 200, 'триста': 300,
    'четыреста': 400, 'пятьсот': 500, 'шестьсот': 600, 'семьсот': 700, 'восемьсот': 800, 'девятьсот': 900,
  };

  /// Сумма прописью на русском → число (рубли/сомы + «NN копеек»). null — слов-чисел нет.
  static double? _wordsToNumber(String text) {
    final norm = _normLabel(text);
    var total = 0.0, cur = 0.0;
    var seen = false;
    final tokens = norm.split(' ');
    for (final t in tokens) {
      if (t.startsWith('руб') || t.startsWith('сом') || t.startsWith('тенге') || t.startsWith('доллар') || t.startsWith('евро')) break;
      if (_w1.containsKey(t)) {
        cur += _w1[t]!;
        seen = true;
      } else if (t.startsWith('тысяч')) {
        total += (cur == 0 ? 1 : cur) * 1000;
        cur = 0;
        seen = true;
      } else if (t.startsWith('миллион')) {
        total += (cur == 0 ? 1 : cur) * 1000000;
        cur = 0;
        seen = true;
      } else if (t.startsWith('миллиард')) {
        total += (cur == 0 ? 1 : cur) * 1000000000;
        cur = 0;
        seen = true;
      }
    }
    total += cur;
    if (!seen || total <= 0) return null;
    final k = RegExp(r'(\d{1,2})\s*коп').firstMatch(norm);
    if (k != null) total += int.parse(k.group(1)!) / 100;
    return total;
  }

  // --------------------------------------------------------------------------------------------- шапка «Ключ: значение»

  static const _numKeys = {
    'номер документа', 'номер', 'номер накладной', 'номер счета фактуры', 'номер счет фактуры', 'номер док', 'документ', 'документ номер',
    'invoice number', 'invoice no', 'invoice', 'document number', 'doc no', 'number', 'no', 'документтин номери', 'номери', 'hujjat raqami',
    'raqami',
  };
  static const _dateKeys = {
    'дата', 'дата составления', 'дата документа', 'дата накладной', 'дата выписки', 'дата отгрузки', 'дата поставки', 'датасы',
    'документтин датасы', 'күнү', 'sana', 'hujjat sanasi', 'date', 'invoice date', 'document date', 'issue date',
  };
  static const _supplierKeys = {
    'поставщик', 'продавец', 'грузоотправитель', 'отправитель', 'организация поставщик', 'жеткирүүчү', 'сатуучу', 'жөнөтүүчү',
    'supplier', 'seller', 'vendor', 'sender', 'from', 'yetkazib beruvchi', 'sotuvchi', 'jonatuvchi',
  };
  static const _buyerKeys = {
    'покупатель', 'получатель', 'грузополучатель', 'плательщик', 'заказчик', 'сатып алуучу', 'алуучу', 'төлөөчү', 'buyer', 'customer',
    'consignee', 'receiver', 'recipient', 'payer', 'xaridor', 'oluvchi', 'buyurtmachi', 'to', 'bill to',
  };
  static final _supplierKeysN = {for (final k in _supplierKeys) _normLabel(k)};
  static final _buyerKeysN = {for (final k in _buyerKeys) _normLabel(k)};
  static final _numKeysN = {for (final k in _numKeys) _normLabel(k)};
  static final _dateKeysN = {for (final k in _dateKeys) _normLabel(k)};

  static String _cleanParty(String v) {
    var s = v.trim();
    // «ООО Ромашка, ИНН 123…» → название до реквизитов (\b для кириллицы не работает — граница задана явно)
    final cut = RegExp(r'[,;\s]\s*(?:инн|inn|тин|tin|кпп|адрес|тел|р/с)(?=[\s:.№\d]|$)').firstMatch(s.toLowerCase());
    if (cut != null) s = s.substring(0, cut.start).trim();
    s = s.replaceAll(RegExp(r'[,;:\s]+$'), '').replaceAll(RegExp(r'\s+'), ' ');
    final low = s.toLowerCase();
    if (low == 'он же' || low == 'он-же' || low == '—' || low == '-') return '';
    return s;
  }

  static String _innIn(String v) => RegExp(r'\d{9,14}').firstMatch(v)?.group(0) ?? '';

  static _Head _scanHeader(List<List<String>> rows) {
    var number = '';
    DateTime? date;
    var supName = '', supInn = '', currency = '';
    var section = ''; // supplier | buyer | ''
    var buyerSeen = false;
    for (final r in rows) {
      for (var i = 0; i < r.length; i++) {
        final raw = r[i].replaceAll(' ', ' ').trim();
        if (raw.isEmpty) continue;
        currency = currency.isEmpty ? _currencyOf(raw) : currency;
        var key = raw, value = '';
        final colon = raw.indexOf(':');
        if (colon > 0 && raw.substring(0, colon).trim().split(RegExp(r'\s+')).length <= 6) {
          key = raw.substring(0, colon);
          value = raw.substring(colon + 1).trim();
        }
        final kn = _normLabel(key);
        // значение в соседней ячейке («Поставщик» | «ООО …»)
        String valueOrNext() {
          if (value.isNotEmpty) return value;
          for (var j = i + 1; j < r.length; j++) {
            final c = r[j].replaceAll(' ', ' ').trim();
            if (c.isNotEmpty) return c;
          }
          return '';
        }

        final isInn = RegExp(r'^(инн|inn|тин|tin|stir|tax id)( |$)').hasMatch(kn);
        if (_supplierKeysN.contains(kn)) {
          section = 'supplier';
          final v = valueOrNext();
          if (supName.isEmpty) supName = _cleanParty(v);
          if (supInn.isEmpty) supInn = _innIn(v);
        } else if (_buyerKeysN.contains(kn)) {
          section = 'buyer';
          buyerSeen = true;
        } else if (isInn) {
          final v = valueOrNext();
          final side = RegExp(r'поставщик|продав|отправител|supplier|seller|sender').hasMatch(kn)
              ? 'supplier'
              : RegExp(r'покупател|получател|buyer|customer|receiver').hasMatch(kn)
              ? 'buyer'
              : (section.isNotEmpty ? section : (buyerSeen ? 'buyer' : 'supplier'));
          if (side == 'supplier' && supInn.isEmpty) supInn = _innIn(v);
        } else if (number.isEmpty && _numKeysN.contains(kn)) {
          var v = valueOrNext().replaceFirst(RegExp(r'^[№#]\s*'), '').trim();
          final m = RegExp(r'^(.*?)\s+(?:от|dan|from)\s+(.+)$', caseSensitive: false).firstMatch(v);
          if (m != null) {
            v = m.group(1)!.trim();
            date ??= parseInvoiceDate(m.group(2));
          }
          if (v.isNotEmpty && v.length <= 30) number = v;
        } else if (date == null && _dateKeysN.contains(kn)) {
          date = parseInvoiceDate(valueOrNext());
        } else if (colon < 0 && number.isEmpty && RegExp(r'^(?:расходная |приходная |товарная |товарно транспортная )?(?:накладн|счет фактур|invoice|ттн)').hasMatch(kn)) {
          // «Накладная № 104 от 05.10.2026» одной ячейкой
          final m = RegExp(r'(?:№|#|(?<![A-Za-zА-Яа-я])[Nn][oо]?\.?(?=[\s:]*\d))\s*:?\s*([^\s,;]+)(?:\s+(?:от|dan|from)\s+(.+))?').firstMatch(raw);
          if (m != null) {
            number = m.group(1)!.replaceAll(RegExp(r'[,;.]+$'), '');
            date ??= parseInvoiceDate(m.group(2) ?? raw.substring(m.end));
          }
        }
      }
    }
    return _Head(number: number, date: date, supplierName: supName, supplierInn: supInn, currency: currency);
  }

  static String _currencyOf(String text) {
    final t = ' ${_normLabel(text)} ';
    final raw = text.toLowerCase();
    if (RegExp(r' (руб|рублей|рубля|рубль|rub|rur) ').hasMatch(t) || raw.contains('₽')) return 'RUB';
    if (RegExp(r' (тенге|kzt|тг) ').hasMatch(t) || raw.contains('₸')) return 'KZT';
    if (RegExp(r' (usd|доллар|долларов|долл) ').hasMatch(t) || raw.contains(r'$')) return 'USD';
    if (RegExp(r' (eur|евро) ').hasMatch(t) || raw.contains('€')) return 'EUR';
    if (RegExp(r' (uzs|сум) ').hasMatch(t)) return 'UZS';
    if (RegExp(r' (сом|сомов|kgs|som) ').hasMatch(t)) return 'KGS';
    return '';
  }

  // --------------------------------------------------------------------------------------------- XML

  /// Файл с DTD/ENTITY не открываем вообще (внешние сущности, «миллиард смешков»).
  static void _rejectUnsafeXml(String text) {
    final up = text.length > 4096 ? text.substring(0, 4096) : text;
    if (RegExp(r'<!DOCTYPE', caseSensitive: false).hasMatch(text) || RegExp(r'<!ENTITY', caseSensitive: false).hasMatch(text) || RegExp(r'<!ELEMENT|<!ATTLIST', caseSensitive: false).hasMatch(up)) {
      throw const InvoiceParseException(InvoiceError.unsafeXml);
    }
  }

  /// Глубина вложенности без разбора дерева (глубокий XML роняет рекурсивный разбор).
  static void _checkDepth(String s) {
    var depth = 0, i = 0;
    final n = s.length;
    while (i < n) {
      final lt = s.indexOf('<', i);
      if (lt < 0 || lt + 1 >= n) break;
      final c = s.codeUnitAt(lt + 1);
      if (c == 0x21) {
        // комментарий или CDATA
        final end = s.startsWith('<!--', lt) ? s.indexOf('-->', lt) : (s.startsWith('<![CDATA[', lt) ? s.indexOf(']]>', lt) : s.indexOf('>', lt));
        if (end < 0) break;
        i = end + 1;
      } else if (c == 0x3F) {
        final end = s.indexOf('?>', lt);
        if (end < 0) break;
        i = end + 2;
      } else {
        // конец тега с учётом кавычек в атрибутах
        var j = lt + 1;
        int? quote;
        while (j < n) {
          final ch = s.codeUnitAt(j);
          if (quote != null) {
            if (ch == quote) quote = null;
          } else if (ch == 0x22 || ch == 0x27) {
            quote = ch;
          } else if (ch == 0x3E) {
            break;
          }
          j++;
        }
        if (j >= n) break;
        if (c == 0x2F) {
          depth--;
        } else if (s.codeUnitAt(j - 1) != 0x2F) {
          depth++;
          if (depth > _maxXmlDepth) throw const InvoiceParseException(InvoiceError.broken, 'depth');
        }
        i = j + 1;
      }
    }
  }

  static ParsedInvoice _parseXml(String rawText, String hash) {
    final text = rawText.trimLeft();
    _rejectUnsafeXml(text);
    _checkDepth(text);
    final XmlDocument doc;
    try {
      doc = XmlDocument.parse(text);
    } on XmlException catch (e) {
      throw InvoiceParseException(InvoiceError.broken, e.message);
    }
    final root = doc.rootElement;
    // все группы одноимённых элементов по пути
    final groups = <String, List<XmlElement>>{};
    final stack = <(XmlElement, String)>[(root, _normName(root.name.local))];
    var total = 0;
    while (stack.isNotEmpty) {
      final (e, path) = stack.removeLast();
      if (++total > 400000) throw const InvoiceParseException(InvoiceError.tooBig);
      groups.putIfAbsent(path, () => []).add(e);
      for (final ch in e.childElements.toList().reversed) {
        stack.add((ch, '$path/${_normName(ch.name.local)}'));
      }
    }
    String? bestPath;
    var bestScore = -1.0;
    final qualified = <String>[];
    for (final entry in groups.entries) {
      final sample = entry.value.take(30).map(_flatten).toList();
      final union = <String>{for (final m in sample) ...m.keys};
      final ids = union.any((f) => const {'name', 'barcode', 'article'}.contains(f));
      final nums = union.any((f) => const {'qty', 'cost', 'sum', 'sumGross', 'costGross'}.contains(f));
      if (!ids || !nums) continue;
      qualified.add(entry.key);
      final fieldCount = union.where(_tableFields.contains).length;
      final score = entry.value.length * fieldCount + entry.key.split('/').length * 0.01;
      if (score > bestScore) {
        bestScore = score;
        bestPath = entry.key;
      }
    }
    // предок строк товара не строка товара
    final real = qualified.where((p) => !qualified.any((o) => o != p && o.startsWith('$p/'))).toList();
    if (real.isEmpty) throw const InvoiceParseException(InvoiceError.noTable);
    if (!real.contains(bestPath)) {
      bestPath = real.reduce((a, b) => groups[a]!.length * 1.0 >= groups[b]!.length ? a : b);
    }
    final rowEls = groups[bestPath]!;
    if (rowEls.length > maxRows) throw const InvoiceParseException(InvoiceError.tooMany);
    final lines = <InvoiceLine>[];
    for (var i = 0; i < rowEls.length; i++) {
      final f = _flatten(rowEls[i]);
      var barcode = '', article = '';
      final bRaw = normalizeInvoiceCode(f['barcode']);
      final aRaw = normalizeInvoiceCode(f['article']);
      final cRaw = normalizeInvoiceCode(f['code']);
      if (bRaw.isNotEmpty) {
        if (isBarcodeLike(bRaw)) {
          barcode = restoreLostZero(bRaw);
        } else {
          article = bRaw;
        }
      }
      if (aRaw.isNotEmpty) {
        if (barcode.isEmpty && !f.containsKey('barcode') && isBarcodeLike(aRaw) && aRaw.length >= 8) {
          barcode = aRaw;
        } else {
          article = aRaw;
        }
      }
      if (cRaw.isNotEmpty && article.isEmpty) article = cRaw;
      var qty = parseInvoiceNumber(f['qty']) ?? 0;
      var cost = parseInvoiceNumber(f['cost']) ?? 0;
      final costGross = parseInvoiceNumber(f['costGross']) ?? 0;
      final sum = parseInvoiceNumber(f['sum']) ?? 0;
      final sumGross = parseInvoiceNumber(f['sumGross']) ?? 0;
      final vatText = _normLabel(f['vat'] ?? '');
      final vat = vatText.isEmpty ? -1.0 : (parseInvoiceNumber(vatText) ?? (vatText.contains('без') || vatText.contains('не обл') ? 0.0 : -1.0));
      if (cost == 0 && qty > 0 && sum > 0) cost = _round(sum / qty, 4);
      var sumNet = sum, sumGr = sumGross, costGr = costGross;
      // 2026-10-27 (ревью Ск-15): CommerceML «УчтеноВСумме = true» — цена и сумма УЖЕ с НДС. Раньше их принимали за «без НДС», и
      // переключатель «с НДС» прибавлял налог второй раз. Теперь они переносятся в «с НДС», а «без НДС» считается по ставке.
      final inclVat = rowEls[i].descendantElements.any((e) => _normName(e.name.local).startsWith('учтено') && RegExp(r'^(true|1|да)$', caseSensitive: false).hasMatch(e.innerText.trim()));
      if (inclVat && vat > 0) {
        final k = 1 + vat / 100;
        if (costGr == 0 && cost > 0) costGr = cost;
        if (sumGr == 0 && sum > 0) sumGr = sum;
        cost = costGr > 0 ? _round(costGr / k, 4) : cost;
        sumNet = sumGr > 0 ? _round(sumGr / k, 2) : sum;
      }
      var fromGross = false;
      if (cost == 0 && costGr > 0) {
        cost = vat > 0 ? _round(costGr / (1 + vat / 100), 4) : costGr;
        fromGross = true;
      }
      if (qty == 0 && cost > 0 && (sumNet > 0 || sumGr > 0)) qty = _round((sumNet > 0 ? sumNet : sumGr) / cost, 3);
      var unit = (f['unit'] ?? '').trim();
      unit = _okei[unit] ?? unit;
      lines.add(
        InvoiceLine(
          row: i + 1,
          name: (f['name'] ?? '').trim(),
          barcode: barcode,
          article: article,
          unit: unit,
          qty: _round(qty, 3),
          cost: cost,
          costGross: costGr,
          sum: sumNet,
          sumGross: sumGr,
          vat: vat,
          retail: parseInvoiceNumber(f['retail']) ?? 0,
          costFromGross: fromGross,
        ),
      );
    }
    final head = _xmlHeader(root, rowEls.toSet());
    final inv = ParsedInvoice(
      format: InvoiceFormat.xml,
      number: head.number,
      date: head.date,
      supplierName: head.supplierName,
      supplierInn: head.supplierInn,
      lines: lines,
      currency: head.currency,
      fileHash: hash,
    );
    return _withTotal(inv, head.totals);
  }

  /// Поля строки товара: поле → значение (атрибуты и листовые элементы до 4 уровней вглубь).
  static Map<String, String> _flatten(XmlElement row) {
    final best = <String, (int, String)>{};
    var depth = 0;
    // оценка: точность названия важнее, при равенстве — ближе к строке и раньше по порядку (первый побеждает)
    void put(String key, String value) {
      final v = value.trim();
      if (v.isEmpty) return;
      for (final h in _hits(_normName(key))) {
        final sc = h.score * 10 - depth;
        final old = best[h.field];
        if (old == null || sc > old.$1) best[h.field] = (sc, v);
      }
    }

    for (final a in row.attributes) {
      put(a.name.local, a.value);
    }
    final stack = <(XmlElement, int)>[for (final c in row.childElements.toList().reversed) (c, 1)];
    while (stack.isNotEmpty) {
      final (e, d) = stack.removeLast();
      depth = d;
      final kids = e.childElements.toList();
      if (kids.isEmpty) {
        var text = e.innerText;
        // <БазоваяЕдиница Код="796" НаименованиеПолное="Штука">796</…> — берём читаемое
        for (final a in e.attributes) {
          final an = _normName(a.name.local);
          if ((text.trim().isEmpty || RegExp(r'^\d+$').hasMatch(text.trim())) && (an.contains('наименование') || an == 'name') && a.value.trim().isNotEmpty) {
            text = a.value;
          }
        }
        put(e.name.local, text);
        for (final a in e.attributes) {
          put('${e.name.local} ${a.name.local}', a.value);
        }
      } else if (d < 4) {
        for (final a in e.attributes) {
          put('${e.name.local} ${a.name.local}', a.value);
        }
        for (final c in kids.reversed) {
          stack.add((c, d + 1));
        }
      }
    }
    return {for (final e in best.entries) e.key: e.value.$2};
  }

  static const _xmlNumKeys = {
    'номер', 'number', 'num', 'invoice number', 'номер документа', 'registration number', 'регистрационный номер', 'no', 'номер накладной',
    'invoice no', 'document number', 'doc number', 'invoice num', 'номер счета фактуры',
  };
  static const _xmlDateKeys = {'дата', 'date', 'invoice date', 'issue date', 'дата документа', 'дата накладной', 'дата выписки', 'document date', 'date issued', 'delivery date'};
  static const _xmlTotalKeys = {
    'сумма', 'итого', 'всего', 'total', 'total amount', 'sum', 'amount', 'сумма документа', 'итого с ндс', 'всего с ндс', 'total sum', 'grand total',
    'total cost', 'общая сумма', 'total price', 'итоговая сумма', 'total with vat', 'total without vat', 'cost total',
  };
  static const _xmlSupplierBlocks = {'продавец', 'поставщик', 'seller', 'supplier', 'vendor', 'отправитель', 'грузоотправитель', 'sender', 'from', 'сторона продавец'};
  static const _xmlBuyerBlocks = {'покупатель', 'buyer', 'customer', 'получатель', 'грузополучатель', 'receiver', 'recipient', 'сторона покупатель', 'to'};
  static const _xmlPartyName = {
    'наименование', 'полное наименование', 'название', 'name', 'full name', 'organization', 'org name', 'legal name', 'наименование организации',
    'title', 'наименование поставщика', 'наименование продавца', 'sellername', 'short name',
  };
  static const _xmlInnKeys = {'инн', 'inn', 'tin', 'бин', 'bin', 'tax id', 'taxid', 'stir', 'сти', 'vat id', 'vat number', 'pin'};

  static _Head _xmlHeader(XmlElement root, Set<XmlElement> rows) {
    var number = '', supName = '', supInn = '', currency = '';
    DateTime? date;
    final totals = <double>[];
    final supBlocks = _xmlSupplierBlocks.map(_normLabel).toSet();
    final buyBlocks = _xmlBuyerBlocks.map(_normLabel).toSet();
    final numKeys = _xmlNumKeys.map(_normLabel).toSet();
    final dateKeys = _xmlDateKeys.map(_normLabel).toSet();
    final totalKeys = _xmlTotalKeys.map(_normLabel).toSet();
    final partyName = _xmlPartyName.map(_normLabel).toSet();
    final innKeys = _xmlInnKeys.map(_normLabel).toSet();
    final isCml = _normName(root.name.local).contains('коммерческая') || _normName(root.name.local).contains('commerceml');

    String innOf(XmlElement e) {
      for (final d in e.descendantElements.where((x) => x.childElements.isEmpty)) {
        if (innKeys.contains(_normName(d.name.local))) {
          final v = _innIn(d.innerText);
          if (v.isNotEmpty) return v;
        }
      }
      return '';
    }

    String nameOf(XmlElement e) {
      String? first;
      for (final d in e.descendantElements.where((x) => x.childElements.isEmpty)) {
        final k = _normName(d.name.local);
        if (partyName.contains(k) && d.innerText.trim().isNotEmpty) {
          if (k == 'полное наименование' || k == 'full name') return d.innerText.trim();
          first ??= d.innerText.trim();
        }
      }
      return first ?? '';
    }

    void walk(XmlElement e, {required bool inParty}) {
      if (rows.contains(e)) return;
      final key = _normName(e.name.local);
      var party = inParty;
      if (!inParty) {
        if (supBlocks.contains(key)) {
          party = true;
          if (supName.isEmpty) supName = nameOf(e);
          if (supInn.isEmpty) supInn = innOf(e);
        } else if (buyBlocks.contains(key)) {
          party = true;
        } else if (isCml && key == 'контрагент') {
          party = true;
          final role = _normLabel(e.childElements.where((c) => _normName(c.name.local) == 'роль').map((c) => c.innerText).firstOrNull ?? '');
          if ((role == 'продавец' || role == 'поставщик') && supName.isEmpty) {
            supName = nameOf(e);
            supInn = supInn.isEmpty ? innOf(e) : supInn;
          }
        }
      }
      final kids = e.childElements.toList();
      if (kids.isEmpty) {
        if (party) return;
        final v = e.innerText.trim();
        if (v.isEmpty) return;
        if (number.isEmpty && numKeys.contains(key) && v.length <= 40) number = v.replaceFirst(RegExp(r'^[№#]\s*'), '');
        if (date == null && dateKeys.contains(key)) date = parseInvoiceDate(v);
        if (totalKeys.contains(key)) {
          final t = parseInvoiceNumber(v);
          if (t != null && t > 0) totals.add(t);
        }
        if (currency.isEmpty && const {'валюта', 'currency', 'currency code', 'код валюты', 'валюта документа'}.contains(key)) {
          currency = _xmlCurrency(v);
        }
        return;
      }
      for (final c in kids) {
        walk(c, inParty: party);
      }
    }

    // обход в глубину без ограничения вызовов: вложенность уже проверена (до 100)
    walk(root, inParty: false);
    // шапки-атрибуты корня: <Накладная Номер="12" Дата="…">
    for (final a in root.attributes) {
      final k = _normName(a.name.local);
      if (number.isEmpty && numKeys.contains(k)) number = a.value.trim();
      if (date == null && dateKeys.contains(k)) date = parseInvoiceDate(a.value);
    }
    return _Head(number: number, date: date, supplierName: supName, supplierInn: supInn, currency: currency, totals: totals);
  }

  static String _xmlCurrency(String v) {
    final s = v.trim().toUpperCase();
    return switch (s) {
      'RUB' || 'RUR' || '643' || 'РУБ' || 'РУБ.' => 'RUB',
      'KGS' || '417' || 'СОМ' => 'KGS',
      'KZT' || '398' || 'ТЕНГЕ' => 'KZT',
      'USD' || '840' => 'USD',
      'EUR' || '978' => 'EUR',
      'UZS' || '860' => 'UZS',
      _ => _currencyOf(v),
    };
  }
}

class _Head {
  final String number;
  final DateTime? date;
  final String supplierName;
  final String supplierInn;
  final String currency;
  final List<double> totals;
  const _Head({this.number = '', this.date, this.supplierName = '', this.supplierInn = '', this.currency = '', this.totals = const []});
}
