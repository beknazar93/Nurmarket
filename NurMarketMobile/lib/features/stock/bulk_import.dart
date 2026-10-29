import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';
import 'package:xml/xml.dart';

import '../../core/api/api_client.dart';
import '../../core/catalog/global_products.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/units.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../admin/admin_receive.dart';
import 'product_edit_repository.dart' show isWeightUnit;
import 'unit_picker.dart';

/// 13.10, владелец: «на складе нет создания товара и массовой загрузки товара — в мобилке». Массовая загрузка как
/// «Массовая загрузка» кассы (BulkImportWindow): файл Excel (.xlsx) или CSV, либо вставка скопированного из Excel;
/// столбцы находятся по заголовкам (в любом порядке), без заголовков — по порядку из шаблона. Товар с тем же штрихкодом
/// не дублируется — прибавляется количество, обновляются цены (тем же путём, что приёмка без поставщика).
/// 2026-10-29: что не так со строкой — подсвечивается в списке; строку с ошибкой не загружаем, пока её не поправят.

enum RowProblem { noName, noPrice, badUnit, qtyWhole, qtyNegative, badBarcode }



class ImportRow {

  final String name;

  final String barcode;

  final double price;

  final double cost;

  final double qty;

  final String unit;

  final String category;



  /// 2026-10-29: бренд (столбец «Бренд» или справочник NurCRM) — уходит в карточку нового товара.

  final String brand;



  /// 2026-10-29: единица распознана по каталогу units.dart (или выбрана руками, в том числе «своя»). Незнакомая единица из

  /// файла («оп», «xyz») — ошибка строки: иначе она молча стала бы чужой единицей на складе и на кассе.

  final bool unitOk;

  const ImportRow({required this.name, this.barcode = '', this.price = 0, this.cost = 0, this.qty = 0, this.unit = 'шт', this.category = '', this.brand = '', this.unitOk = true});



  /// Ошибки, из-за которых строка не загрузится: нет названия, цены, незнакомая единица, дробное количество у «шт», минус.

  List<RowProblem> get problems => [

    if (name.trim().isEmpty) RowProblem.noName,

    if (price <= 0) RowProblem.noPrice,

    if (!unitOk) RowProblem.badUnit,

    if (qtyProblem(unit, qty) == QtyProblem.negative) RowProblem.qtyNegative,

    if (qtyProblem(unit, qty) == QtyProblem.notWhole) RowProblem.qtyWhole,
    if (!barcodeFieldOk(barcode)) RowProblem.badBarcode, // 2026-10-29: штрихкод — только 4–14 цифр

  ];

  bool get valid => problems.isEmpty;



  /// Количество не указано: не ошибка (товар создаётся с остатком 0, у существующего меняются цены), но подсвечивается.

  bool get noQty => qty == 0;



  ImportRow copyWith({String? name, String? barcode, double? price, double? cost, double? qty, String? unit, String? category, String? brand, bool? unitOk}) => ImportRow(

    name: name ?? this.name,

    barcode: barcode ?? this.barcode,

    price: price ?? this.price,

    cost: cost ?? this.cost,

    qty: qty ?? this.qty,

    unit: unit ?? this.unit,

    category: category ?? this.category,

    brand: brand ?? this.brand,

    unitOk: unitOk ?? this.unitOk,

  );



  /// 2026-10-27: строка со штрихкодом, но без названия — название (и пустую категорию) берём из встроенного справочника NurCRM,

  /// без сети. Введённое в файле не затирается. Возвращает новые строки и сколько названий подставлено.

  /// 2026-10-29: так же подставляются пустые категория и бренд (не затирая введённое).

  static Future<(List<ImportRow>, int)> fillNames(List<ImportRow> rows, GlobalProducts catalog) async {

    var filled = 0;

    final out = <ImportRow>[];

    for (final r in rows) {

      if (r.barcode.isEmpty || (r.name.trim().isNotEmpty && r.category.isNotEmpty && r.brand.isNotEmpty)) {

        out.add(r);

        continue;

      }

      final g = await catalog.local(r.barcode);

      if (g == null) {

        out.add(r);

        continue;

      }

      final nameBlank = r.name.trim().isEmpty;

      if (nameBlank) filled++;

      out.add(

        r.copyWith(

          name: nameBlank ? g.name : null,

          category: r.category.isEmpty && g.category.isNotEmpty ? g.category : null,

          brand: r.brand.isEmpty && g.brand.isNotEmpty ? g.brand : null,

        ),

      );

    }

    return (out, filled);

  }

}



