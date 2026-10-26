import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/audit.dart';
import '../../core/util/barcode_generator.dart';
import '../../core/util/ui.dart';
import '../admin/admin_receive.dart';
import '../admin/admin_screens.dart';
import '../admin/new_item.dart';
import '../sale/catalog_repository.dart';
import '../scan/scan_screen.dart';
import '../scan/smart_camera.dart';
import 'product_edit_repository.dart';

// 2026-10-22 (мобилка, админ): владелец — «в мобилке нельзя редактировать товары на складе». Нажатие на товар склада
// открывает карточку: название, цены, единица, категория, штрихкоды (основной + доп., скан камерой), минимальный
// остаток, фото. Остаток — только документом «Приход / Списание / Ревизия» с причиной (product_edit_repository.dart).

final productEditRepoProvider = Provider(
  (ref) => ProductEditRepository(ref.watch(apiProvider), ref.watch(databaseProvider), ref.watch(stockRepoProvider)),
);

/// Открыть карточку товара. Без права «Склад» — сообщение (маршрут /admin/product — то же право, что у раздела цен).
/// После закрытия список склада надо перечитать (карточку могли изменить или удалить).
Future<void> openProductEditor(BuildContext context, WidgetRef ref, Product p) async {
  final s = ref.read(sessionProvider);
  if (s == null || !s.canRoute('/admin/product')) {
    toast(context, context.l.peNoAccess, error: true);
    return;
  }
  await Navigator.of(context).push<bool>(MaterialPageRoute(builder: (_) => ProductEditScreen(product: p)));
}

class ProductEditScreen extends ConsumerStatefulWidget {
  final Product product;
  const ProductEditScreen({super.key, required this.product});
  @override
  ConsumerState<ProductEditScreen> createState() => _ProductEditScreenState();
}

class _ProductEditScreenState extends ConsumerState<ProductEditScreen> {
  static const _units = ['шт', 'кг', 'л', 'упак'];

  Map<String, dynamic>? _full;
  ProductDraft? _before;
  bool _loading = true;
  String? _loadError;
  bool _saving = false;
  double _stock = 0;

  final _name = TextEditingController();
  final _price = TextEditingController();
  final _purchase = TextEditingController();
  final _wholesale = TextEditingController();
  final _category = TextEditingController();
  final _barcode = TextEditingController();
  final _min = TextEditingController();
  final _altInput = TextEditingController();
  String _unit = 'шт';
  List<String> _alt = [];
  final _photos = <Uint8List>[];

  Product get p => widget.product;

  @override
  void initState() {
    super.initState();
    _stock = p.quantity;
    _load();
  }

  @override
  void dispose() {
    for (final c in [_name, _price, _purchase, _wholesale, _category, _barcode, _min, _altInput]) {
      c.dispose();
    }
    super.dispose();
  }

