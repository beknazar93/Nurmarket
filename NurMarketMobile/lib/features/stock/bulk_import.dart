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
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
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
    final hasHeader = map.containsKey('name');
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

class BulkImportScreen extends ConsumerStatefulWidget {
  const BulkImportScreen({super.key});
  @override
  ConsumerState<BulkImportScreen> createState() => _BulkImportScreenState();
}

class _BulkImportScreenState extends ConsumerState<BulkImportScreen> {
  List<ImportRow> _rows = [];
  final Map<String, String> _existing = {}; // штрихкод → id товара
  bool _busy = false;
  int _done = 0;

  Future<void> _setRows(List<ImportRow> rows) async {
    final l = context.l;
    final db = ref.read(databaseProvider);
    _existing.clear();
    for (final r in rows.where((r) => r.barcode.isNotEmpty)) {
      final p = await db.productByBarcode(r.barcode);
      if (p != null) _existing[r.barcode] = p.id;
    }
    if (!mounted) return;
    if (rows.isEmpty) toast(context, l.bulkNothing, error: true);
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
    final valid = _rows.where((r) => r.valid).toList();
    if (valid.isEmpty) return;
    setState(() {
      _busy = true;
      _done = 0;
    });
    final repo = ref.read(adminReceiveRepoProvider);
    final errors = <String>[];
    // по 20 строк: прогресс виден, а одна ошибка не роняет весь файл
    for (var i = 0; i < valid.length; i += 20) {
      final part = valid.skip(i).take(20).toList();
      final lines = [
        for (final r in part)
          ReceiveLine(
            productId: _existing[r.barcode] ?? '',
            name: r.name,
            barcode: r.barcode,
            unit: r.unit,
            qty: r.qty,
            cost: r.cost,
            price: r.price,
            isNew: !_existing.containsKey(r.barcode),
            category: r.category,
          ),
      ];
      try {
        final (n, errs) = await repo.post(lines, null, false);
        errors.addAll(errs);
        // строки без количества приёмка не проводит — товар при этом уже создан
        _done += n;
        // без количества приёмка строку не проводит: новый товар уже создан, у существующего — только цены
        for (final x in lines.where((x) => x.qty <= 0 && x.productId.isNotEmpty)) {
          if (!x.isNew) {
            await ref
                .read(apiProvider)
                .patch(
                  'api/main/products/${x.productId}/',
                  body: {'price': x.price.toStringAsFixed(2), if (x.cost > 0) 'purchase_price': x.cost.toStringAsFixed(2)},
                );
          }
          _done++;
        }
      } on ApiException catch (e) {
        errors.add(e.message);
      }
      if (mounted) setState(() {});
    }
    await ref.read(catalogRepoProvider).sync();
    if (!mounted) return;
    setState(() => _busy = false);
    toast(context, errors.isEmpty ? l.bulkDone(_done) : '${l.bulkDone(_done)} · ${errors.take(2).join('; ')}', error: errors.isNotEmpty);
    if (errors.isEmpty) Navigator.of(context).maybePop();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final valid = _rows.where((r) => r.valid).toList();
    final existing = valid.where((r) => _existing.containsKey(r.barcode)).length;
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
            Text(l.bulkSummary(_rows.length, valid.length - existing, existing), style: const TextStyle(fontWeight: FontWeight.w800)),
            const SizedBox(height: 6),
            for (final r in _rows.take(200))
              ListTile(
                dense: true,
                contentPadding: EdgeInsets.zero,
                title: Text(r.name.isEmpty ? '—' : r.name),
                subtitle: Text(
                  !r.valid
                      ? l.bulkBad
                      : [
                          if (r.barcode.isNotEmpty) r.barcode,
                          '${qtyText(r.qty)} ${r.unit}',
                          if (r.category.isNotEmpty) r.category,
                          if (_existing.containsKey(r.barcode)) l.bulkExists,
                        ].join(' · '),
                  style: TextStyle(color: r.valid ? pal.textSoft : T.danger, fontSize: 12),
                ),
                trailing: Text(money(r.price), style: const TextStyle(fontWeight: FontWeight.w800)),
              ),
            const SizedBox(height: 10),
            if (_busy) LinearProgressIndicator(value: valid.isEmpty ? null : _done / valid.length),
            const SizedBox(height: 8),
            FilledButton(
              style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
              onPressed: _busy || valid.isEmpty ? null : _upload,
              child: Text(l.bulkUpload(valid.length)),
            ),
          ],
        ],
      ),
    );
  }
}