class BulkParser {
  // 2026-10-29: названия столбцов на ru/ky/uz/en/tr (в нормальном виде — см. [_norm]: без точек и апострофов, нижний регистр).

  static const _fields = {

    'name': ['название', 'наименование', 'товар', 'name', 'аталышы', 'аты', 'nomi', 'nom', 'ad', 'ürün adı', 'urun adi', 'product', 'tovar'],

    'barcode': ['штрихкод', 'штрих-код', 'штрих код', 'barcode', 'ean', 'код', 'shtrix kod', 'shtrixkod', 'barkod'],

    'price': [

      'цена продажи', 'цена', 'продажа', 'price', 'сатуу баасы', 'розница', 'баасы', 'баа', 'narx', 'sotuv narxi', 'sotish narxi', 'fiyat', 'satış fiyatı', 'satis fiyati', 'sale price',

    ],

    'cost': [

      'закупка', 'цена закупки', 'закупочная цена', 'себестоимость', 'cost', 'purchase', 'сатып алуу', 'сатып алуу баасы', 'xarid', 'xarid narxi', 'alış', 'alış fiyatı', 'alis fiyati', 'purchase price',

    ],

    'qty': ['количество', 'кол-во', 'кол во', 'остаток', 'qty', 'quantity', 'саны', 'калдык', 'miqdor', 'soni', 'qoldiq', 'miktar', 'adet', 'stok', 'stock'],

    'unit': ['единица', 'ед', 'ед изм', 'единица измерения', 'unit', 'бирдик', 'өлчөө бирдиги', 'өлчөм бирдиги', 'birlik', 'olchov birligi', 'birim', 'uom', 'measure'],

    'category': ['категория', 'category', 'группа', 'категориясы', 'kategoriya', 'kategori'],

    'brand': ['бренд', 'брэнд', 'brand', 'brend', 'marka'],

  };

  static const _defaultOrder = ['name', 'barcode', 'price', 'cost', 'qty', 'unit', 'category'];



  static String _norm(String s) => unitKey(s.replaceAll('\uFEFF', ''));



  /// Число из ячейки: «12,75», «12.75», «1 250,5», «1.250,50». Пусто или не число — 0.

  static double _num(String s) => parseNum(s) ?? 0;



  /// Строки таблицы → товары. Заголовок узнаём по названиям столбцов; нет заголовка — порядок шаблона.

  static List<ImportRow> parse(List<List<String>> table) {

    final rows = table.where((r) => r.any((c) => c.trim().isNotEmpty)).toList();

    if (rows.isEmpty) return [];

    final header = rows.first.map(_norm).toList();

    final map = <String, int>{};

    // точное совпадение важнее частичного: «цена закупки» не должна стать «ценой» — сначала у всех полей точное, потом начало слова

    for (final entry in _fields.entries) {

      final i = header.indexWhere((h) => entry.value.contains(h) && !map.values.contains(header.indexOf(h)));

      if (i >= 0) map[entry.key] = i;

    }

    for (final entry in _fields.entries) {

      if (map.containsKey(entry.key)) continue;

      final i = header.indexWhere((h) => h.isNotEmpty && entry.value.any((w) => h.startsWith(w)) && !map.values.contains(header.indexOf(h)));

      if (i >= 0) map[entry.key] = i;

    }

    // 2026-10-27: файл «штрихкод + цена + количество» без столбца названий тоже с заголовком — названия подставит справочник NurCRM

    final hasHeader = map.containsKey('name') || (map.containsKey('barcode') && map.length >= 2);

    if (!hasHeader) {

      map.clear();

      for (var i = 0; i < _defaultOrder.length; i++) {

        map[_defaultOrder[i]] = i;

      }

    }

    String cell(List<String> r, String f) {

      final i = map[f];

      return i == null || i >= r.length ? '' : r[i].trim();

    }



    return [

      for (final r in hasHeader ? rows.skip(1) : rows) _row(cell, r),

    ];

  }



  static ImportRow _row(String Function(List<String>, String) cell, List<String> r) {

    final rawUnit = cell(r, 'unit');

    // пустая единица — «шт» (старые файлы без столбца); написание из каталога — к коду; незнакомое — ошибка строки

    final def = unitOf(rawUnit);

    return ImportRow(

      name: cell(r, 'name'),

      barcode: cell(r, 'barcode').replaceAll(RegExp(r'\.0+$'), '').replaceAll(RegExp(r'[\s\-]'), ''),

      price: _num(cell(r, 'price')),

      cost: _num(cell(r, 'cost')),

      qty: _num(cell(r, 'qty')),

      unit: rawUnit.isEmpty ? 'шт' : (def?.code ?? rawUnit),

      unitOk: rawUnit.isEmpty || def != null,

      category: cell(r, 'category'),

      brand: cell(r, 'brand'),

    );

  }