  static String _num(double v) => v == 0 ? '' : money(v).replaceAll(RegExp('[   ]'), '');
  static String _q(double v) => qtyText(v).replaceAll(RegExp('[   ]'), '');

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _loadError = null;
    });
    try {
      final full = await ref.read(productEditRepoProvider).load(p.id);
      final d = ProductDraft.fromServer(full, p);
      if (!mounted) return;
      setState(() {
        _full = full;
        _before = d;
        _stock = num0(full, 'quantity');
        _name.text = d.name;
        _price.text = _num(d.price);
        _purchase.text = _num(d.purchase);
        _wholesale.text = _num(d.wholesale);
        _category.text = d.category;
        _barcode.text = d.barcode;
        _min.text = d.minQty == null ? '' : _q(d.minQty!);
        _unit = d.unit.isEmpty ? 'шт' : d.unit;
        _alt = [...d.alt];
        _loading = false;
      });
    } on ApiException catch (e) {
      if (mounted) {
        setState(() {
          _loading = false;
          _loadError = e.message;
        });
      }
    }
  }

  ProductDraft? _draft() {
    final b = _before;
    if (b == null) return null;
    return ProductDraft(
      name: _name.text.trim(),
      unit: _unit,
      category: _category.text.trim(),
      barcode: _barcode.text.trim(),
      alt: _alt,
      price: parseNum(_price.text) ?? 0,
      purchase: parseNum(_purchase.text) ?? 0,
      wholesale: parseNum(_wholesale.text) ?? 0,
      minQty: _min.text.trim().isEmpty ? b.minQty : parseNum(_min.text),
    );
  }

  bool get _canPrices => ref.read(sessionProvider)?.canEditPrices ?? false;

  bool _dirty() {
    final b = _before, d = _draft();
    if (b == null || d == null) return false;
    return _photos.isNotEmpty || productPatchBody(b, d, canPrices: _canPrices).isNotEmpty;
  }

  Future<void> _scanInto(void Function(List<String>) put) async {
    final codes = await Navigator.of(context)
        .push<List<String>>(MaterialPageRoute(builder: (_) => const ScanScreen(initial: ScanMode.receive, returnCodes: true)));
    if (codes != null && codes.isNotEmpty && mounted) setState(() => put(codes));
  }

  /// 2026-10-26 (требование владельца): «Сгенерировать» — свой EAN-13 (не с 0, не весовой), свободный в базе телефона и среди
  /// доп. кодов карточки (до 10 попыток; нет связи — код без проверки, дубль поймает _checkCodes при сохранении).
  /// Уже введённый код меняется только после подтверждения.
  Future<void> _generateCode() async {
    final l = context.l;
    if (_barcode.text.trim().isNotEmpty) {
      final ok = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          content: Text(l.bcGenReplace),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.bcGenDo)),
          ],
        ),
      );
      if (ok != true || !mounted) return;
    }
    final repo = ref.read(productEditRepoProvider);
    final code = await BarcodeGenerator.createFree((c) async => _alt.any((a) => ProductDraft.altCode(a) == c) || await repo.barcodeOwner(p.id, c) != null);
    if (mounted) setState(() => _barcode.text = code);
  }

  void _addAlt(String code) {
    final c = code.trim();
    if (c.isEmpty || _alt.any((l) => ProductDraft.altCode(l) == c) || _barcode.text.trim() == c) return;
    _alt = [..._alt, c];
  }

  Future<String?> _checkCodes(ProductDraft d) async {
    final l = context.l;
    final seen = <String>{};
    for (final c in d.allCodes) {
      if (!seen.add(c)) return l.peBarcodeTwice(c);
      final owner = await ref.read(productEditRepoProvider).barcodeOwner(p.id, c);
      if (owner != null) return l.peBarcodeTaken(c, owner);
    }
    return null;
  }

  Future<void> _save() async {
    if (_saving) return; // двойное нажатие
    final l = context.l;
    final b = _before, d = _draft();
    if (b == null || d == null) return;
    if (d.name.isEmpty) {
      toast(context, l.peNameNeed, error: true);
      return;
    }
    setState(() => _saving = true);
    try {
      final bad = await _checkCodes(d);
      if (bad != null) {
        if (mounted) toast(context, bad, error: true);
        return;
      }
      final canPrices = _canPrices;
      final r = await ref.read(productEditRepoProvider).save(p.id, b, d, canPrices: canPrices);
      // Фото — после карточки: цена уже записана, даже если фото не загрузится (аудит, «цена → база → фото»).
      final imageUrl = _full == null ? null : CatalogRepository.productImageUrl(_full!);
      for (var i = 0; i < _photos.length; i++) {
        await ref.read(adminReceiveRepoProvider).uploadImage(p.id, _photos[i], primary: imageUrl == null && i == 0);
      }
      final photos = _photos.length;
      if (!r.changed && photos == 0) {
        if (mounted) toast(context, l.peNoChanges);
        return;
      }
      Audit.log(
        'product',
        [
          '${l.peEdit}: ${d.name}',
          if (canPrices && (d.price - b.price).abs() > 0.004) '${l.sellingPrice}: ${money(b.price)} → ${money(d.price)}',
          if (canPrices && (d.purchase - b.purchase).abs() > 0.004) '${l.purchasePrice}: ${money(b.purchase)} → ${money(d.purchase)}',
          if (d.barcode != b.barcode) '${l.barcodeLabel}: ${d.barcode}',
          if (photos > 0) '${l.photoLabel}: +$photos',
        ].join(' · '),
      );
      _photos.clear();
      if (!mounted) return;
      toast(context, r.priceRestored ? l.pePriceRestored(money(d.price)) : l.peSaved);
      await _load();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _stockDoc(StockDoc doc) async {
    final l = context.l;
    final qty = TextEditingController();
    final note = TextEditingController();
    final reasons = switch (doc) {
      StockDoc.writeOff => [l.peReasonExpired, l.peReasonDefect, l.peReasonSpoiled, l.peReasonTheft, l.peReasonOther],
      StockDoc.income => [l.peReasonSupplier, l.peReasonOther],
      StockDoc.recount => [l.peReasonCount, l.peReasonOther],
    };
    var reason = reasons.first;
    final title = switch (doc) {
      StockDoc.income => l.peIncome,
      StockDoc.writeOff => l.peWriteOff,
      StockDoc.recount => l.peRecount,
    };
    var busy = false;
    final result = await showModalBottomSheet<StockChangeResult>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => SafeArea(
          child: Padding(
            padding: EdgeInsets.fromLTRB(20, 20, 20, MediaQuery.viewInsetsOf(c).bottom + 16),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text('$title · ${p.name}', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
                Text('${l.peStock}: ${qtyText(_stock)} $_unit', style: TextStyle(color: c.pal.textSoft)),
                FieldLabel(switch (doc) {
                  StockDoc.income => l.peQtyIn,
                  StockDoc.writeOff => l.peQtyOut,
                  StockDoc.recount => l.peQtyFact,
                }),
                TextField(
                  controller: qty,
                  autofocus: true,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(suffixText: _unit),
                ),
                FieldLabel(l.peReason),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final r in reasons) ChoiceChip(label: Text(r), selected: reason == r, onSelected: (_) => set(() => reason = r)),
                  ],
                ),
                const SizedBox(height: 8),
                TextField(controller: note, decoration: InputDecoration(hintText: l.peReason), textCapitalization: TextCapitalization.sentences),
                const SizedBox(height: 14),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: busy
                      ? null
                      : () async {
                          final q = parseNum(qty.text);
                          if (q == null || q < 0 || (doc != StockDoc.recount && q == 0)) return;
                          final why = [reason, note.text.trim()].where((s) => s.isNotEmpty).join(': ');
                          if (reason == l.peReasonOther && note.text.trim().isEmpty) {
                            toast(c, l.peReasonNeed, error: true);
                            return;
                          }
                          set(() => busy = true);
                          try {
                            final r = await ref.read(productEditRepoProvider).changeStock(p.id, doc, q, '$title: $why');
                            Audit.log('receive', '$title: ${p.name} · ${_q(r.from)} → ${_q(r.to)} · $why');
                            if (c.mounted) Navigator.pop(c, r);
                          } on StockBelowZeroException catch (e) {
                            if (c.mounted) toast(c, l.peStockBelowZero(_q(e.current)), error: true);
                          } on ApiException catch (e) {
                            if (c.mounted) toast(c, e.isTransient ? l.peStockOffline : e.message, error: true);
                          } finally {
                            if (c.mounted) set(() => busy = false);
                          }
                        },
                  child: busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.peApply),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    qty.dispose();
    note.dispose();
    if (result == null || !mounted) return;
    if (result.unchanged) {
      toast(context, l.peStockSame(_q(result.to)));
    } else if (!result.applied) {
      toast(context, l.peStockQueued);
    } else {
      toast(context, l.peStockDone(_q(result.from), _q(result.to)));
    }
    setState(() => _stock = result.applied ? result.to : _stock);
  }

  Future<void> _addPhoto(Future<Uint8List?> Function() pick) async {
    final bytes = await pick();
    if (bytes != null && mounted) setState(() => _photos.add(bytes));
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final canPrices = _canPrices;
    final pal = context.pal;
    Widget body;
    if (_loading) {
      body = const Center(child: CircularProgressIndicator());
    } else if (_loadError != null || _before == null) {
      body = Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(l.peLoadFailed, textAlign: TextAlign.center),
              if (_loadError != null) Text(_loadError!, textAlign: TextAlign.center, style: TextStyle(color: pal.textSoft, fontSize: 12)),
              const SizedBox(height: 12),
              FilledButton(onPressed: _load, child: Text(l.retry)),
            ],
          ),
        ),
      );
    } else {
      final imageUrl = _full == null ? null : CatalogRepository.productImageUrl(_full!);
      final pr = parseNum(_price.text) ?? 0;
      final co = parseNum(_purchase.text) ?? 0;
      final isService = _full?['kind'] == 'service';
      body = ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        children: [
          FieldLabel(l.peName),
          TextField(controller: _name, textCapitalization: TextCapitalization.sentences, onChanged: (_) => setState(() {})),
          FieldLabel(l.sellingPrice),
          TextField(
            controller: _price,
            enabled: canPrices,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            onChanged: (_) => setState(() {}),
          ),
          FieldLabel(l.purchasePrice),
          TextField(
            controller: _purchase,
            enabled: canPrices,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            onChanged: (_) => setState(() {}),
          ),
          if (co > 0 && pr > 0)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text(
                pr < co ? l.belowCost : l.markup(((pr - co) / co * 100).toStringAsFixed(0)),
                style: TextStyle(color: pr < co ? T.danger : T.success, fontWeight: FontWeight.w700),
              ),
            ),
          FieldLabel(l.wholesalePrice),
          TextField(
            controller: _wholesale,
            enabled: canPrices,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            onChanged: (_) => setState(() {}),
          ),
          if (!canPrices) Padding(padding: const EdgeInsets.only(top: 4), child: Text(l.pePriceLocked, style: TextStyle(color: pal.textSoft, fontSize: 12))),
          FieldLabel(l.unitLabel),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final u in {..._units, _unit}) ChoiceChip(label: Text(u), selected: _unit == u, onSelected: (_) => setState(() => _unit = u)),
            ],
          ),
          FieldLabel(l.categoryLabel),
          TextField(controller: _category, textCapitalization: TextCapitalization.sentences, onChanged: (_) => setState(() {})),
          FieldLabel(l.barcodeLabel),
          TextField(
            controller: _barcode,
            onChanged: (_) => setState(() {}),
            decoration: InputDecoration(
              suffixIcon: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  IconButton(tooltip: l.bcGenTooltip, icon: const Icon(Icons.casino_outlined), onPressed: _generateCode),
                  IconButton(
                    tooltip: l.peScan,
                    icon: const Icon(Icons.qr_code_scanner_rounded),
                    onPressed: () => _scanInto((codes) => _barcode.text = codes.first.trim()),
                  ),
                ],
              ),
            ),
          ),
          FieldLabel(l.altBarcodesLabel),
          for (final line in _alt)
            ListTile(
              contentPadding: EdgeInsets.zero,
              dense: true,
              title: Text(ProductDraft.altCode(line)),
              subtitle: line.split('|').length > 1 ? Text(l.pePackQty(line.split('|')[1])) : null,
              trailing: IconButton(
                icon: const Icon(Icons.close_rounded),
                onPressed: () => setState(() => _alt = [..._alt]..remove(line)),
              ),
            ),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _altInput,
                  decoration: InputDecoration(hintText: l.altBarcodeAdd),
                  onSubmitted: (v) => setState(() {
                    _addAlt(v);
                    _altInput.clear();
                  }),
                ),
              ),
              IconButton(
                tooltip: l.altBarcodeAdd,
                icon: const Icon(Icons.add_rounded),
                onPressed: () => setState(() {
                  _addAlt(_altInput.text);
                  _altInput.clear();
                }),
              ),
              IconButton(
                tooltip: l.peScan,
                icon: const Icon(Icons.qr_code_scanner_rounded),
                onPressed: () => _scanInto((codes) => codes.forEach(_addAlt)),
              ),
            ],
          ),
          FieldLabel(l.peMinQty),
          TextField(
            controller: _min,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(suffixText: _unit, helperText: l.peMinQtyHint),
            onChanged: (_) => setState(() {}),
          ),
          if (!isService) ...[
            FieldLabel(l.peStock),
            Panel(
              padding: const EdgeInsets.all(12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(
                    '${qtyText(_stock)} $_unit',
                    style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800, color: _stock <= 0 ? T.danger : null),
                  ),
                  const SizedBox(height: 4),
                  Text(l.peStockHint, style: TextStyle(fontSize: 12, color: pal.textSoft)),
                  const SizedBox(height: 10),
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      OutlinedButton.icon(onPressed: () => _stockDoc(StockDoc.income), icon: const Icon(Icons.add_box_outlined), label: Text(l.peIncome)),
                      OutlinedButton.icon(
                        onPressed: () => _stockDoc(StockDoc.writeOff),
                        icon: const Icon(Icons.remove_circle_outline_rounded),
                        label: Text(l.peWriteOff),
                      ),
                      OutlinedButton.icon(onPressed: () => _stockDoc(StockDoc.recount), icon: const Icon(Icons.fact_check_outlined), label: Text(l.peRecount)),
                    ],
                  ),
                ],
              ),
            ),
          ],
          FieldLabel(l.photoLabel),
          SizedBox(
            height: 72,
            child: ListView(
              scrollDirection: Axis.horizontal,
              children: [
                if (imageUrl != null)
                  Padding(
                    padding: const EdgeInsets.only(right: 8),
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(10),
                      child: Image.network(
                        imageUrl,
                        width: 72,
                        height: 72,
                        fit: BoxFit.cover,
                        cacheWidth: 200,
                        errorBuilder: (_, _, _) => const SizedBox(width: 72, child: Icon(Icons.broken_image_outlined)),
                      ),
                    ),
                  ),
                for (final b in _photos)
                  Padding(
                    padding: const EdgeInsets.only(right: 8),
                    child: ClipRRect(borderRadius: BorderRadius.circular(10), child: Image.memory(b, width: 72, height: 72, fit: BoxFit.cover, cacheWidth: 200)),
                  ),
              ],
            ),
          ),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              ActionChip(
                avatar: const Icon(Icons.photo_camera_rounded, size: 18),
                label: Text(l.photoCamera),
                onPressed: () => _addPhoto(() => pickPhoto(ImageSource.camera)),
              ),
              ActionChip(
                avatar: const Icon(Icons.photo_library_outlined, size: 18),
                label: Text(l.photoGallery),
                onPressed: () => _addPhoto(() => pickPhoto(ImageSource.gallery)),
              ),
              ActionChip(
                avatar: const Icon(Icons.travel_explore_rounded, size: 18),
                label: Text(l.photoInternet),
                onPressed: () => _addPhoto(() => pickInternetPhoto(context, _name.text, _barcode.text)),
              ),
            ],
          ),
          const SizedBox(height: 20),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
            onPressed: _saving ? null : _save,
            child: _saving ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.save),
          ),
        ],
      );
    }
    return LeaveGuard(
      ask: _dirty,
      child: Scaffold(
        appBar: ScreenTitle(
          l.peEdit,
          subtitle: p.name,
          actions: [
            PopupMenuButton<String>(
              onSelected: (v) async {
                if (v == 'teach') {
                  await teachExistingProduct(context, ref, p);
                } else if (v == 'delete' && await confirmDeleteProduct(context, ref, p)) {
                  if (context.mounted) Navigator.of(context).pop(true);
                }
              },
              itemBuilder: (c) => [
                PopupMenuItem(value: 'teach', child: Text(l.teachCamera)),
                PopupMenuItem(value: 'delete', child: Text(l.deleteProduct, style: const TextStyle(color: T.danger))),
              ],
            ),
          ],
        ),
        body: body,
      ),
    );
  }
}
