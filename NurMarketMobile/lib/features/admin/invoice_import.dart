import 'dart:async';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../core/api/api_client.dart';
import '../../core/catalog/global_products.dart';
import '../../core/db/database.dart';
import '../../core/invoice/invoice_match.dart';
import '../../core/invoice/invoice_parser.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/barcode_generator.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import 'admin_receive.dart';

/// 2026-10-27 (владелец: «в мобилке и в десктопе добавь возможность загрузки электронных накладных»): «Загрузить накладную».
/// Файл поставщика (Excel / CSV / XML) → разбор ([InvoiceParser]) → предпросмотр: поставщик (по ИНН/названию), номер и дата,
/// «уже загружалась», строки со статусом (на складе / новый / не распознана), правка количества, закупки, наценки и цены,
/// итог по строкам против итога файла → «Добавить в приход» отдаёт строки обычному экрану прихода ([AdminReceiveScreen]),
/// проведение — прежним путём ([AdminReceiveRepository.post]). PDF и фото накладной читает «Умная камера» (путь не новый).
class InvoiceImportScreen extends ConsumerStatefulWidget {
  /// true — открыт из экрана прихода: строки возвращаются ему ([ReceiveHandoff]); false — открывается новый приход.
  final bool forReceive;

  /// 2026-10-27 (владелец: «в умной камере в накладной добавь выбор файла Excel и CSV»): файл уже выбран (в «Умной камере») —
  /// экран сразу разбирает его, не просит выбрать ещё раз.
  final InvoiceFileArg? initialFile;

  /// Только для проверок без выбора файла и сети.
  @visibleForTesting
  final ParsedInvoice? debugInvoice;
  @visibleForTesting
  final List<SupplierRef>? debugSuppliers;
  const InvoiceImportScreen({super.key, this.forReceive = false, this.initialFile, this.debugInvoice, this.debugSuppliers});

  @override
  ConsumerState<InvoiceImportScreen> createState() => _InvoiceImportScreenState();
}

/// 2026-10-27: выбранный файл накладной (байты и имя) — передаётся в [InvoiceImportScreen] через `extra` маршрута.
class InvoiceFileArg {
  final Uint8List bytes;
  final String name;
  const InvoiceFileArg(this.bytes, this.name);
}

/// 2026-10-27: разбор в отдельном изоляте. Замыкание создаётся здесь, а не в методе экрана: в методе экрана оно делило контекст
/// с `setState(() …)` и тянуло за собой весь State (а с ним Element и RenderObject) — «object is unsendable», любой файл
/// выдавал «Файл повреждён».
Future<ParsedInvoice> _parseOffThread(Uint8List bytes, String name) => Isolate.run(() => InvoiceParser.parse(bytes, fileName: name));

/// Цена продажи по правилу прихода: +30 % к закупке, вверх до 5 сом.
double invoiceAutoPrice(double cost, [double pct = 30]) => cost <= 0 ? 0 : ((cost * (1 + pct / 100) / 5).ceil() * 5).toDouble();

/// 2026-10-27 (ревью М-5 / Ск-11): как принять строку накладной в упаковках. unset — кассир ещё не выбрал (приход не добавляется).
enum PackChoice { unset, pieces, asIs }

/// Штук в упаковке по доп. штрихкоду товара (код упаковки с количеством); null — код не упаковочный или количество неизвестно.
int? invoicePackSize(Product p, InvoiceLine l) {
  for (final c in {l.barcode, l.article, ...invoiceCodeVariants(l.barcode)}) {
    if (c.isEmpty) continue;
    final q = AppDatabase.packQuantity(p, c);
    if (q != null && q >= 2 && q == q.roundToDouble()) return q.round();
  }
  return null;
}

class _Row {
  final InvoiceLine src;
  final LineMatch match;

  /// Упаковки: выбор кассира и штук в упаковке (при выборе «в штуки»).
  PackChoice packChoice = PackChoice.unset;
  int pack = 1;
  final packCtl = TextEditingController();

  /// «Похожий товар»: true — это он (количество пойдёт ему), false — новый товар.
  bool useCandidate;
  final name = TextEditingController();
  final qty = TextEditingController();
  final cost = TextEditingController();
  final markup = TextEditingController();
  final price = TextEditingController();

  /// Цена продажи вписана вручную (или взята со склада) — закупка её не пересчитывает.
  bool priceManual = false;

  /// Закупку правили руками — переключатель «с НДС» её не трогает.
  bool costEdited = false;

  _Row(this.src, this.match) : useCandidate = match.autoPick;

  Product? get product => switch (match.kind) {
    MatchKind.barcode || MatchKind.article || MatchKind.name => match.product,
    MatchKind.fuzzy => useCandidate ? match.product : null,
    MatchKind.none => null,
  };
  bool get bad => !src.valid;

  /// Строка пришла в упаковках, а товар склада считается в штуках (или код — упаковочный): тихо принять нельзя.
  bool get packSuspect {
    final p = product;
    if (p == null || bad || isPackUnit(p.unit)) return false;
    return isPackUnit(src.unit) || invoicePackSize(p, src) != null;
  }