  /// CSV или текст из Excel: разделитель — табуляция, «;» или «,» (что чаще в первой строке); кавычки учитываются.
  static List<List<String>> readText(String text) {
    final lines = const LineSplitter().convert(text.replaceFirst('﻿', ''));
    if (lines.isEmpty) return [];
    final first = lines.first;
    final sep = ['\t', ';', ','].reduce((a, b) => a.allMatches(first).length >= b.allMatches(first).length ? a : b);
    List<String> split(String line) {
      final out = <String>[];
      final sb = StringBuffer();
      var quoted = false;
      for (var i = 0; i < line.length; i++) {
        final ch = line[i];
        if (ch == '"') {
          if (quoted && i + 1 < line.length && line[i + 1] == '"') {
            sb.write('"');
            i++;
          } else {
            quoted = !quoted;
          }
        } else if (ch == sep && !quoted) {
          out.add(sb.toString());
          sb.clear();
        } else {
          sb.write(ch);
        }
      }
      out.add(sb.toString());
      return out;
    }

    return lines.map(split).toList();
  }

  /// .xlsx — zip с XML: общие строки (sharedStrings) и первый лист; ячейки по адресу (A1, B1…), пустые — пропуски.
  static List<List<String>> readXlsx(Uint8List bytes) {
    final zip = ZipDecoder().decodeBytes(bytes);
    String? file(String name) {
      final f = zip.findFile(name);
      return f == null ? null : utf8.decode(f.content as List<int>);
    }

    final shared = <String>[];
    final ss = file('xl/sharedStrings.xml');
    if (ss != null) {
      for (final si in XmlDocument.parse(ss).findAllElements('si')) {
        shared.add(si.findAllElements('t').map((t) => t.innerText).join());
      }
    }
    final sheetName = zip.files.map((f) => f.name).where((n) => n.startsWith('xl/worksheets/sheet') && n.endsWith('.xml')).toList()..sort();
    if (sheetName.isEmpty) return [];
    final sheet = XmlDocument.parse(file(sheetName.first)!);
    int col(String ref) {
      var n = 0;
      for (final c in ref.codeUnits) {
        if (c < 65 || c > 90) break;
        n = n * 26 + (c - 64);
      }
      return n - 1;
    }

    final out = <List<String>>[];
    for (final row in sheet.findAllElements('row')) {
      final cells = <String>[];
      for (final c in row.findElements('c')) {
        final i = col(c.getAttribute('r') ?? '');
        final t = c.getAttribute('t');
        final v = c.getElement('v')?.innerText ?? '';
        final text = t == 's'
            ? (int.tryParse(v) != null && int.parse(v) < shared.length ? shared[int.parse(v)] : '')
            : t == 'inlineStr'
            ? c.findAllElements('t').map((e) => e.innerText).join()
            : v;
        while (cells.length < i) {
          cells.add('');
        }
        cells.add(text);
      }
      out.add(cells);
    }
    return out;
  }
}

/// 2026-10-22 (мобилка, админ, П12): загрузка строк — как StartImport_Click кассы. Раньше «уже есть» считалось один раз
/// при выборе файла: повтор «Загрузить» после ошибки создавал все новые товары второй раз и снова прибавлял остатки.
/// Теперь: загруженные строки при повторе не трогаются; созданный товар запоминается после каждой порции; повтор
/// одного штрихкода (или названия без штрихкода) в файле пропускается; остаток — «Прибавить» или «Задать».
class BulkUploader {
  final AdminReceiveRepository repo;
  final ApiClient api;
  BulkUploader(this.repo, this.api);

  /// true — «Задать остаток» (как касса: остаток заменяется), false — «Прибавить к остатку» (как приход).
  bool setStock = false;

  /// Строка → id товара (уже есть на складе или создан этой загрузкой).
  final existing = Map<ImportRow, String>.identity();

  /// Строка → номер (с 1) первой строки с тем же штрихкодом или названием.
  final duplicates = Map<ImportRow, int>.identity();

  /// Загружены — повтор их не трогает.
  final uploaded = Set<ImportRow>.identity();
  final _lines = Map<ImportRow, ReceiveLine>.identity();

