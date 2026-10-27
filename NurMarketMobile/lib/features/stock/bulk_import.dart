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
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../admin/admin_receive.dart';

/// 13.10, владелец: «на складе нет создания товара и массовой загрузки товара — в мобилке». Массовая загрузка как
/// «Массовая загрузка» кассы (BulkImportWindow): файл Excel (.xlsx) или CSV, либо вставка скопированного из Excel;
/// столбцы находятся по заголовкам (в любом порядке), без заголовков — по порядку из шаблона. Товар с тем же штрихкодом
/// не дублируется — прибавляется количество, обновляются цены (тем же путём, что приёмка без поставщика).
class ImportRow {
  final String name;
  final String barcode;
  final double price;
  final double cost;
  final double qty;
  final String unit;
  final String category;
  const ImportRow({required this.name, this.barcode = '', this.price = 0, this.cost = 0, this.qty = 0, this.unit = 'шт', this.category = ''});
  bool get valid => name.trim().isNotEmpty && price > 0;

  ImportRow copyWith({String? name, String? category}) =>
      ImportRow(name: name ?? this.name, barcode: barcode, price: price, cost: cost, qty: qty, unit: unit, category: category ?? this.category);

  /// 2026-10-27: строка со штрихкодом, но без названия — название (и пустую категорию) берём из встроенного справочника NurCRM,
  /// без сети. Введённое в файле не затирается. Возвращает новые строки и сколько названий подставлено.
  static Future<(List<ImportRow>, int)> fillNames(List<ImportRow> rows, GlobalProducts catalog) async {
    var filled = 0;
    final out = <ImportRow>[];
    for (final r in rows) {
      if (r.name.trim().isNotEmpty || r.barcode.isEmpty) {
        out.add(r);
        continue;
      }
      final g = await catalog.local(r.barcode);
      if (g == null) {
        out.add(r);
        continue;
      }
      filled++;
      out.add(r.copyWith(name: g.name, category: r.category.isEmpty ? g.category : null));
    }
    return (out, filled);
  }
}

class BulkParser {
  static const _fields = {
    'name': ['название', 'наименование', 'товар', 'name', 'аталышы', 'nomi'],
    'barcode': ['штрихкод', 'штрих-код', 'barcode', 'ean', 'код'],
    'price': ['цена продажи', 'цена', 'продажа', 'price', 'сатуу баасы', 'розница'],
    'cost': ['закупка', 'цена закупки', 'себестоимость', 'cost', 'purchase', 'сатып алуу'],
    'qty': ['количество', 'кол-во', 'остаток', 'qty', 'quantity', 'саны'],
    'unit': ['единица', 'ед.', 'ед', 'unit', 'бирдик'],
    'category': ['категория', 'category', 'группа'],
  };
  static const _defaultOrder = ['name', 'barcode', 'price', 'cost', 'qty', 'unit', 'category'];

  static double _num(String s) => double.tryParse(s.replaceAll(RegExp(r'[\s ]'), '').replaceAll(',', '.')) ?? 0;

  /// Строки таблицы → товары. Заголовок узнаём по названиям столбцов; нет заголовка — порядок шаблона.
  static List<ImportRow> parse(List<List<String>> table) {
    final rows = table.where((r) => r.any((c) => c.trim().isNotEmpty)).toList();
    if (rows.isEmpty) return [];
    final header = rows.first.map((c) => c.trim().toLowerCase()).toList();
    final map = <String, int>{};
    for (final entry in _fields.entries) {
      // точное совпадение важнее частичного: «цена закупки» не должна стать «ценой»
      var i = header.indexWhere((h) => entry.value.contains(h));
      if (i < 0) i = header.indexWhere((h) => h.isNotEmpty && entry.value.any((w) => h.startsWith(w)) && !map.values.contains(header.indexOf(h)));
      if (i >= 0 && !map.values.contains(i)) map[entry.key] = i;
    }
    // 2026-10-27: файл «штрихкод + цена + количество» без столбца названий тоже с заголовком — названия подставит справочник NurCRM
    final hasHeader = map.containsKey('name') || (map.containsKey('barcode') && map.length >= 2);
    if (!hasHeader) {
      for (var i = 0; i < _defaultOrder.length; i++) {
        map[_defaultOrder[i]] = i;
      }
    }
    String cell(List<String> r, String f) {
      final i = map[f];
      return i == null || i >= r.length ? '' : r[i].trim();
    }

    return [
      for (final r in hasHeader ? rows.skip(1) : rows)
        ImportRow(
          name: cell(r, 'name'),
          barcode: cell(r, 'barcode').replaceAll(RegExp(r'\.0+$'), ''),
          price: _num(cell(r, 'price')),
          cost: _num(cell(r, 'cost')),
          qty: _num(cell(r, 'qty')),
          unit: cell(r, 'unit').isEmpty ? 'шт' : cell(r, 'unit'),
          category: cell(r, 'category'),
        ),
    ];
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
  Future<void> prepare(AppDatabase db, List<ImportRow> rows) async {
    existing.clear();
    duplicates.clear();
    uploaded.clear();
    _lines.clear();
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
      if (r.barcode.isNotEmpty) {
        final p = (await db.productByScan(r.barcode, allowWeightLabel: false))?.product;
        if (p != null) existing[r] = p.id;
      } else {
        final same = (await db.searchProducts(r.name, limit: 20)).where((p) => _nameKey(p.name) == _nameKey(r.name)).firstOrNull;
        if (same != null) existing[r] = same.id;
      }
    }
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
    setState(() => _rows = rows);
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
    await file.writeAsString('﻿Название;Штрихкод;Цена продажи;Закупка;Количество;Единица;Категория\nКока-Кола 2л;5449000000996;170;130;12;шт;Напитки\n');
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
    toast(context, errors.isEmpty ? l.bulkDone(done) : '${l.bulkDone(done)} · ${errors.take(2).join('; ')}', error: errors.isNotEmpty);
    if (errors.isEmpty) Navigator.of(context).maybePop();
  }

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
          const SizedBox(height: 10),
          FilledButton.icon(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
            onPressed: _busy ? null : _pickFile,
            icon: const Icon(Icons.upload_file_rounded),
            label: Text(l.bulkPickFile),
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _busy ? null : _paste,
                  icon: const Icon(Icons.content_paste_rounded, size: 18),
                  label: Text(l.bulkPaste, maxLines: 1, overflow: TextOverflow.ellipsis),
                ),
              ),
              const SizedBox(width: 6),
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
            for (final r in _rows.take(200))
              ListTile(
                dense: true,
                contentPadding: EdgeInsets.zero,
                title: Text(r.name.isEmpty ? '—' : r.name),
                subtitle: Text(
                  !r.valid
                      ? l.bulkBad
                      : _up.duplicates.containsKey(r)
                      ? l.pADupRow(_up.duplicates[r]!)
                      : [
                          if (r.barcode.isNotEmpty) r.barcode,
                          '${qtyText(r.qty)} ${r.unit}',
                          if (r.category.isNotEmpty) r.category,
                          if (_up.uploaded.contains(r)) l.pAUploaded else if (_up.existing.containsKey(r)) l.bulkExists,
                        ].join(' · '),
                  style: TextStyle(color: r.valid && !_up.duplicates.containsKey(r) ? pal.textSoft : T.danger, fontSize: 12),
                ),
                trailing: Text(money(r.price), style: const TextStyle(fontWeight: FontWeight.w800)),
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
