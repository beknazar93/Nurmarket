import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/catalog/global_products.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../admin/admin_screens.dart';
import '../scan/scan_screen.dart';
import 'product_edit.dart';
import 'product_edit_repository.dart';
import 'product_packages.dart';
import 'receive_lookup.dart';
import 'stock_repository.dart';
import '../scan/smart_camera.dart';

/// Склад кассира (Т7): остатки, «Приёмка сканером», «Ревизия». Цены и новые товары меняет администратор.
class StockScreen extends ConsumerStatefulWidget {
  final String? filter;
  const StockScreen({super.key, this.filter});
  @override
  ConsumerState<StockScreen> createState() => _StockScreenState();
}

class _StockScreenState extends ConsumerState<StockScreen> {
  final _q = TextEditingController();
  late String _filter = widget.filter ?? 'all';
  // 14.10, владелец: «фильтр везде, где есть товары» — к остатку добавлены категории.
  String? _category;
  List<Product> _list = [];
  Map<String, PiecePack> _packs = {}; // 2026-10-27 (М4): остаток товара с упаковкой — «12 пачек + 5 шт»
  Timer? _debounce;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    super.dispose();
  }

  double _pull = 0;
  bool _lowOpen = false;

  /// Дотянули ниже конца списка (Android — overscroll, iPhone — отскок) больше чем на 70 точек — окно «Заканчивается».
  bool _onScroll(ScrollNotification n) {
    if (n is OverscrollNotification && n.overscroll > 0 && n.dragDetails != null && n.metrics.extentAfter == 0) {
      _pull += n.overscroll;
    } else if (n is ScrollUpdateNotification && n.dragDetails != null && n.metrics.pixels > n.metrics.maxScrollExtent) {
      _pull = n.metrics.pixels - n.metrics.maxScrollExtent;
    } else if (n is ScrollEndNotification) {
      _pull = 0;
    }
    if (_pull > 70 && !_lowOpen) {
      _pull = 0;
      _openLowStock();
    }
    return false;
  }

  Future<void> _openLowStock() async {
    _lowOpen = true;
    HapticFeedback.mediumImpact();
    final l = context.l;
    final all = (await ref.read(databaseProvider).searchProducts('', limit: 20000)).where((p) => !p.isService && isLowStock(p.quantity, p.minQuantity)).toList() // 2026-10-22 (мобилка, админ, П11): порог как на ПК
      ..sort((a, b) => a.quantity.compareTo(b.quantity));
    if (!mounted) return;
    final s = ref.read(sessionProvider);
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (c) => DraggableScrollableSheet(
        expand: false,
        initialChildSize: .6,
        maxChildSize: .92,
        builder: (c, scroll) => ListView(
          controller: scroll,
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
          children: [
            Text(l.lowStockTitle(all.length), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
            const SizedBox(height: 8),
            if (all.isEmpty)
              Padding(
                padding: const EdgeInsets.all(24),
                child: Text(l.lowStockEmpty, textAlign: TextAlign.center),
              ),
            for (final p in all)
              ListTile(
                contentPadding: EdgeInsets.zero,
                leading: ProductThumb(name: p.name, url: p.imageUrl, size: 40),
                title: Text(p.name, maxLines: 2, overflow: TextOverflow.ellipsis),
                trailing: Text(
                  '${qtyText(p.quantity)} ${p.unit}',
                  style: TextStyle(fontWeight: FontWeight.w800, color: p.quantity <= 0 ? T.danger : T.warn),
                ),
              ),
            if (all.isNotEmpty && s != null && s.canRoute('/admin/purchase')) ...[
              const SizedBox(height: 8),
              FilledButton.icon(
                onPressed: () {
                  Navigator.pop(c);
                  context.push('/admin/purchase');
                },
                icon: const Icon(Icons.local_shipping_outlined),
                label: Text(l.supplierOrder),
              ),
            ],
          ],
        ),
      ),
    );
    _lowOpen = false;
  }

  Future<void> _productActions(Product p) async {
    final l = context.l;
    final s = ref.read(sessionProvider);
    final canEdit = s != null && s.canRoute('/admin/new-product');
    final action = await showModalBottomSheet<String>(
      context: context,
      builder: (c) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: ProductThumb(name: p.name, url: p.imageUrl, size: 44),
              title: Text(p.name, style: const TextStyle(fontWeight: FontWeight.w800)),
              subtitle: Text('${_stockText(p)} · ${money(p.price)} ${l.som}${p.barcode == null ? '' : ' · ${p.barcode}'}'),
            ),
            // 2026-10-22 (мобилка, админ): «Цена» → полная карточка товара (product_edit.dart).
            if (s != null && s.canRoute('/admin/product'))
              ListTile(leading: const Icon(Icons.edit_outlined), title: Text(l.peEdit), onTap: () => Navigator.pop(c, 'price')),
            // 14.10, владелец: обучение умной камеры на товаре (3 фото + штрихкод).
            if (canEdit) ListTile(leading: const Icon(Icons.center_focus_strong_rounded), title: Text(l.teachCamera), onTap: () => Navigator.pop(c, 'teach')),
            if (canEdit)
              ListTile(
                leading: const Icon(Icons.delete_outline_rounded, color: T.danger),
                title: Text(l.deleteProduct, style: const TextStyle(color: T.danger)),
                onTap: () => Navigator.pop(c, 'delete'),
              ),
          ],
        ),
      ),
    );
    if (!mounted) return;
    if (action == 'price') {
      await openProductEditor(context, ref, p);
      await _load();
    } else if (action == 'teach') {
      await teachExistingProduct(context, ref, p);
    } else if (action == 'delete' && await confirmDeleteProduct(context, ref, p)) {
      await _load();
    }
  }

  Future<void> _load() async {
    var list = await ref.read(databaseProvider).searchProducts(_q.text, category: _category, limit: 20000);
    list = list.where((p) => !p.isService).toList();
    if (_filter == 'low') list = list.where((p) => p.quantity > 0 && isLowStock(p.quantity, p.minQuantity)).toList(); // 2026-10-22 (мобилка, админ, П11)
    if (_filter == 'out') list = list.where((p) => p.quantity <= 0).toList();
    if (_filter == 'in') list = list.where((p) => p.quantity > 0).toList();
    final packs = await PackageStore.read(ref.read(databaseProvider));
    if (mounted) {
      setState(() {
        _list = list;
        _packs = packs;
      });
    }
  }

  /// 2026-10-27 (М4): остаток товара: с поштучной упаковкой — «12 пачек + 5 шт», без неё — «245 шт» как раньше.
  String _stockText(Product p) {
    final pk = _packs[p.id];
    return pk == null
        ? '${qtyText(p.quantity)} ${p.unit}'
        : stockWithPack(qtyText, p.quantity, pk, packWord: context.l.pkPackShort, pcs: context.l.pkPcs);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    // 2026-10-26 (живой аудит, П7, В-3): плавающая «Новый товар» убрана — она перекрывала строки списка (касание по
    // товару открывало создание), а плитка «Новый товар» уже есть над поиском.
    return Column(
      children: [
        ScreenTitle(l.tabStock, back: false),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16),
          child: Row(
            children: [
              Expanded(
                child: Panel(
                  color: T.accent,
                  onTap: () => context.push('/stock/receive'),
                  child: Row(
                    children: [
                      Icon(Icons.add_rounded, color: context.pal.action),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          l.receiveByScanner,
                          style: const TextStyle(fontWeight: FontWeight.w800, color: T.navy),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Panel(
                  onTap: () => context.push('/stock/audit'),
                  child: Row(
                    children: [
                      const Icon(Icons.fact_check_outlined),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(l.audit, style: const TextStyle(fontWeight: FontWeight.w800)),
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
        // 13.10, владелец: «на складе нет создания товара и массовой загрузки товара» (и умная камера — Т9).
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 10, 16, 0),
          child: Row(
            children: [
              Expanded(
                child: _StockAction(icon: Icons.add_box_outlined, text: l.newProduct, onTap: () => context.push('/admin/new-product')),
              ),
              const SizedBox(width: 6),
              Expanded(
                child: _StockAction(icon: Icons.upload_file_rounded, text: l.bulkImport, onTap: () => context.push('/stock/import')),
              ),
              const SizedBox(width: 6),
              Expanded(
                child: _StockAction(icon: Icons.photo_camera_rounded, text: l.smartCam, onTap: () => context.push('/admin/smartcam')),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 6),
          child: TextField(
            controller: _q,
            onChanged: (_) {
              _debounce?.cancel();
              _debounce = Timer(const Duration(milliseconds: 250), _load);
            },
            decoration: InputDecoration(hintText: l.findProduct, prefixIcon: const Icon(Icons.search_rounded)),
          ),
        ),
        ProductFilterBar(
          padding: const EdgeInsets.symmetric(horizontal: 16),
          stock: switch (_filter) {
            'low' => StockFilter.low,
            'out' => StockFilter.out,
            'in' => StockFilter.inStock,
            _ => StockFilter.all,
          },
          category: _category,
          onChanged: (st, c) {
            setState(() {
              _filter = switch (st) {
                StockFilter.low => 'low',
                StockFilter.out => 'out',
                StockFilter.inStock => 'in',
                StockFilter.all => 'all',
              };
              _category = c;
            });
            _load();
          },
        ),
        Expanded(
          child: RefreshIndicator(
            onRefresh: () async {
              try {
                await ref.read(catalogRepoProvider).sync();
              } on ApiException catch (e) {
                if (context.mounted) toast(context, e.message, error: true);
              }
              await _load();
            },
            // 13.10, владелец: «долистать до низа и ещё потянуть вверх — плавно открывается окно с низким остатком».
            child: NotificationListener<ScrollNotification>(
              onNotification: _onScroll,
              child: ListView.separated(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
                itemCount: _list.length + 1,
                separatorBuilder: (_, _) => const SizedBox(height: 6),
                itemBuilder: (c, i) {
                  if (i == _list.length) {
                    return Padding(
                      padding: const EdgeInsets.all(12),
                      child: Column(
                        children: [
                          Text(l.pricesByAdmin, style: TextStyle(fontSize: 12, color: pal.textSoft)),
                          const SizedBox(height: 10),
                          Text(
                            l.lowStockPull,
                            style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: pal.textSoft),
                          ),
                          const SizedBox(height: 40),
                        ],
                      ),
                    );
                  }
                  final p = _list[i];
                  final color = p.quantity <= 0 ? T.danger : (isLowStock(p.quantity, p.minQuantity) ? T.warn : null);
                  return Panel(
                    // 13.10, владелец: «кнопку удалить товар добавь» — нажатие на товар: действия с ним
                    // 2026-10-22 (мобилка, админ): владелец — «нельзя редактировать товары на складе». С правом «Склад» —
                    // сразу карточка товара (правка, остаток документом, обучение камеры и удаление — в её меню).
                    onTap: () async {
                      final s = ref.read(sessionProvider);
                      if (s != null && s.canRoute('/admin/product')) {
                        await openProductEditor(context, ref, p);
                        await _load();
                      } else {
                        await _productActions(p);
                      }
                    },
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                    child: Row(
                      children: [
                        ProductThumb(name: p.name, url: p.imageUrl, size: 40),
                        const SizedBox(width: 10),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                p.name,
                                maxLines: 2,
                                overflow: TextOverflow.ellipsis,
                                style: const TextStyle(fontWeight: FontWeight.w600),
                              ),
                              Text('${money(p.price)} ${l.som}', style: TextStyle(fontSize: 12, color: pal.textSoft)),
                            ],
                          ),
                        ),
                        Text(
                          _stockText(p),
                          style: TextStyle(fontWeight: FontWeight.w800, color: color),
                        ),
                      ],
                    ),
                  );
                },
              ),
            ),
          ),
        ),
      ],
    );
  }
}

// ======================================================================== приёмка

/// 2026-10-26 (живой аудит, П7, С-14): что дал один скан в приёмке и ревизии — товар и сколько штук прибавить.
/// Штрихкод упаковки (доп. штрихкод с количеством, new_item.dart «В упаковке») прибавляет всю упаковку, обычный — 1.
/// Этикетка весов — не товар поставки (allowWeightLabel: false), как в самом сканере.
Future<({Product product, double qty})?> receiveScanHit(AppDatabase db, String code) async {
  final hit = await db.productByScan(code, allowWeightLabel: false);
  if (hit == null) return null;
  return (product: hit.product, qty: hit.qty ?? 1);
}

/// Приёмка сканером (Т16): каждый скан — +1 штука (штрихкод упаковки — вся упаковка); неизвестный штрихкод — администратору; без цен.
class ReceiveScreen extends ConsumerStatefulWidget {
  /// Только для проверок без камеры: коды, которые «отсканировал» кассир.
  @visibleForTesting
  final Future<List<String>?> Function()? debugScan;
  const ReceiveScreen({super.key, this.debugScan});
  @override
  ConsumerState<ReceiveScreen> createState() => _ReceiveScreenState();
}

class _ReceiveScreenState extends ConsumerState<ReceiveScreen> {
  final List<StockLine> _lines = [];
  final List<String> _unknown = [];
  // 2026-10-27: сколько раз отсканирован неизвестный код (раньше повтор не считался), что про него нашлось, какой код ищем сейчас
  final Map<String, double> _unknownQty = {};
  final Map<String, ReceiveFind> _finds = {};
  final Set<String> _searching = {};
  bool _busy = false;

  static const _draftKey = 'receive.draft';

  GlobalProducts? get _globals {
    try {
      return ref.read(globalProductsProvider);
    } catch (_) {
      return null;
    }
  }

  /// Завести, привязать или исправить штрихкод — только с правом на товары (как «Новый товар»).
  bool get _canEdit => ref.read(sessionProvider)?.canRoute('/admin/new-product') ?? false;

  @override
  void initState() {
    super.initState();
    _restore();
  }

  /// «Отложить» — черновик остаётся на телефоне до следующего открытия.
  Future<void> _restore() async {
    final raw = await ref.read(databaseProvider).getSetting(_draftKey);
    if (raw == null || raw.isEmpty) return;
    final j = jsonDecode(raw) as Map<String, dynamic>;
    setState(() {
      for (final m in (j['lines'] as List).cast<Map>()) {
        _lines.add(
          StockLine(productId: m['productId'] as String, name: m['name'] as String, before: (m['before'] as num).toDouble(), qty: (m['qty'] as num).toDouble()),
        );
      }
      _unknown.addAll((j['unknown'] as List).cast<String>());
      final q = j['unknownQty'];
      if (q is Map) {
        for (final e in q.entries) {
          _unknownQty['${e.key}'] = (e.value as num).toDouble();
        }
      }
    });
    // что про неизвестные коды нашлось (без сети: локальный справочник и телефон)
    for (final code in List.of(_unknown)) {
      unawaited(_recheck(code, network: false));
    }
  }

  Future<void> _saveDraft() => ref
      .read(databaseProvider)
      .setSetting(
        _draftKey,
        jsonEncode({
          'lines': _lines.map((l) => {'productId': l.productId, 'name': l.name, 'before': l.before, 'qty': l.qty}).toList(),
          'unknown': _unknown,
          'unknownQty': _unknownQty,
        }),
      );

  Future<void> _scan() async {
    final codes = await (widget.debugScan?.call() ??
        Navigator.of(context).push<List<String>>(MaterialPageRoute(builder: (_) => const ScanScreen(initial: ScanMode.receive, returnCodes: true))));
    if (codes == null) return;
    final db = ref.read(databaseProvider);
    for (final code in codes) {
      // 2026-10-26: сначала телефон (и UPC-A <-> EAN-13, и упаковка) — быстро. Неизвестное — сервер и справочник NurCRM.
      final hit = await receiveScanHit(db, code);
      if (hit != null) {
        _addLine(hit.product, hit.qty);
        continue;
      }
      // повторный скан неизвестного кода считается (раньше терялся)
      if (_unknown.contains(code)) {
        _unknownQty[code] = (_unknownQty[code] ?? 1) + 1;
        continue;
      }
      _unknown.add(code);
      _unknownQty[code] = 1;
      if (mounted) setState(() {});
      await _recheck(code);
    }
    if (mounted) setState(() {});
    await _saveDraft();
  }

  void _addLine(Product p, double qty) {
    final i = _lines.indexWhere((l) => l.productId == p.id);
    if (i >= 0) {
      _lines[i].qty += qty;
    } else {
      _lines.insert(0, StockLine(productId: p.id, name: p.name, before: p.quantity, qty: qty));
    }
  }

  /// Найти неизвестный код: сервер -> справочник NurCRM. Товар нашёлся — в строки приёмки с накопленным количеством.
  Future<void> _recheck(String code, {bool network = true}) async {
    if (_searching.contains(code)) return;
    if (mounted) setState(() => _searching.add(code));
    try {
      final f = await resolveReceiveCode(ref.read(databaseProvider), ref.read(apiProvider), _globals, code, network: network);
      if (!mounted) return;
      if (f.kind == ReceiveKind.found) {
        _unknown.remove(code);
        _addLine(f.product!, (_unknownQty.remove(code) ?? 1) * f.qty);
        _finds.remove(code);
      } else {
        _finds[code] = f;
      }
    } finally {
      if (mounted) setState(() => _searching.remove(code));
    }
    await _saveDraft();
  }

  Future<void> _addNew(String code) async {
    if (!_canEdit) {
      toast(context, context.l.recvNoRight, error: true);
      return;
    }
    await context.push('/admin/new-product?barcode=$code');
    if (mounted) await _recheck(code);
  }

  /// «Привязать штрихкод» (доп. код к похожему товару) и «Исправить» (основной код карточки) — через карточку товара.
  Future<void> _bind(String code, Product p, {required bool fix}) async {
    final l = context.l;
    if (!_canEdit) {
      toast(context, l.recvNoRight, error: true);
      return;
    }
    try {
      final repo = ref.read(productEditRepoProvider);
      final local = await ref.read(databaseProvider).productById(p.id) ?? p;
      final before = ProductDraft.fromServer(await repo.load(p.id), local);
      final after = fix ? before.copyWith(barcode: code) : before.copyWith(alt: [...before.alt, code]);
      await repo.save(p.id, before, after, canPrices: false);
      if (!mounted) return;
      toast(context, fix ? l.recvFixed : l.recvBound(p.name));
      await _recheck(code, network: false);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  Future<void> _post() async {
    final l = context.l;
    if (_busy) return; // 2026-10-22 (мобилка, деньги, К-7): двойное нажатие — одна приёмка
    setState(() => _busy = true);
    try {
      final sent = await ref.read(stockRepoProvider).postReceiving(_lines.where((x) => x.qty > 0).toList());
      await ref.read(databaseProvider).setSetting(_draftKey, '');
      if (!mounted) return;
      toast(context, sent ? l.receivedOk : l.receivedQueued);
      Navigator.pop(context);
    } on ApiException catch (e) {
      // 2026-10-22: приход уже в очереди («Синхронизация», с причиной) — черновик не оставляем, иначе повторное
      // «Провести» прибавило бы остаток ещё раз.
      await ref.read(databaseProvider).setSetting(_draftKey, '');
      if (!mounted) return;
      toast(context, l.receiveNotAccepted(e.message), error: true);
      Navigator.pop(context);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Карточка кода, которого нет в каталоге: что нашлось в справочнике NurCRM и что с этим можно сделать.
  Widget _unknownCard(String code) {
    final l = context.l;
    final f = _finds[code];
    final qty = _unknownQty[code] ?? 1;
    final searching = _searching.contains(code);
    final canEdit = _canEdit;
    final lines = <Widget>[];
    final buttons = <Widget>[];
    Widget head(String t) => Text(t, style: const TextStyle(color: T.warn, fontWeight: FontWeight.w800));
    Widget small(String t) => Text(t, style: const TextStyle(color: T.warn, fontSize: 12));
    Widget button(String label, VoidCallback onTap, {Key? key}) => FilledButton.tonal(
      key: key,
      style: FilledButton.styleFrom(minimumSize: const Size(0, 48)),
      onPressed: searching ? null : onTap,
      child: Text(label),
    );
    switch (f?.kind) {
      case ReceiveKind.typo:
        lines.add(head(l.recvTypo(f!.product!.name, f.product!.barcode ?? '', code)));
        if (canEdit) buttons.add(button(l.recvFixCode, () => _bind(code, f.product!, fix: true), key: ValueKey('fix_$code')));
      case ReceiveKind.similar:
        lines.add(head(l.recvFoundGlobal(f!.global!.name)));
        lines.add(small(l.recvLooksLike(f.product!.name)));
        if (canEdit) {
          buttons.add(button(l.recvBindCode, () => _bind(code, f.product!, fix: false), key: ValueKey('bind_$code')));
          buttons.add(button(l.recvAddProduct, () => _addNew(code), key: ValueKey('add_$code')));
        }
      case ReceiveKind.global:
        lines.add(head(l.recvFoundGlobal(f!.global!.name)));
        if (canEdit) buttons.add(button(l.recvAddProduct, () => _addNew(code), key: ValueKey('add_$code')));
      default:
        lines.add(head(l.unknownBarcode));
        if (!searching) {
          lines.add(small(l.unknownBarcodeHint(code)));
          if (canEdit) buttons.add(button(l.recvAddProduct, () => _addNew(code), key: ValueKey('add_$code')));
        }
    }
    if (searching) lines.add(small(l.recvSearching(code)));
    if (!canEdit && f != null && f.kind != ReceiveKind.none) lines.add(small(l.recvNoRight));
    return Container(
      key: ValueKey('unknown_$code'),
      margin: const EdgeInsets.only(top: 8),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(color: context.pal.warnSoft, borderRadius: BorderRadius.circular(12)),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: lines)),
              if (qty > 1) Padding(padding: const EdgeInsets.only(left: 8), child: head('×${qtyText(qty)}')),
              IconButton(
                key: ValueKey('drop_$code'),
                visualDensity: VisualDensity.compact,
                icon: const Icon(Icons.close_rounded, size: 20),
                onPressed: () {
                  setState(() {
                    _unknown.remove(code);
                    _unknownQty.remove(code);
                    _finds.remove(code);
                  });
                  _saveDraft();
                },
              ),
            ],
          ),
          if (searching) const Padding(padding: EdgeInsets.only(top: 6), child: LinearProgressIndicator()),
          if (buttons.isNotEmpty) Padding(padding: const EdgeInsets.only(top: 8), child: Wrap(spacing: 8, runSpacing: 8, children: buttons)),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final pieces = _lines.fold<double>(0, (s, x) => s + x.qty);
    return Scaffold(
      appBar: ScreenTitle(l.receiving, subtitle: l.receiveNoSupplier),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
        children: [
          InkWell(
            onTap: _scan,
            borderRadius: BorderRadius.circular(18),
            child: Container(
              padding: const EdgeInsets.symmetric(vertical: 22),
              decoration: BoxDecoration(
                border: Border.all(color: pal.text, width: 1.6),
                borderRadius: BorderRadius.circular(18),
              ),
              child: Column(
                children: [
                  const Icon(Icons.qr_code_scanner_rounded, size: 32),
                  const SizedBox(height: 8),
                  Text(l.scanProduct, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
                  Text(l.eachScanPlusOne, style: TextStyle(fontSize: 12, color: pal.textSoft)),
                ],
              ),
            ),
          ),
          const SizedBox(height: 14),
          Row(
            children: [
              Text(l.accepted, style: const TextStyle(fontWeight: FontWeight.w800)),
              const SizedBox(width: 8),
              // 2026-10-27: на 360 dp с шрифтом 1,3 (ky/uz) подпись не помещалась в строку
              Expanded(child: Text(l.goodsPieces(_lines.length, qtyText(pieces)), textAlign: TextAlign.end, style: TextStyle(fontSize: 12.5, color: pal.textSoft))),
            ],
          ),
          const SizedBox(height: 6),
          for (var i = 0; i < _lines.length; i++)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(_lines[i].name, style: const TextStyle(fontWeight: FontWeight.w700)),
                        Text(l.wasPieces(qtyText(_lines[i].before)), style: TextStyle(fontSize: 12, color: pal.textSoft)),
                      ],
                    ),
                  ),
                  IconButton.outlined(
                    onPressed: () {
                      setState(() {
                        _lines[i].qty -= 1;
                        if (_lines[i].qty <= 0) _lines.removeAt(i);
                      });
                      _saveDraft();
                    },
                    icon: const Icon(Icons.remove, size: 18),
                  ),
                  InkWell(
                    onTap: () async {
                      final v = await askNumber(context, title: _lines[i].name, initial: _lines[i].qty);
                      if (v != null && v >= 0) {
                        setState(() => _lines[i].qty = v);
                        _saveDraft();
                      }
                    },
                    child: SizedBox(
                      width: 44,
                      child: Text(
                        qtyText(_lines[i].qty),
                        textAlign: TextAlign.center,
                        style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16),
                      ),
                    ),
                  ),
                  IconButton.filled(
                    style: IconButton.styleFrom(backgroundColor: context.pal.action),
                    onPressed: () {
                      setState(() => _lines[i].qty += 1);
                      _saveDraft();
                    },
                    icon: Icon(Icons.add, size: 18, color: context.pal.onAction),
                  ),
                ],
              ),
            ),
          for (final code in _unknown) _unknownCard(code),
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 6, 16, 12),
          child: Row(
            children: [
              // 2026-10-27: на 360 dp с шрифтом 1,3 (ky/uz) обе кнопки не помещались в строку — «Отложить» сжимается
              Flexible(
                child: OutlinedButton(
                  style: OutlinedButton.styleFrom(minimumSize: const Size(100, T.ctaHeight)),
                  onPressed: () async {
                    await _saveDraft();
                    if (context.mounted) Navigator.pop(context);
                  },
                  child: Text(l.postpone, maxLines: 1, overflow: TextOverflow.ellipsis),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                flex: 2,
                child: FilledButton(onPressed: _busy || _lines.isEmpty ? null : _post, child: Text(l.postPieces(qtyText(pieces)), textAlign: TextAlign.center)),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ======================================================================== ревизия

/// Ревизия (Т17): пересчёт по категории или всего склада; расхождения подсвечены; уходит администратору.
class AuditScreen extends ConsumerStatefulWidget {
  const AuditScreen({super.key});
  @override
  ConsumerState<AuditScreen> createState() => _AuditScreenState();
}

class _AuditScreenState extends ConsumerState<AuditScreen> {
  String? _category;
  bool _started = false;
  List<Product> _products = [];
  final Map<String, double> _counted = {};
  final Map<String, TextEditingController> _ctrls = {};
  String _filter = 'all';
  List<String> _categories = [];
  bool _busy = false;
  bool _done = false;
  final _q = TextEditingController();

  /// 2026-10-22 (мобилка, деньги): черновик пересчёта — на телефоне до проведения (закрыли экран, позвонили — не пропадёт).
  static const _draftKey = 'audit.draft';
  Map<String, dynamic>? _draft;
  Timer? _saveTimer;

  @override
  void initState() {
    super.initState();
    final db = ref.read(databaseProvider);
    db.categories().then((c) => mounted ? setState(() => _categories = c) : null);
    db.getSetting(_draftKey).then((raw) {
      if (raw == null || raw.isEmpty || !mounted) return;
      try {
        final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
        if ((m['counted'] as Map?)?.isNotEmpty ?? false) setState(() => _draft = m);
      } catch (_) {
        // испорченный черновик — без него
      }
    });
  }

  @override
  void dispose() {
    _saveTimer?.cancel();
    for (final c in _ctrls.values) {
      c.dispose();
    }
    _q.dispose();
    super.dispose();
  }

  void _saveDraftSoon() {
    _saveTimer?.cancel();
    _saveTimer = Timer(const Duration(milliseconds: 600), () {
      ref.read(databaseProvider).setSetting(_draftKey, _counted.isEmpty ? '' : jsonEncode({'category': _category, 'counted': _counted}));
    });
  }

  Future<void> _start(String? category, {Map<String, double>? counted}) async {
    // 2026-10-22: без предела 2000 — ревизия «Весь склад» видит все товары.
    final list = (await ref.read(databaseProvider).searchProducts('', category: category, limit: 1000000)).where((p) => !p.isService).toList();
    if (!mounted) return;
    setState(() {
      _category = category;
      _products = list;
      _started = true;
      if (counted != null) {
        _counted.addAll(counted);
        for (final p in list) {
          final v = counted[p.id];
          if (v != null) _ctrlFor(p).text = qtyText(v);
        }
      }
    });
  }

  Future<void> _scan() async {
    final codes = await Navigator.of(context)
        .push<List<String>>(MaterialPageRoute(builder: (_) => const ScanScreen(initial: ScanMode.receive, returnCodes: true)));
    if (codes == null) return;
    for (final code in codes) {
      final hit = await receiveScanHit(ref.read(databaseProvider), code); // 13.10: и UPC-A ↔ EAN-13; 2026-10-26: и упаковка
      final p = hit?.product;
      if (p == null) continue;
      if (!_products.any((x) => x.id == p.id)) _products.insert(0, p);
      _counted[p.id] = (_counted[p.id] ?? 0) + hit!.qty;
      _ctrlFor(p).text = qtyText(_counted[p.id]!);
    }
    setState(() {});
    _saveDraftSoon();
  }

  TextEditingController _ctrlFor(Product p) => _ctrls.putIfAbsent(p.id, () => TextEditingController());

  /// 2026-10-22 (мобилка, деньги, К-10): «Провести ревизию» — акт и проведение, как на ПК (остатки = пересчитанным).
  Future<void> _send() async {
    final l = context.l;
    if (_busy) return;
    final diffs = _products.where((p) => _counted[p.id] != null && (_counted[p.id]! - p.quantity).abs() > 0.0001).length;
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.auditApply),
        content: Text(l.auditApplyAsk(_counted.length, diffs)),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.auditApply)),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    setState(() => _busy = true);
    try {
      final note = '${l.audit} · ${_category ?? l.allStock} · ${ref.read(sessionProvider)?.userName ?? ''}';
      final applied = await ref.read(stockRepoProvider).applyAudit(note, Map.of(_counted));
      _saveTimer?.cancel();
      await ref.read(databaseProvider).setSetting(_draftKey, '');
      _done = true;
      if (!mounted) return;
      toast(context, applied ? l.auditApplied : l.auditApplyQueued);
      Navigator.pop(context);
    } on ApiException catch (e) {
      // Акт не создан: пересчёт и черновик остаются — «Провести» ещё раз, когда будет связь.
      if (mounted) toast(context, e.status == null ? l.auditOnlineOnly : e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    if (!_started) {
      return Scaffold(
        appBar: ScreenTitle(l.audit, subtitle: l.chooseWhatToCount),
        body: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            if (_draft case final d?)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Panel(
                  onTap: () => _start(
                    d['category'] as String?,
                    counted: {for (final e in (d['counted'] as Map).entries) e.key.toString(): (e.value as num).toDouble()},
                  ),
                  child: Text(
                    l.auditContinue((d['counted'] as Map).length),
                    style: const TextStyle(fontWeight: FontWeight.w800, color: T.success),
                  ),
                ),
              ),
            Panel(
              onTap: () => _start(null),
              child: Text(l.allStock, style: const TextStyle(fontWeight: FontWeight.w700)),
            ),
            const SizedBox(height: 8),
            for (final c in _categories)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Panel(
                  onTap: () => _start(c),
                  child: Text(c, style: const TextStyle(fontWeight: FontWeight.w600)),
                ),
              ),
          ],
        ),
      );
    }
    final q = _q.text.trim().toLowerCase();
    final shown = _products.where((p) {
      final c = _counted[p.id];
      // 2026-10-22: поиск по названию, штрихкоду, артикулу
      if (q.isNotEmpty && !p.searchKey.contains(q) && !(p.barcode ?? '').contains(q)) return false;
      return switch (_filter) {
        'unchecked' => c == null,
        'diff' => c != null && (c - p.quantity).abs() > 0.0001,
        _ => true,
      };
    }).toList();
    // 2026-10-22: выход с непроведённым пересчётом — с вопросом (черновик и так сохранён).
    return LeaveGuard(ask: () => _counted.isNotEmpty && !_done, child: Scaffold(
      appBar: ScreenTitle(
        '${l.audit} · ${_category ?? l.allStock}',
        subtitle: l.checkedOf(_counted.length, _products.length),
        actions: [
          IconButton.filled(
            tooltip: l.a11yScanner, // 2026-10-26 (живой аудит, П7): подпись для TalkBack
            style: IconButton.styleFrom(backgroundColor: T.navy),
            onPressed: _scan,
            icon: const Icon(Icons.qr_code_scanner_rounded, color: T.accent),
          ),
        ],
      ),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: LinearProgressIndicator(
              value: _products.isEmpty ? 0 : _counted.length / _products.length,
              minHeight: 6,
              borderRadius: BorderRadius.circular(3),
              color: T.accent,
              backgroundColor: pal.border,
            ),
          ),
          SizedBox(
            height: 56, // 2026-10-26 (живой аудит, П7): 48 dp обрезали чипы (поиск 48 + отступ 8)
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 0),
              children: [
                SizedBox(
                  width: 170,
                  child: TextField(
                    controller: _q,
                    onChanged: (_) => setState(() {}),
                    decoration: InputDecoration(
                      isDense: true,
                      prefixIcon: const Icon(Icons.search_rounded, size: 18),
                      hintText: l.searchProduct,
                    ),
                  ),
                ),
                const SizedBox(width: 6),
                for (final (v, label) in [('all', l.all), ('unchecked', l.notChecked), ('diff', l.differences)])
                  Padding(
                    padding: const EdgeInsets.only(right: 6),
                    child: ChoiceChip(
                      label: Text(label),
                      selected: _filter == v,
                      labelStyle: TextStyle(color: _filter == v ? Colors.white : pal.text, fontWeight: FontWeight.w600),
                      onSelected: (_) => setState(() => _filter = v),
                    ),
                  ),
              ],
            ),
          ),
          Expanded(
            child: ListView.builder(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
              itemCount: shown.length,
              itemBuilder: (c, i) {
                final p = shown[i];
                final counted = _counted[p.id];
                final diff = counted == null ? null : counted - p.quantity;
                final color = diff == null ? pal.border : (diff.abs() < 0.0001 ? T.success : T.danger);
                return Container(
                  margin: const EdgeInsets.only(bottom: 8),
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: diff != null && diff.abs() >= 0.0001 ? T.dangerSoft.withValues(alpha: pal.dark ? .15 : 1) : pal.panel,
                    border: Border.all(color: color),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(p.name, style: const TextStyle(fontWeight: FontWeight.w600)),
                            Text(
                              diff == null
                                  ? l.byProgram(qtyText(p.quantity), p.unit)
                                  : diff.abs() < 0.0001
                                  ? l.matches
                                  : (diff < 0 ? l.shortBy(qtyText(-diff)) : l.extraBy(qtyText(diff))),
                              style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: diff == null ? pal.textSoft : color),
                            ),
                          ],
                        ),
                      ),
                      SizedBox(
                        width: 90,
                        child: TextField(
                          controller: _ctrlFor(p),
                          keyboardType: const TextInputType.numberWithOptions(decimal: true),
                          textAlign: TextAlign.center,
                          decoration: const InputDecoration(hintText: '?'),
                          onChanged: (s) {
                            setState(() {
                              final v = parseNum(s);
                              if (v == null) {
                                _counted.remove(p.id);
                              } else {
                                _counted[p.id] = v;
                              }
                            });
                            _saveDraftSoon();
                          },
                        ),
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(l.auditApplyHint, style: TextStyle(fontSize: 12, color: pal.textSoft)),
              const SizedBox(height: 8),
              FilledButton(
                onPressed: _busy || _counted.isEmpty ? null : _send,
                child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.auditApply),
              ),
            ],
          ),
        ),
      ),
    ));
  }
}

/// Кнопка действия склада: значок и подпись в две строки.
class _StockAction extends StatelessWidget {
  final IconData icon;
  final String text;
  final VoidCallback onTap;
  const _StockAction({required this.icon, required this.text, required this.onTap});
  @override
  Widget build(BuildContext context) => Panel(
    onTap: onTap,
    padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 10),
    child: Column(
      children: [
        Icon(icon, color: context.pal.action),
        const SizedBox(height: 4),
        Text(
          text,
          maxLines: 2,
          textAlign: TextAlign.center,
          style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, height: 1.15),
        ),
      ],
    ),
  );
}