  static String _nameKey(String s) => s.trim().toLowerCase().replaceAll(RegExp(r'\s+'), ' ');

  /// Новые строки файла: товары, что уже есть (по штрихкоду в любом написании, без штрихкода — по точному названию), и
  /// повторы внутри файла.
  /// 2026-10-29: [keep] — пересчёт после правки строки: загруженные строки и созданные товары не забываются (иначе повтор
  /// «Загрузить» создал бы их второй раз).
  Future<void> prepare(AppDatabase db, List<ImportRow> rows, {bool keep = false}) async {
    if (keep) {
      duplicates.clear();
    } else {
      existing.clear();
      duplicates.clear();
      uploaded.clear();
      _lines.clear();
    }
    final first = <String, int>{};
    for (var i = 0; i < rows.length; i++) {
      final r = rows[i];
      if (!r.valid) continue;
      final key = r.barcode.isNotEmpty ? 'b:${r.barcode}' : 'n:${_nameKey(r.name)}';
      final seen = first[key];
      if (seen != null) {
        duplicates[r] = seen;
        continue;
      }
      first[key] = i + 1;
      if (uploaded.contains(r) || existing.containsKey(r)) continue; // уже загружена или товар известен — не ищем заново
      if (r.barcode.isNotEmpty) {
        final p = (await db.productByScan(r.barcode, allowWeightLabel: false))?.product;
        if (p != null) existing[r] = p.id;
      } else {
        final same = (await db.searchProducts(r.name, limit: 20)).where((p) => _nameKey(p.name) == _nameKey(r.name)).firstOrNull;
        if (same != null) existing[r] = same.id;
      }
    }
  }

  /// 2026-10-29: строку поправили (экран «Строка товара», массовые «Установить…») — новая строка занимает место старой,
  /// а помнить о ней нужно то же: создан ли товар, загружена ли.
  void replace(ImportRow old, ImportRow now, {bool keyChanged = false}) {
    final id = existing.remove(old);
    // сменили штрихкод или название — «уже есть» ищем заново (prepare), старый id к новой строке не относится
    if (id != null && !keyChanged) existing[now] = id;
    if (uploaded.remove(old)) uploaded.add(now);
    duplicates.remove(old);
    _lines.remove(old);
  }

  bool canUpload(ImportRow r) => r.valid && !duplicates.containsKey(r) && !uploaded.contains(r);

  /// Загрузить ещё не загруженные строки порциями по 20. Возвращает ошибки.
  Future<List<String>> upload(List<ImportRow> rows, {AppLocalizations? l10n, void Function()? onProgress}) async {
    final todo = rows.where(canUpload).toList();
    final errors = <String>[];
    // по 20 строк: прогресс виден, а одна ошибка не роняет весь файл
    for (var i = 0; i < todo.length; i += 20) {
      final part = todo.skip(i).take(20).toList();
      // строка живёт до успеха: созданный товар и остаток без ответа помнятся между попытками
      final lines = [
        for (final r in part)
          _lines.putIfAbsent(
            r,
            () => ReceiveLine(
              productId: existing[r] ?? '',
              name: r.name,
              barcode: r.barcode,
              unit: r.unit,
              qty: r.qty,
              cost: r.cost,
              price: r.price,
              isNew: !existing.containsKey(r),
              category: r.category,
              brand: r.brand,
              // 2026-10-29: кг/г — весовой товар (is_weight + PLU), как «Новый товар»; раньше «кг» из файла создавался штучным
              weight: isWeightUnit(r.unit),
            ),
          ),
      ];
      try {
        final (_, errs) = await repo.post(lines, null, false, setStock: setStock, l10n: l10n);
        errors.addAll(errs);
      } on ApiException catch (e) {
        errors.add(e.message);
      } finally {
        // «уже есть» — сразу после порции: повтор не создаст товар второй раз
        for (var k = 0; k < part.length; k++) {
          if (lines[k].productId.isNotEmpty) existing[part[k]] = lines[k].productId;
          if (lines[k].posted) uploaded.add(part[k]);
        }
      }
      // без количества приёмка строку не проводит: новый товар уже создан, у существующего — только цены
      for (var k = 0; k < part.length; k++) {
        final x = lines[k];
        if (x.qty > 0 || x.productId.isEmpty || uploaded.contains(part[k])) continue;
        try {
          if (!x.isNew) {
            await api.patch(
              'api/main/products/${x.productId}/',
              body: {'price': x.price.toStringAsFixed(2), if (x.cost > 0) 'purchase_price': x.cost.toStringAsFixed(2)},
            );
          }
          uploaded.add(part[k]);
        } on ApiException catch (e) {
          errors.add('${x.name}: ${e.message}');
        }
      }
      onProgress?.call();
    }
    return errors;
  }
}