  bool get needsPackChoice => packSuspect && packChoice == PackChoice.unset;

  /// Закупка за единицу прихода: за штуку, если упаковки пересчитаны в штуки.
  double baseCost(bool withVat) => src.unitCost(withVat: withVat) / (packChoice == PackChoice.pieces ? pack : 1);
  double get qtyV => parseNum(qty.text) ?? 0;
  double get costV => parseNum(cost.text) ?? 0;
  double get priceV => parseNum(price.text) ?? 0;

  /// Строка готова к приходу.
  bool get ready => !bad && !needsPackChoice && qtyV > 0 && costV > 0 && priceV > 0 && (product != null || name.text.trim().isNotEmpty);

  void dispose() {
    for (final c in [name, qty, cost, markup, price, packCtl]) {
      c.dispose();
    }
  }
}

class _InvoiceImportScreenState extends ConsumerState<InvoiceImportScreen> {
  ParsedInvoice? _inv;
  String _fileName = '';
  bool _busy = false;
  bool _adding = false;
  String? _error;
  final List<_Row> _rows = [];
  List<SupplierRef> _suppliers = [];
  bool _suppliersFailed = false;
  SupplierRef? _supplier;
  SupplierBy _supplierBy = SupplierBy.none;
  String? _dup; // posted | pending
  bool _dupOk = false;

  /// 2026-10-27 (ревью Ск-14): накладная не в сомах — кассир подтверждает, что суммы проверил (1 валюта = 1 сом).
  bool _curOk = false;
  bool _withVat = false;
  bool _genCodes = false;

  static final _date = DateFormat('dd.MM.yyyy');

  bool _foreignCurrency(ParsedInvoice? inv) => inv != null && inv.currency.isNotEmpty && inv.currency != 'KGS';

  /// 2026-10-27 (ревью Ск-11): единица накладной (шт / кг / л) не совпала с единицей карточки товара — проверить.
  static bool unitDiffers(String invoiceUnit, String productUnit) {
    if (invoiceUnit.trim().isEmpty || isPackUnit(invoiceUnit) || isPackUnit(productUnit)) return false;
    const base = {'шт', 'кг', 'л'};
    final a = normalizeInvoiceUnit(invoiceUnit), b = normalizeInvoiceUnit(productUnit);
    return base.contains(a) && base.contains(b) && a != b;
  }

  @override
  void initState() {
    super.initState();
    final d = widget.debugInvoice;
    if (d != null) WidgetsBinding.instance.addPostFrameCallback((_) => _prepare(d, 'invoice.xlsx'));
    final f = widget.initialFile;
    if (f != null) WidgetsBinding.instance.addPostFrameCallback((_) => _load(f.bytes, f.name));
  }

  @override
  void dispose() {
    for (final r in _rows) {
      r.dispose();
    }
    super.dispose();
  }

  String _errorText(AppLocalizations l, InvoiceError e) => switch (e) {
    InvoiceError.tooBig => l.invErrTooBig,
    InvoiceError.empty => l.invErrEmpty,
    InvoiceError.pdf || InvoiceError.image => l.invErrPdf,
    InvoiceError.xls => l.invErrXls,
    InvoiceError.unsupported => l.invErrFormat,
    InvoiceError.broken => l.invErrBroken,
    InvoiceError.noTable => l.invErrNoTable,
    InvoiceError.unsafeXml => l.invErrUnsafe,
    InvoiceError.tooMany => l.invErrTooMany,
  };