class BulkImportScreen extends ConsumerStatefulWidget {
  const BulkImportScreen({super.key});
  @override
  ConsumerState<BulkImportScreen> createState() => _BulkImportScreenState();
}

class _BulkImportScreenState extends ConsumerState<BulkImportScreen> {
  List<ImportRow> _rows = [];
  late final BulkUploader _up = BulkUploader(ref.read(adminReceiveRepoProvider), ref.read(apiProvider));
  bool _busy = false;

  /// 2026-10-29: отмеченные галочкой строки — к ним применяются «Установить единицу / цену / количество».
  final Set<ImportRow> _sel = Set<ImportRow>.identity();

  /// Итог последней загрузки: «Готово N, с ошибками M».
  String? _result;

  Future<void> _setRows(List<ImportRow> parsed) async {
    final l = context.l;
    // 2026-10-27: названия по штрихкодам из встроенного справочника NurCRM (там, где в файле названия нет)
    var rows = parsed, filled = 0;
    try {
      (rows, filled) = await ImportRow.fillNames(parsed, ref.read(globalProductsProvider)).timeout(const Duration(seconds: 8));
    } catch (_) {}
    if (!mounted) return;
    await _up.prepare(ref.read(databaseProvider), rows);
    if (!mounted) return;
    if (rows.isEmpty) toast(context, l.bulkNothing, error: true);
    if (filled > 0) toast(context, l.globalBulkFilled(filled));
    setState(() {
      _rows = rows;
      _sel.clear();
      _result = null;
    });
  }

  /// 2026-10-29: заменить строки (правка одной или массовая операция). Загруженные не трогаем.
  Future<void> _change(Map<ImportRow, ImportRow> changes) async {
    var keyChanged = false;
    final next = [
      for (final r in _rows)
        if (changes.containsKey(r) && !_up.uploaded.contains(r)) changes[r]! else r,
    ];
    for (final e in changes.entries) {
      if (_up.uploaded.contains(e.key)) continue;
      final key = e.key.barcode != e.value.barcode || e.key.name.trim().toLowerCase() != e.value.name.trim().toLowerCase();
      keyChanged = keyChanged || key;
      _up.replace(e.key, e.value, keyChanged: key);
      if (_sel.remove(e.key)) _sel.add(e.value);
    }
    if (keyChanged) {
      await _up.prepare(ref.read(databaseProvider), next, keep: true);
      if (!mounted) return;
    }
    setState(() {
      _rows = next;
      _result = null;
    });
  }

  Future<void> _editRow(ImportRow r) async {
    if (_busy || _up.uploaded.contains(r)) return;
    final now = await showModalBottomSheet<ImportRow>(
      context: context,
      isScrollControlled: true,
      builder: (c) => _RowSheet(row: r),
    );
    if (now == null || !mounted) return;
    await _change({r: now});
  }

  List<ImportRow> get _selected => [for (final r in _rows) if (_sel.contains(r) && !_up.uploaded.contains(r)) r];

  Future<void> _massUnit() async {
    final l = context.l;
    var unit = 'шт';
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => AlertDialog(
          title: Text(l.bkSetUnit),
          content: SingleChildScrollView(child: UnitPicker(value: unit, onChanged: (u) => set(() => unit = u))),
          actions: [
            TextButton(style: TextButton.styleFrom(minimumSize: const Size(64, 48)), onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
            FilledButton(
              key: const Key('bkMassApply'),
              style: FilledButton.styleFrom(minimumSize: const Size(64, 48)),
              onPressed: () => Navigator.pop(c, true),
              child: Text(l.bkApplyTo(_selected.length)),
            ),
          ],
        ),
      ),
    );
    if (ok != true || !mounted) return;
    await _change({for (final r in _selected) r: r.copyWith(unit: unit, unitOk: true)});
  }

  Future<void> _massPrice() async {
    final l = context.l;
    final v = await askNumber(context, title: l.bkSetPrice, label: l.salePrice, suffix: l.som, ok: l.bkApplyTo(_selected.length));
    if (v == null || v <= 0 || !mounted) return;
    await _change({for (final r in _selected) r: r.copyWith(price: v)});
  }

  Future<void> _massQty() async {
    final l = context.l;
    final v = await askNumber(context, title: l.bkSetQty, label: l.quantityLabel, ok: l.bkApplyTo(_selected.length));
    if (v == null || v < 0 || !mounted) return;
    await _change({for (final r in _selected) r: r.copyWith(qty: v)});
  }

  Future<void> _pickFile() async {
    final files = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['xlsx', 'csv', 'txt', 'tsv']);
    final f = files.firstOrNull;
    if (f == null) return;
    final bytes = await f.readAsBytes();
    try {
      final table = (f.extension ?? '').toLowerCase() == 'xlsx' ? BulkParser.readXlsx(bytes) : BulkParser.readText(utf8.decode(bytes, allowMalformed: true));
      await _setRows(BulkParser.parse(table));
    } catch (e) {
      if (mounted) toast(context, '$e', error: true);
    }
  }

  Future<void> _paste() async {
    final data = await Clipboard.getData(Clipboard.kTextPlain);
    final text = data?.text ?? '';
    if (text.trim().isEmpty) return;
    await _setRows(BulkParser.parse(BulkParser.readText(text)));
  }

  Future<void> _template() async {
    final dir = await getTemporaryDirectory();
    final file = File('${dir.path}/nurmarket-tovary.csv');
    await file.writeAsString('﻿Название;Штрихкод;Цена продажи;Закупка;Количество;Единица;Категория\nКока-Кола 2л;5449000000996;170;130;12;шт;Напитки\nЯблоки;;120,5;90;12,5;кг;Фрукты\n');
    await SharePlus.instance.share(
      ShareParams(
        files: [XFile(file.path, mimeType: 'text/csv', name: 'nurmarket-tovary.csv')],
      ),
    );
  }

  Future<void> _upload() async {
    final l = context.l;
    if (_busy || !_rows.any(_up.canUpload)) return;
    setState(() => _busy = true);
    var errors = <String>[];
    try {
      errors = await _up.upload(_rows, l10n: l, onProgress: () {
        if (mounted) setState(() {});
      });
      // 2026-10-22 (мобилка, админ, П12): сбой обновления каталога больше не оставляет экран «занятым» навсегда
      try {
        await ref.read(catalogRepoProvider).sync();
      } catch (_) {}
    } catch (e) {
      errors.add('$e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
    if (!mounted) return;
    final done = _up.uploaded.length;
    // 2026-10-29: «Готово N, с ошибками M» — M: строки с ошибкой (не загружены) и отказы сервера
    final bad = _rows.where((r) => !r.valid).length + errors.length;
    setState(() => _result = l.bkResult(done, bad));
    toast(context, errors.isEmpty ? l.bulkDone(done) : '${l.bulkDone(done)} · ${errors.take(2).join('; ')}', error: errors.isNotEmpty);
    if (errors.isEmpty && bad == 0) Navigator.of(context).maybePop();
  }

  String _problemText(AppLocalizations l, ImportRow r) => [
    for (final p in r.problems)
      switch (p) {
        RowProblem.noName => l.bkErrName,
        RowProblem.noPrice => l.bkErrPrice,
        RowProblem.badUnit => l.bkErrUnit(r.unit),
        RowProblem.qtyWhole => l.bkErrQtyWhole(qtyText(r.qty), r.unit),
        RowProblem.qtyNegative => l.bkErrQtyNeg,
        RowProblem.badBarcode => l.barcodeBadLen,
      },
  ].join('; ');

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final valid = _rows.where((r) => r.valid && !_up.duplicates.containsKey(r)).toList();
    final existing = valid.where((r) => _up.existing.containsKey(r) && !_up.uploaded.contains(r)).length;
    final left = valid.where((r) => !_up.uploaded.contains(r)).length;
    return Scaffold(
      appBar: ScreenTitle(l.bulkImport),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        children: [
          Text(l.bulkHint, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
          const SizedBox(height: 4),
          Text(l.bkHintUnits, key: const Key('bkHintUnits'), style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
          const SizedBox(height: 10),
          FilledButton.icon(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
            onPressed: _busy ? null : _pickFile,
            icon: const Icon(Icons.upload_file_rounded),
            label: Text(l.bulkPickFile),
          ),
          const SizedBox(height: 6),
          // 2026-10-29: Wrap вместо Row — длинная надпись «Вставить скопированное из Excel» больше не обрезается (360 dp, шрифт 1,3)
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              OutlinedButton.icon(onPressed: _busy ? null : _paste, icon: const Icon(Icons.content_paste_rounded, size: 18), label: Text(l.bulkPaste)),
              OutlinedButton.icon(onPressed: _template, icon: const Icon(Icons.description_outlined, size: 18), label: Text(l.bulkTemplate)),
            ],
          ),
          if (_rows.isNotEmpty) ...[
            const SizedBox(height: 12),
            Text(l.bulkSummary(_rows.length, left - existing, existing), style: const TextStyle(fontWeight: FontWeight.w800)),
            const SizedBox(height: 6),
            // 2026-10-22 (мобилка, админ, П12): остаток товара, что уже есть, — прибавить или задать (как касса)
            SegmentedButton<bool>(
              segments: [
                ButtonSegment(value: false, label: Text(l.pAStockAdd)),
                ButtonSegment(value: true, label: Text(l.pAStockSet)),
              ],
              selected: {_up.setStock},
              onSelectionChanged: _busy ? null : (s) => setState(() => _up.setStock = s.first),
            ),
            Text(_up.setStock ? l.pAStockSetHint : l.pAStockAddHint, style: TextStyle(color: pal.textSoft, fontSize: 12)),
            const SizedBox(height: 6),
            // 2026-10-29: массовые операции над отмеченными строками (единица, цена, количество) и выбор «с ошибками»
            const SizedBox(height: 8),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                ActionChip(
                  key: const Key('bkSelectAll'),
                  label: Text(l.bkSelectAll),
                  onPressed: _busy ? null : () => setState(() => _sel..clear()..addAll(_rows.where((r) => !_up.uploaded.contains(r)))),
                ),
                ActionChip(
                  key: const Key('bkSelectBad'),
                  label: Text(l.bkSelectBad),
                  onPressed: _busy ? null : () => setState(() => _sel..clear()..addAll(_rows.where((r) => !r.valid))),
                ),
                if (_sel.isNotEmpty) ActionChip(key: const Key('bkSelectNone'), label: Text(l.bkSelectNone), onPressed: () => setState(_sel.clear)),
              ],
            ),
            Padding(
              padding: const EdgeInsets.only(top: 6, bottom: 4),
              child: Text(_selected.isEmpty ? l.bkSelectFirst : l.bkSelectedN(_selected.length), style: TextStyle(color: pal.textSoft, fontSize: 12)),
            ),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                OutlinedButton(key: const Key('bkMassUnit'), style: OutlinedButton.styleFrom(minimumSize: const Size(64, 48)), onPressed: _busy || _selected.isEmpty ? null : _massUnit, child: Text(l.bkSetUnit)),
                OutlinedButton(key: const Key('bkMassPrice'), style: OutlinedButton.styleFrom(minimumSize: const Size(64, 48)), onPressed: _busy || _selected.isEmpty ? null : _massPrice, child: Text(l.bkSetPrice)),
                OutlinedButton(key: const Key('bkMassQty'), style: OutlinedButton.styleFrom(minimumSize: const Size(64, 48)), onPressed: _busy || _selected.isEmpty ? null : _massQty, child: Text(l.bkSetQty)),
              ],
            ),
            const SizedBox(height: 6),
            for (final r in _rows.take(200))
              ListTile(
                key: ValueKey('bkRow_${identityHashCode(r)}'),
                dense: true,
                contentPadding: EdgeInsets.zero,
                minLeadingWidth: 0,
                onTap: _busy || _up.uploaded.contains(r) ? null : () => _editRow(r),
                leading: Checkbox(
                  value: _sel.contains(r),
                  onChanged: _busy || _up.uploaded.contains(r) ? null : (v) => setState(() => v == true ? _sel.add(r) : _sel.remove(r)),
                ),
                title: Text(r.name.isEmpty ? '—' : r.name),
                subtitle: Text(
                  !r.valid
                      ? _problemText(l, r)
                      : _up.duplicates.containsKey(r)
                      ? l.pADupRow(_up.duplicates[r]!)
                      : [
                          if (r.barcode.isNotEmpty) r.barcode,
                          '${qtyText(r.qty)} ${r.unit}',
                          if (r.noQty) l.bkWarnQty,
                          if (r.category.isNotEmpty) r.category,
                          if (_up.uploaded.contains(r)) l.pAUploaded else if (_up.existing.containsKey(r)) l.bulkExists,
                        ].join(' · '),
                  style: TextStyle(color: r.valid && !_up.duplicates.containsKey(r) ? pal.textSoft : T.danger, fontSize: 12),
                ),
                trailing: Text(money(r.price), style: const TextStyle(fontWeight: FontWeight.w800)),
              ),
            if (_result != null)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(_result!, key: const Key('bkResult'), style: const TextStyle(fontWeight: FontWeight.w800)),
              ),
            const SizedBox(height: 10),
            if (_busy) LinearProgressIndicator(value: valid.isEmpty ? null : _up.uploaded.length / valid.length),
            const SizedBox(height: 8),
            FilledButton(
              style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
              onPressed: _busy || left == 0 ? null : _upload,
              child: Text(l.bulkUpload(left)),
            ),
          ],
        ],
      ),
    );
  }
}