  Future<void> _pick() async {
    final l = context.l;
    try {
      final files = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['xlsx', 'csv', 'xml', 'txt', 'tsv']);
      final f = files.firstOrNull;
      if (f == null || !mounted) return;
      final len = await f.length();
      if (len != null && len > InvoiceParser.maxBytes) {
        setState(() => _error = l.invErrTooBig);
        return;
      }
      final bytes = await f.readAsBytes();
      await _load(Uint8List.fromList(bytes), f.name);
    } catch (_) {
      if (mounted) setState(() => _error = l.invErrBroken);
    }
  }

  Future<void> _load(Uint8List bytes, String name) async {
    final l = context.l;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      // разбор тяжёлого файла — в отдельном изоляте, экран не замирает
      final inv = await _parseOffThread(bytes, name);
      if (!mounted) return;
      await _prepare(inv, name);
    } on InvoiceParseException catch (e) {
      if (mounted) setState(() => _error = _errorText(l, e.error));
    } catch (_) {
      if (mounted) setState(() => _error = l.invErrBroken);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _initRow(_Row r, {required bool resetPrice}) {
    final cost = r.baseCost(_withVat);
    if (resetPrice || r.price.text.isEmpty) {
      final p = r.product;
      double price;
      if (r.src.retail > 0) {
        price = r.src.retail;
        r.priceManual = true;
      } else if (p != null && p.price > 0 && p.price >= cost) {
        price = p.price; // цена на складе не меняется, пока закупка её не обогнала
        r.priceManual = true;
      } else {
        price = invoiceAutoPrice(cost);
        r.priceManual = false;
      }
      r.price.text = _num(price);
      r.markup.text = cost > 0 && price > 0 ? _num((price - cost) / cost * 100, 1) : '30';
    }
  }

  static String _num(double v, [int digits = 2]) {
    if (v == 0) return '';
    final s = v.toStringAsFixed(digits);
    return s.contains('.') ? s.replaceAll(RegExp(r'0+$'), '').replaceAll(RegExp(r'\.$'), '') : s;
  }

  Future<void> _prepare(ParsedInvoice inv, String name) async {
    final db = ref.read(databaseProvider);
    final api = ref.read(apiProvider);
    GlobalProducts? catalog;
    try {
      catalog = ref.read(globalProductsProvider);
    } catch (_) {}
    if (mounted) setState(() => _busy = true);
    // поставщики (нет связи — выбрать позже вручную нельзя, поэтому покажем подсказку)
    var suppliers = widget.debugSuppliers ?? <SupplierRef>[];
    var failed = false;
    if (widget.debugSuppliers == null) {
      try {
        suppliers = await fetchSuppliers(api).timeout(const Duration(seconds: 10));
      } catch (_) {
        failed = true;
      }
    }
    final sup = matchSupplier(suppliers, name: inv.supplierName, inn: inv.supplierInn);
    final matcher = InvoiceMatcher(db, catalog);
    final rows = <_Row>[];
    for (final line in inv.lines) {
      final m = line.valid ? await matcher.find(line) : const LineMatch(MatchKind.none);
      final row = _Row(line, m);
      if (!line.valid) {
        rows.add(row);
        continue;
      }
      row.name.text = line.name.trim().isNotEmpty ? line.name.trim() : (m.global?.name ?? line.article);
      row.qty.text = _num(line.qty, 3);
      row.cost.text = _num(line.unitCost(withVat: _withVat));
      _initRow(row, resetPrice: true);
      rows.add(row);
    }
    if (!mounted) {
      for (final r in rows) {
        r.dispose();
      }
      return;
    }
    for (final r in _rows) {
      r.dispose();
    }
    setState(() {
      _inv = inv;
      _fileName = name;
      _suppliers = suppliers;
      _suppliersFailed = failed;
      _supplier = sup.supplier;
      _supplierBy = sup.by;
      _rows
        ..clear()
        ..addAll(rows);
      _withVat = false;
      _dupOk = false;
      _curOk = false;
      _busy = false;
    });
    await _checkDup();
  }

  String get _supplierKey => _supplier?.id ?? (_inv?.supplierInn.isNotEmpty == true ? _inv!.supplierInn : (_inv?.supplierName ?? ''));

  Future<void> _checkDup() async {
    final inv = _inv;
    if (inv == null) return;
    // 2026-10-27 (ревью Ск-2): ключи не зависят от поставщика; прежние (с поставщиком) — только чтобы узнать старую запись
    final st = await InvoiceGuard.status(ref.read(databaseProvider), [...InvoiceGuard.keys(inv), ...InvoiceGuard.legacyKeys(inv, _supplierKey)]);
    if (mounted) setState(() => _dup = st);
  }

  // ---------------------------------------------------------------------------------------------- правки строк

  void _setText(TextEditingController c, String text) {
    if (c.text != text) c.value = TextEditingValue(text: text, selection: TextSelection.collapsed(offset: text.length));
  }

  void _onCost(_Row r) {
    r.costEdited = true;
    final c = r.costV;
    if (!r.priceManual) {
      final pct = parseNum(r.markup.text) ?? 30;
      _setText(r.price, _num(invoiceAutoPrice(c, pct)));
    } else if (c > 0 && r.priceV > 0) {
      _setText(r.markup, _num((r.priceV - c) / c * 100, 1));
    }
    setState(() {});
  }

  void _onMarkup(_Row r) {
    r.priceManual = false;
    final pct = parseNum(r.markup.text);
    if (pct != null && r.costV > 0) _setText(r.price, _num(invoiceAutoPrice(r.costV, pct)));
    setState(() {});
  }

  void _onPrice(_Row r) {
    r.priceManual = true;
    if (r.costV > 0 && r.priceV > 0) _setText(r.markup, _num((r.priceV - r.costV) / r.costV * 100, 1));
    setState(() {});
  }

  void _setVat(bool v) {
    setState(() {
      _withVat = v;
      for (final r in _rows.where((r) => !r.bad && !r.costEdited)) {
        _setText(r.cost, _num(r.baseCost(v), 4));
        if (!r.priceManual) _setText(r.price, _num(invoiceAutoPrice(r.costV, parseNum(r.markup.text) ?? 30)));
      }
    });
  }

  /// 2026-10-27 (ревью М-5): «Пересчитать в штуки» — количество × штук в упаковке, закупка ÷ штук в упаковке.
  void _packToPieces(_Row r) {
    final n = int.tryParse(r.packCtl.text.trim());
    if (n == null || n < 2 || n > 10000) {
      toast(context, context.l.invPackBad, error: true);
      return;
    }
    setState(() {
      r.packChoice = PackChoice.pieces;
      r.pack = n;
      _setText(r.qty, _num(r.src.qty * n, 3));
      r.costEdited = false;
      _setText(r.cost, _num(r.baseCost(_withVat), 4));
      _initRow(r, resetPrice: true);
    });
  }

  /// «Принять как есть»: количество упаковок идёт в остаток как есть, по цене упаковки (явный выбор кассира).
  void _packAsIs(_Row r) {
    setState(() {
      r.packChoice = PackChoice.asIs;
      r.pack = 1;
      _setText(r.qty, _num(r.src.qty, 3));
      r.costEdited = false;
      _setText(r.cost, _num(r.baseCost(_withVat), 4));
      _initRow(r, resetPrice: true);
    });
  }

  /// Сбросить выбор упаковок (без setState): строка снова с количеством и закупкой из файла.
  void _packFields(_Row r) {
    r.packChoice = PackChoice.unset;
    r.pack = 1;
    r.packCtl.clear();
    _setText(r.qty, _num(r.src.qty, 3));
    r.costEdited = false;
    _setText(r.cost, _num(r.baseCost(_withVat), 4));
  }

  void _packReset(_Row r) {
    setState(() {
      _packFields(r);
      _initRow(r, resetPrice: true);
    });
  }

  Widget _packBox(AppLocalizations l, Palette pal, _Row r, Product p) {
    if (r.packChoice != PackChoice.unset) {
      final text = r.packChoice == PackChoice.pieces
          ? l.invPackDonePieces(r.pack, qtyText(r.qtyV), money(r.costV))
          : l.invPackDoneAsIs(qtyText(r.qtyV), p.unit, money(r.costV));
      return Padding(
        padding: const EdgeInsets.only(top: 6),
        child: Row(
          children: [
            Icon(Icons.inventory_rounded, size: 18, color: pal.success),
            const SizedBox(width: 6),
            Expanded(child: Text(text, style: TextStyle(color: pal.success, fontWeight: FontWeight.w700, fontSize: 12.5))),
            TextButton(key: ValueKey('packReset_${r.src.row}'), onPressed: () => _packReset(r), child: Text(l.invPackChange)),
          ],
        ),
      );
    }
    final known = invoicePackSize(p, r.src);
    if (r.packCtl.text.isEmpty && known != null) r.packCtl.text = '$known';
    return Padding(
      padding: const EdgeInsets.only(top: 6),
      child: Panel(
        color: pal.warnSoft,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              l.invPackWarn(r.src.unit.trim().isEmpty ? '—' : r.src.unit.trim(), p.name, p.unit),
              style: TextStyle(color: pal.warn, fontWeight: FontWeight.w700, fontSize: 12.5),
            ),
            const SizedBox(height: 6),
            TextField(
              key: ValueKey('packN_${r.src.row}'),
              controller: r.packCtl,
              keyboardType: TextInputType.number,
              decoration: InputDecoration(labelText: l.invPackSize, isDense: true),
            ),
            const SizedBox(height: 6),
            Wrap(
              spacing: 8,
              runSpacing: 6,
              children: [
                FilledButton(key: ValueKey('packPieces_${r.src.row}'), style: FilledButton.styleFrom(minimumSize: const Size(48, 48)), onPressed: () => _packToPieces(r), child: Text(l.invPackToPieces)),
                OutlinedButton(key: ValueKey('packAsIs_${r.src.row}'), style: OutlinedButton.styleFrom(minimumSize: const Size(48, 48)), onPressed: () => _packAsIs(r), child: Text(l.invPackAsIs)),
              ],
            ),
          ],
        ),
      ),
    );
  }

  void _remove(_Row r) {
    setState(() => _rows.remove(r));
    r.dispose();
  }

  double get _liveTotal => _rows.where((r) => !r.bad).fold(0.0, (s, r) => s + r.qtyV * r.costV);

  // ---------------------------------------------------------------------------------------------- поставщик

  Future<void> _createSupplier() async {
    final l = context.l;
    final inv = _inv!;
    final name = TextEditingController(text: inv.supplierName);
    final inn = TextEditingController(text: inv.supplierInn);
    final phone = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.invSupCreate),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(controller: name, decoration: InputDecoration(labelText: l.supplierName)),
              const SizedBox(height: 8),
              TextField(controller: inn, keyboardType: TextInputType.number, decoration: InputDecoration(labelText: l.innLabel)),
              const SizedBox(height: 8),
              TextField(controller: phone, keyboardType: TextInputType.phone, decoration: InputDecoration(labelText: l.phone)),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, name.text.trim().isNotEmpty), child: Text(l.createDoc)),
        ],
      ),
    );
    final n = name.text.trim(), i = inn.text.trim(), p = phone.text.trim();
    name.dispose();
    inn.dispose();
    phone.dispose();
    if (ok != true || !mounted) return;
    try {
      final s = await createSupplier(ref.read(apiProvider), name: n, phone: p, inn: i);
      if (!mounted) return;
      setState(() {
        _suppliers = [..._suppliers, s];
        _supplier = s;
        _supplierBy = SupplierBy.name;
      });
      toast(context, l.invSupCreated(s.name));
      await _checkDup();
    } on ApiException catch (e) {
      if (mounted) toast(context, l.invSupFail(e.message), error: true);
    } catch (e) {
      if (mounted) toast(context, l.invSupFail('$e'), error: true);
    }
  }

  // ---------------------------------------------------------------------------------------------- в приход

  Future<void> _addToReceipt() async {
    if (_adding) return;
    final inv = _inv;
    if (inv == null) return;
    final l = context.l;
    final ready = _rows.where((r) => !r.bad).toList();
    if (ready.isEmpty || ready.any((r) => !r.ready)) {
      toast(context, l.invAddFix, error: true);
      return;
    }
    setState(() => _adding = true);
    try {
      final db = ref.read(databaseProvider);
      final api = ref.read(apiProvider);
      // повторы одного товара в накладной — одна строка (закупка — средневзвешенная)
      final merged = <String, ({_Row first, double qty, double sum})>{};
      var mergedCount = 0;
      for (final r in ready) {
        final p = r.product;
        final key = p != null ? 'p:${p.id}' : (r.src.barcode.isNotEmpty ? 'b:${r.src.barcode}' : 'n:${invoiceNameKey(r.name.text)}');
        final old = merged[key];
        if (old == null) {
          merged[key] = (first: r, qty: r.qtyV, sum: r.qtyV * r.costV);
        } else {
          mergedCount++;
          merged[key] = (first: old.first, qty: old.qty + r.qtyV, sum: old.sum + r.qtyV * r.costV);
        }
      }
      // прежняя закупка товаров склада — чтобы приход показал «закупка выросла»; без связи — нули
      final oldCost = <String, double>{};
      final ids = {for (final m in merged.values) if (m.first.product != null) m.first.product!.id};
      final deadline = DateTime.now().add(const Duration(seconds: 20));
      final queue = ids.toList();
      Future<void> worker() async {
        while (queue.isNotEmpty && DateTime.now().isBefore(deadline)) {
          final id = queue.removeLast();
          try {
            final m = (await api.get('api/main/products/$id/', timeout: const Duration(seconds: 5))) as Map;
            oldCost[id] = num0(m.cast<String, dynamic>(), 'purchase_price');
          } catch (_) {}
        }
      }

      await Future.wait([for (var i = 0; i < 6; i++) worker()]);
      final taken = <String>{};
      final lines = <ReceiveLine>[];
      for (final m in merged.values) {
        final r = m.first;
        final p = r.product;
        final cost = (m.sum / m.qty * 100).round() / 100;
        final unit = p?.unit ?? normalizeInvoiceUnit(r.src.unit);
        if (p != null) {
          lines.add(
            ReceiveLine(
              productId: p.id,
              name: p.name,
              barcode: p.barcode ?? '',
              unit: unit,
              before: p.quantity,
              oldCost: oldCost[p.id] ?? 0,
              oldPrice: p.price,
              qty: roundQty(m.qty),
              cost: cost,
              price: r.priceV,
            ),
          );
        } else {
          var code = isBarcodeLike(r.src.barcode) ? r.src.barcode : '';
          if (code.isEmpty && _genCodes) {
            code = await BarcodeGenerator.createFree((c) async => taken.contains(c) || await db.productByBarcode(c) != null);
            taken.add(code);
          }
          lines.add(
            ReceiveLine(
              productId: '',
              name: r.name.text.trim(),
              barcode: code,
              unit: unit,
              qty: roundQty(m.qty),
              cost: cost,
              price: r.priceV,
              isNew: true,
              category: r.match.global?.category ?? '',
            ),
          );
        }
      }
      if (!mounted) return;
      final keys = InvoiceGuard.keys(inv);
      InvoiceGuard.markPending(keys);
      final handoff = ReceiveHandoff(
        lines,
        supplierId: _supplier?.id,
        supplierName: _supplier?.name ?? inv.supplierName,
        invoiceKeys: keys,
        onPosted: () => InvoiceGuard.markPosted(db, keys, number: inv.number, supplier: _supplier?.name ?? inv.supplierName),
      );
      if (mergedCount > 0) toast(context, l.invMerged(mergedCount));
      if (widget.forReceive) {
        context.pop(handoff);
      } else {
        context.pushReplacement('/admin/receive', extra: handoff);
      }
    } finally {
      if (mounted) setState(() => _adding = false);
    }
  }

  // ---------------------------------------------------------------------------------------------- экран

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final inv = _inv;
    final okRows = _rows.where((r) => !r.bad).toList();
    final canAdd = inv != null && okRows.isNotEmpty && okRows.every((r) => r.ready) && (_dup == null || _dupOk) && (!_foreignCurrency(inv) || _curOk) && !_adding && !_busy;
    return Scaffold(
      appBar: ScreenTitle(l.invTitle, subtitle: l.invSub),
      body: Column(
        children: [
          Expanded(
            child: ListView(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
              children: [
                if (inv == null) ..._startBlock(l, pal) else ..._previewBlock(l, pal, inv),
              ],
            ),
          ),
          if (inv != null)
            SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
                child: FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
                  onPressed: canAdd ? _addToReceipt : null,
                  child: _adding
                      ? Text(l.invAdding)
                      : Text(okRows.any((r) => !r.ready) ? l.invAddFix : l.invAdd(okRows.length), textAlign: TextAlign.center),
                ),
              ),
            ),
        ],
      ),
    );
  }

  List<Widget> _startBlock(AppLocalizations l, Palette pal) => [
    Text(l.invHint, style: TextStyle(color: pal.textSoft, fontSize: 13)),
    const SizedBox(height: 12),
    if (_error != null) ...[_ErrorBox(_error!), const SizedBox(height: 12)],
    FilledButton.icon(
      style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
      onPressed: _busy ? null : _pick,
      icon: const Icon(Icons.upload_file_rounded),
      label: Text(_busy ? l.invReading : l.invPick),
    ),
    if (_busy) const Padding(padding: EdgeInsets.only(top: 12), child: LinearProgressIndicator()),
    const SizedBox(height: 20),
    Text(l.invPhotoHint, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
    const SizedBox(height: 8),
    OutlinedButton.icon(
      style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(50)),
      onPressed: _busy ? null : () => context.push('/admin/smartcam?mode=invoice'),
      icon: const Icon(Icons.photo_camera_rounded),
      label: Text(l.invPhotoBtn),
    ),
  ];

  String _docLine(AppLocalizations l, ParsedInvoice inv) {
    final num = inv.number.trim();
    final d = inv.date == null ? '' : _date.format(inv.date!);
    if (num.isNotEmpty && d.isNotEmpty) return l.invDocLine(num, d);
    if (num.isNotEmpty) return l.invDocNum(num);
    if (d.isNotEmpty) return l.invDocDate(d);
    return l.invDocNone;
  }

  List<Widget> _previewBlock(AppLocalizations l, Palette pal, ParsedInvoice inv) {
    final live = _liveTotal;
    final fileTotal = inv.fileTotal;
    final tol = fileTotal == null ? 0.0 : (fileTotal.abs() * 0.001 < 1 ? 1.0 : fileTotal.abs() * 0.001);
    final mismatch = fileTotal != null && (live - fileTotal).abs() > tol;
    final vatFits = mismatch && inv.hasVat && (inv.linesTotal(withVat: !_withVat) - fileTotal).abs() <= tol;
    final stock = _rows.where((r) => !r.bad && r.product != null).length;
    final fresh = _rows.where((r) => !r.bad && r.product == null).length;
    final bad = _rows.where((r) => r.bad).length;
    final needCodes = _rows.any((r) => !r.bad && r.product == null && !isBarcodeLike(r.src.barcode));
    final cur = inv.currency;
    return [
      Row(
        children: [
          Expanded(child: Text(l.invFileName(_fileName), style: TextStyle(color: pal.textSoft, fontSize: 12.5))),
          TextButton(onPressed: _busy ? null : _pick, child: Text(l.invOtherFile)),
        ],
      ),
      if (_error != null) ...[_ErrorBox(_error!), const SizedBox(height: 8)],
      if (cur.isNotEmpty && cur != 'KGS') ...[
        _WarnBox(l.invCurrency(cur)),
        CheckboxListTile(
          key: const Key('curOk'),
          contentPadding: EdgeInsets.zero,
          value: _curOk,
          onChanged: (v) => setState(() => _curOk = v ?? false),
          title: Text(l.invCurrencyOk(cur)),
          controlAffinity: ListTileControlAffinity.leading,
        ),
        const SizedBox(height: 8),
      ],
      if (inv.costFromGross) ...[_WarnBox(l.invGrossOnly), const SizedBox(height: 8)],
      Panel(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(_docLine(l, inv), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
            const SizedBox(height: 8),
            _supplierBlock(l, pal, inv),
          ],
        ),
      ),
      if (_dup != null) ...[
        const SizedBox(height: 8),
        _WarnBox(_dup == 'posted' ? l.invDupPosted : l.invDupPending),
        CheckboxListTile(
          contentPadding: EdgeInsets.zero,
          value: _dupOk,
          onChanged: (v) => setState(() => _dupOk = v ?? false),
          title: Text(l.invDupAnyway),
          controlAffinity: ListTileControlAffinity.leading,
        ),
      ],
      const SizedBox(height: 8),
      Panel(
        color: mismatch ? pal.warnSoft : null,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l.invTotalLines(money(live)), style: const TextStyle(fontWeight: FontWeight.w800)),
            if (fileTotal != null) Text(l.invTotalFile(money(fileTotal)), style: TextStyle(color: pal.textSoft)),
            const SizedBox(height: 4),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(
                  fileTotal == null ? Icons.info_outline_rounded : (mismatch ? Icons.warning_amber_rounded : Icons.check_circle_outline_rounded),
                  size: 18,
                  color: fileTotal == null ? pal.textSoft : (mismatch ? pal.warn : pal.success),
                ),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    fileTotal == null ? l.invTotalNone : (mismatch ? l.invTotalBad(money((live - fileTotal).abs())) : l.invTotalOk),
                    style: TextStyle(color: fileTotal == null ? pal.textSoft : (mismatch ? pal.warn : pal.success), fontWeight: FontWeight.w700, fontSize: 12.5),
                  ),
                ),
              ],
            ),
            if (vatFits) Text(l.invTotalVatHint, style: TextStyle(color: pal.warn, fontSize: 12.5)),
          ],
        ),
      ),
      if (inv.hasVat) SwitchListTile(contentPadding: EdgeInsets.zero, value: _withVat, onChanged: _setVat, title: Text(l.invVat), subtitle: Text(l.invVatSub)),
      if (needCodes)
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          value: _genCodes,
          onChanged: (v) => setState(() => _genCodes = v),
          title: Text(l.invGenCodes),
          subtitle: Text(l.invGenCodesSub),
        ),
      const SizedBox(height: 4),
      Text(l.invSummary(_rows.length, '$stock', '$fresh', '$bad'), style: const TextStyle(fontWeight: FontWeight.w800)),
      const SizedBox(height: 8),
      for (final r in _rows) Padding(padding: const EdgeInsets.only(bottom: 8), child: _rowCard(l, pal, r)),
    ];
  }

  Widget _supplierBlock(AppLocalizations l, Palette pal, ParsedInvoice inv) {
    final fileLine = inv.supplierName.isNotEmpty
        ? (inv.supplierInn.isNotEmpty ? l.invSupplierFileInn(inv.supplierName, inv.supplierInn) : l.invSupplierFile(inv.supplierName))
        : (inv.supplierInn.isNotEmpty ? l.invSupplierInnOnly(inv.supplierInn) : l.invSupplierNoneInFile);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(fileLine, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
        const SizedBox(height: 8),
        DropdownButtonFormField<String?>(
          key: ValueKey('sup-${_supplier?.id}'),
          initialValue: _supplier?.id,
          isExpanded: true,
          decoration: InputDecoration(labelText: l.supplierName),
          items: [
            DropdownMenuItem<String?>(value: null, child: Text(l.noSupplier)),
            for (final s in _suppliers) DropdownMenuItem<String?>(value: s.id, child: Text(s.name, overflow: TextOverflow.ellipsis)),
          ],
          onChanged: (v) {
            setState(() {
              _supplier = _suppliers.where((s) => s.id == v).firstOrNull;
              _supplierBy = SupplierBy.none;
            });
            _checkDup();
          },
        ),
        const SizedBox(height: 6),
        if (_supplier != null && _supplierBy != SupplierBy.none)
          Row(
            children: [
              Icon(Icons.check_circle_outline_rounded, size: 18, color: pal.success),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  _supplierBy == SupplierBy.inn ? l.invSupByInn : l.invSupByName,
                  style: TextStyle(color: pal.success, fontWeight: FontWeight.w700, fontSize: 12.5),
                ),
              ),
            ],
          )
        else if (_supplier == null && (inv.supplierName.isNotEmpty || inv.supplierInn.isNotEmpty)) ...[
          Text(_suppliersFailed ? l.invSupOffline : l.invSupNotFound, style: TextStyle(color: pal.warn, fontSize: 12.5)),
          if (inv.supplierName.isNotEmpty)
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: TextButton.icon(onPressed: _createSupplier, icon: const Icon(Icons.add_rounded), label: Text(l.invSupCreate)),
            ),
        ],
      ],
    );
  }

  (String, Color, IconData) _status(AppLocalizations l, Palette pal, _Row r) {
    if (r.bad) return (l.invStBad, pal.danger, Icons.error_outline_rounded);
    switch (r.match.kind) {
      case MatchKind.barcode:
        return (l.invStBarcode, pal.success, Icons.inventory_2_outlined);
      case MatchKind.article:
        return (l.invStArticle, pal.success, Icons.inventory_2_outlined);
      case MatchKind.name:
        return (l.invStName, pal.success, Icons.inventory_2_outlined);
      case MatchKind.fuzzy:
        // «похоже» показывается всегда (выбор — кнопками ниже); принятый кандидат отмечен галочкой-складом
        return (l.invStFuzzy(r.match.product!.name), r.useCandidate ? pal.success : pal.warn, r.useCandidate ? Icons.inventory_2_outlined : Icons.help_outline_rounded);
      case MatchKind.none:
        return r.match.global != null ? (l.invStCatalog, pal.warn, Icons.fiber_new_rounded) : (l.invStManual, pal.warn, Icons.fiber_new_rounded);
    }
  }

  Widget _field(String label, TextEditingController c, VoidCallback onChanged, {bool error = false}) => Expanded(
    child: TextField(
      controller: c,
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      decoration: InputDecoration(labelText: label, errorText: error ? '' : null, errorStyle: const TextStyle(height: 0, fontSize: 0)),
      onChanged: (_) => onChanged(),
    ),
  );

  Widget _rowCard(AppLocalizations l, Palette pal, _Row r) {
    final (statusText, color, icon) = _status(l, pal, r);
    final p = r.product;
    final src = r.src;
    final meta = <String>[
      if (src.barcode.isNotEmpty) src.barcode,
      if (src.article.isNotEmpty) l.invArticle(src.article),
      l.invSrcRow(src.row),
    ].join(' · ');
    return Panel(
      key: ObjectKey(r),
      padding: const EdgeInsets.fromLTRB(12, 8, 4, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Semantics(
                  label: statusText,
                  child: Row(
                    children: [
                      Icon(icon, size: 18, color: color),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(statusText, style: TextStyle(color: color, fontWeight: FontWeight.w700, fontSize: 12.5)),
                      ),
                    ],
                  ),
                ),
              ),
              IconButton(
                tooltip: l.invRemoveRow,
                constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
                onPressed: () => _remove(r),
                icon: const Icon(Icons.delete_outline_rounded),
              ),
            ],
          ),
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (r.bad) ...[
                  Text(src.name.isEmpty ? '—' : src.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                  Text(src.issue == InvoiceLineIssue.noName ? l.invBadNoName : l.invBadNoQty, style: TextStyle(color: pal.danger, fontSize: 12.5)),
                  Text(meta, style: TextStyle(color: pal.textSoft, fontSize: 12)),
                ] else ...[
                  if (p != null)
                    Text(p.name, style: const TextStyle(fontWeight: FontWeight.w700))
                  else
                    TextField(
                      controller: r.name,
                      decoration: InputDecoration(labelText: l.peName, errorText: r.name.text.trim().isEmpty ? '' : null, errorStyle: const TextStyle(height: 0, fontSize: 0)),
                      onChanged: (_) => setState(() {}),
                    ),
                  const SizedBox(height: 2),
                  Text(
                    p != null ? '${l.onStockLabel}: ${qtyText(p.quantity)} ${p.unit} · $meta' : meta,
                    style: TextStyle(color: pal.textSoft, fontSize: 12),
                  ),
                  if (p != null && !r.packSuspect && unitDiffers(src.unit, p.unit))
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Text(l.invUnitCheck(src.unit.trim(), p.unit), style: TextStyle(color: pal.warn, fontWeight: FontWeight.w700, fontSize: 12.5)),
                    ),
                  if (p != null && r.packSuspect) _packBox(l, pal, r, p),
                  if (r.match.kind == MatchKind.fuzzy)
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Wrap(
                        spacing: 8,
                        runSpacing: 4,
                        children: [
                          ChoiceChip(
                            label: Text(l.invUseCandidate(r.match.product!.name)),
                            selected: r.useCandidate,
                            onSelected: (_) => setState(() {
                              r.useCandidate = true;
                              _packFields(r); // другой товар — выбор упаковок заново (ревью М-5)
                              _initRow(r, resetPrice: true);
                            }),
                          ),
                          ChoiceChip(
                            label: Text(l.invAsNew),
                            selected: !r.useCandidate,
                            onSelected: (_) => setState(() {
                              r.useCandidate = false;
                              _packFields(r);
                              _initRow(r, resetPrice: true);
                            }),
                          ),
                        ],
                      ),
                    ),
                  const SizedBox(height: 10),
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      _field(l.quantityLabel, r.qty, () => setState(() {}), error: r.qtyV <= 0),
                      const SizedBox(width: 8),
                      _field(l.purchasePrice, r.cost, () => _onCost(r), error: r.costV <= 0),
                    ],
                  ),
                  const SizedBox(height: 8),
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      _field(l.marginPercent, r.markup, () => _onMarkup(r)),
                      const SizedBox(width: 8),
                      _field(l.salePrice, r.price, () => _onPrice(r), error: r.priceV <= 0),
                    ],
                  ),
                  if (r.costV <= 0 || r.priceV <= 0)
                    Padding(
                      padding: const EdgeInsets.only(top: 4),
                      child: Text(r.costV <= 0 ? l.invCostMissing : l.invPriceMissing, style: TextStyle(color: pal.danger, fontSize: 12.5)),
                    ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _ErrorBox extends StatelessWidget {
  final String text;
  const _ErrorBox(this.text);
  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Panel(
      color: pal.dangerSoft,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.error_outline_rounded, color: pal.danger),
          const SizedBox(width: 8),
          Expanded(child: Text(text, style: TextStyle(color: pal.danger, fontWeight: FontWeight.w700))),
        ],
      ),
    );
  }
}

class _WarnBox extends StatelessWidget {
  final String text;
  const _WarnBox(this.text);
  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Panel(
      color: pal.warnSoft,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.warning_amber_rounded, color: pal.warn),
          const SizedBox(width: 8),
          Expanded(child: Text(text, style: TextStyle(color: pal.warn, fontWeight: FontWeight.w700, fontSize: 13))),
        ],
      ),
    );
  }
}