/// 2026-10-29: «Строка товара» — поправить название, штрихкод, единицу, количество, закупку, цену и категорию одной строки файла.
class _RowSheet extends StatefulWidget {
  final ImportRow row;
  const _RowSheet({required this.row});
  @override
  State<_RowSheet> createState() => _RowSheetState();
}

class _RowSheetState extends State<_RowSheet> {
  static String _f(double v) => v == 0 ? '' : qtyText(v).replaceAll(RegExp('[ \u00A0\u202F]'), '');
  late final _name = TextEditingController(text: widget.row.name);
  late final _barcode = TextEditingController(text: widget.row.barcode);
  late final _qty = TextEditingController(text: _f(widget.row.qty));
  late final _cost = TextEditingController(text: _f(widget.row.cost));
  late final _price = TextEditingController(text: _f(widget.row.price));
  late final _category = TextEditingController(text: widget.row.category);
  late String _unit = widget.row.unit;
  bool _unitTouched = false;

  @override
  void dispose() {
    for (final c in [_name, _barcode, _qty, _cost, _price, _category]) {
      c.dispose();
    }
    super.dispose();
  }

  ImportRow get _now => widget.row.copyWith(
    name: _name.text.trim(),
    barcode: _barcode.text.trim(),
    unit: _unit,
    unitOk: widget.row.unitOk || _unitTouched,
    qty: parseNum(_qty.text) ?? 0,
    cost: parseNum(_cost.text) ?? 0,
    price: parseNum(_price.text) ?? 0,
    category: _category.text.trim(),
  );

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final now = _now;
    final problems = now.problems;
    return SafeArea(
      child: Padding(
        padding: EdgeInsets.fromLTRB(16, 16, 16, MediaQuery.viewInsetsOf(context).bottom + 16),
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(l.bkEditRow, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
              FieldLabel(l.peName),
              TextField(key: const Key('bkName'), controller: _name, textCapitalization: TextCapitalization.sentences, onChanged: (_) => setState(() {})),
              FieldLabel(l.barcodeLabel),
              TextField(key: const Key('bkBarcode'), controller: _barcode, keyboardType: TextInputType.number, inputFormatters: barcodeFormatters, onChanged: (_) => setState(() {})),
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        FieldLabel(l.purchasePrice),
                        TextField(key: const Key('bkCost'), controller: _cost, keyboardType: const TextInputType.numberWithOptions(decimal: true), onChanged: (_) => setState(() {})),
                      ],
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        FieldLabel(l.retailPrice),
                        TextField(
                          key: const Key('bkPrice'),
                          controller: _price,
                          keyboardType: const TextInputType.numberWithOptions(decimal: true),
                          onChanged: (_) => setState(() {}),
                          decoration: InputDecoration(errorText: problems.contains(RowProblem.noPrice) ? l.bkErrPrice : null),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              FieldLabel(l.quantityLabel),
              TextField(
                key: const Key('bkQty'),
                controller: _qty,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                onChanged: (_) => setState(() {}),
                decoration: InputDecoration(suffixText: _unit, errorText: qtyErrorText(l, _unit, parseNum(_qty.text))),
              ),
              FieldLabel(l.unitLabel),
              UnitPicker(
                value: _unit,
                onChanged: (u) => setState(() {
                  _unit = u;
                  _unitTouched = true;
                }),
              ),
              if (problems.contains(RowProblem.badUnit))
                Padding(padding: const EdgeInsets.only(top: 4), child: Text(l.bkErrUnit(_unit), style: const TextStyle(color: T.danger, fontSize: 12))),
              FieldLabel(l.categoryLabel),
              TextField(key: const Key('bkCategory'), controller: _category, textCapitalization: TextCapitalization.sentences),
              const SizedBox(height: 14),
              FilledButton(
                key: const Key('bkRowSave'),
                style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                onPressed: () => Navigator.pop(context, _now),
                child: Text(l.save),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
