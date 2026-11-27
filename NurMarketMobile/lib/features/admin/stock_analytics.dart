import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/ui/infographic/chart_logic.dart';
import '../../core/ui/infographic/infographics.dart';
import '../../core/ui/link_banner.dart';
import '../../core/util/product_picker.dart' show isLowStock;
import '../../core/util/ui.dart';
import '../sale/catalog_repository.dart';
import '../stock/product_batches.dart' show parseBatchDate;
import 'analytics.dart' show reportError;

// 2026-11-27 (просьба владельца «в мобилке аналитика где? Аналитика по складу? Общее количество товаров и тому подобное»): экран
// «Аналитика склада» для владельца и администратора. Цифры — из каталога на телефоне (тот же, что у списка «Склад»; свежие остатки
// подтягивает фоновая синхронизация, при открытии экрана и по «потянуть вниз» она запускается ещё раз), ничего не выдумывается.
// Закупочные цены и наценка — только Session.canSeePurchase (как карточка товара и ревизия). Последние поступления — с сервера
// (suppliers/receipts/, только при связи и только с правом на закупку). Плитки «внимания» открывают «Склад» с тем же условием фильтра
// (stock_screens.dart: zero, low, minus, noprice, nocode) — число на плитке и длина списка совпадают.

/// Строка склада для расчёта: только нужные поля (15 000 товаров читаются без картинок, акций и прочего).
class StockRow {
  final String name;
  final double price;

  /// null или ≤ 0 — закупка не задана.
  final double? purchase;
  final double quantity;
  final double minQuantity;
  final String? barcode;
  final String altBarcodes;
  final String? plu;
  final String category;
  final bool isWeight;
  const StockRow({
    required this.name,
    this.price = 0,
    this.purchase,
    this.quantity = 0,
    this.minQuantity = 0,
    this.barcode,
    this.altBarcodes = '',
    this.plu,
    this.category = '',
    this.isWeight = false,
  });

  /// Нет ни основного, ни дополнительного штрихкода, ни PLU весов — то же условие, что фильтр `nocode` списка «Склад».
  bool get noCode => (barcode == null || barcode!.isEmpty) && altBarcodes.isEmpty && (plu == null || plu!.isEmpty);
}

/// Итоги склада. Услуги в расчёт не входят (их убирает загрузка).
class StockStats {
  final int positions;

  /// Сумма остатков без весовых товаров (кг и штуки в одну сумму не складываем) и без товаров в минусе.
  final double units;

  /// Остаток × цена продажи (товары в минусе не входят).
  final double retailValue;

  /// Остаток × закупка — только по товарам, у которых закупка задана.
  final double purchaseValue;

  /// Сколько товаров с остатком не имеют закупки (в [purchaseValue] не вошли).
  final int purchaseMissing;

  /// Наценка на остаток — по товарам, у которых заданы и закупка, и цена продажи; null — таких товаров нет.
  final double? markup;
  final double? markupPercent;
  final int zero;
  final int low;
  final int minus;
  final int noPrice;
  final int noCode;

  /// Стоимость остатка по цене продажи по категориям (пустое имя — без категории), по убыванию.
  final List<(String, double)> categories;

  /// Топ товаров по стоимости остатка (цена продажи), по убыванию.
  final List<(String, double)> top;
  const StockStats({
    required this.positions,
    required this.units,
    required this.retailValue,
    required this.purchaseValue,
    required this.purchaseMissing,
    required this.markup,
    required this.markupPercent,
    required this.zero,
    required this.low,
    required this.minus,
    required this.noPrice,
    required this.noCode,
    required this.categories,
    required this.top,
  });

  /// Есть ли закупочные цены хоть у одного товара с остатком.
  bool get hasPurchase => purchaseValue > 0;

  static StockStats of(Iterable<StockRow> rows, {int topCount = 10}) {
    var n = 0, zero = 0, low = 0, minus = 0, noPrice = 0, noCode = 0, missing = 0;
    var units = 0.0, retail = 0.0, purchase = 0.0, markupBase = 0.0, markupSum = 0.0;
    var hasMarkup = false;
    final cats = <String, double>{};
    final items = <(String, double)>[];
    for (final r in rows) {
      n++;
      final q = r.quantity;
      if (q < 0) {
        minus++;
      } else if (q == 0) {
        zero++;
      } else if (isLowStock(q, r.minQuantity)) {
        low++;
      }
      if (r.price <= 0) noPrice++;
      if (r.noCode) noCode++;
      if (q <= 0) continue;
      if (!r.isWeight) units += q;
      final value = q * r.price;
      retail += value;
      if (value > 0) {
        final c = r.category.trim();
        cats[c] = (cats[c] ?? 0) + value;
        items.add((r.name, value));
      }
      final p = r.purchase;
      if (p != null && p > 0) {
        purchase += q * p;
        if (r.price > 0) {
          hasMarkup = true;
          markupBase += q * p;
          markupSum += q * (r.price - p);
        }
      } else {
        missing++;
      }
    }
    final cl = cats.entries.map((e) => (e.key, e.value)).toList()..sort((a, b) => b.$2.compareTo(a.$2));
    items.sort((a, b) => b.$2.compareTo(a.$2));
    return StockStats(
      positions: n,
      units: units,
      retailValue: retail,
      purchaseValue: purchase,
      purchaseMissing: missing,
      markup: hasMarkup ? markupSum : null,
      markupPercent: hasMarkup && markupBase > 0 ? markupSum / markupBase * 100 : null,
      zero: zero,
      low: low,
      minus: minus,
      noPrice: noPrice,
      noCode: noCode,
      categories: cl,
      top: items.take(topCount).toList(),
    );
  }
}

/// Последний приход поставщика (строка списка «Последние поступления»).
class RecentReceipt {
  final String number;
  final String supplier;
  final DateTime? at;
  final double qty;
  final double sum;
  const RecentReceipt({required this.number, required this.supplier, this.at, this.qty = 0, this.sum = 0});

  static RecentReceipt? parse(Map<String, dynamic> r) {
    final id = str(r, 'id') ?? '';
    final raw = r['items'] is List && (r['items'] as List).isNotEmpty ? r['items'] : r['lines'];
    var qty = 0.0, sum = 0.0;
    if (raw is List) {
      for (final it in raw.whereType<Map>()) {
        final m = it.cast<String, dynamic>();
        final q = num0(m, 'qty') != 0 ? num0(m, 'qty') : num0(m, 'quantity');
        qty += q;
        sum += q * num0(m, 'purchase_price');
      }
    }
    var supplier = str(r, 'supplier_name') ?? '';
    if (supplier.isEmpty && r['supplier'] is Map) {
      final s = (r['supplier'] as Map).cast<String, dynamic>();
      supplier = str(s, 'full_name') ?? str(s, 'name') ?? str(s, 'llc') ?? '';
    }
    var number = str(r, 'number') ?? str(r, 'doc_number') ?? str(r, 'document_number') ?? str(r, 'receipt_number') ?? str(r, 'invoice_number') ?? '';
    if (number.isEmpty) number = id.length > 8 ? id.substring(0, 8).toUpperCase() : id;
    if (number.isEmpty && supplier.isEmpty && qty == 0) return null;
    return RecentReceipt(number: number, supplier: supplier, at: parseBatchDate(str(r, 'created_at') ?? str(r, 'date')), qty: qty, sum: sum);
  }
}

/// Данные экрана: товары — из базы телефона, свежесть — синхронизация каталога, поступления — с сервера.
class StockAnalyticsRepository {
  final ApiClient api;
  final AppDatabase db;
  final CatalogRepository catalog;
  StockAnalyticsRepository(this.api, this.db, this.catalog);

  /// Все товары склада (без услуг) одним запросом по нужным колонкам.
  Future<List<StockRow>> rows() async {
    final p = db.products;
    final q = db.selectOnly(p)
      ..addColumns([p.name, p.price, p.purchasePrice, p.quantity, p.minQuantity, p.barcode, p.altBarcodes, p.plu, p.category, p.isWeight])
      ..where(p.isService.equals(false));
    return [
      for (final r in await q.get())
        StockRow(
          name: r.read(p.name) ?? '',
          price: r.read(p.price) ?? 0,
          purchase: r.read(p.purchasePrice),
          quantity: r.read(p.quantity) ?? 0,
          minQuantity: r.read(p.minQuantity) ?? 0,
          barcode: r.read(p.barcode),
          altBarcodes: r.read(p.altBarcodes) ?? '',
          plu: r.read(p.plu),
          category: r.read(p.category) ?? '',
          isWeight: r.read(p.isWeight) ?? false,
        ),
    ];
  }

  /// Свежие остатки с сервера (лента изменений каталога). ApiException — нет связи.
  Future<void> refreshCatalog() => catalog.sync();

  /// Последние приходы поставщиков: сервер отдаёт новые сверху.
  Future<List<RecentReceipt>> recentReceipts({int count = 7}) async {
    final data = await api.get('api/main/suppliers/receipts/', query: {'page': 1, 'limit': 20});
    return [
      for (final r in asList(data)) ?RecentReceipt.parse(r),
    ].take(count).toList();
  }
}

final stockAnalyticsRepoProvider = Provider((ref) => StockAnalyticsRepository(ref.watch(apiProvider), ref.watch(databaseProvider), ref.watch(catalogRepoProvider)));

class StockAnalyticsScreen extends ConsumerStatefulWidget {
  const StockAnalyticsScreen({super.key});
  @override
  ConsumerState<StockAnalyticsScreen> createState() => _StockAnalyticsScreenState();
}

class _StockAnalyticsScreenState extends ConsumerState<StockAnalyticsScreen> {
  StockStats? _stats;
  bool _failed = false;
  bool _offline = false;
  DateTime? _at;
  List<RecentReceipt>? _receipts;
  bool _receiptsFailed = false;
  int _seq = 0;

  bool get _money => ref.read(sessionProvider)?.canSeePurchase ?? false;

  @override
  void initState() {
    super.initState();
    unawaited(_run());
  }

  /// Сначала цифры с телефона (сразу), потом — свежие данные с сервера, если есть связь.
  Future<void> _run() async {
    final seq = ++_seq;
    await _local(seq);
    if (!mounted || seq != _seq) return;
    final repo = ref.read(stockAnalyticsRepoProvider);
    try {
      await repo.refreshCatalog();
      if (mounted && seq == _seq) setState(() => _offline = false);
    } on ApiException {
      if (mounted && seq == _seq) setState(() => _offline = true);
    } catch (_) {
      // каталог не обновился по другой причине — остаются данные телефона
    }
    if (!mounted || seq != _seq) return;
    await _local(seq);
    if (!_money || !mounted || seq != _seq) return;
    try {
      final r = await repo.recentReceipts();
      if (mounted && seq == _seq) {
        setState(() {
          _receipts = r;
          _receiptsFailed = false;
        });
      }
    } on ApiException {
      if (mounted && seq == _seq) setState(() => _receiptsFailed = true);
    } catch (_) {
      if (mounted && seq == _seq) setState(() => _receiptsFailed = true);
    }
  }

  Future<void> _local(int seq) async {
    try {
      final repo = ref.read(stockAnalyticsRepoProvider);
      final stats = StockStats.of(await repo.rows());
      final at = await LinkBanner.catalogAt(ref);
      if (!mounted || seq != _seq) return;
      setState(() {
        _stats = stats;
        _at = at;
        _failed = false;
      });
    } catch (_) {
      if (mounted && seq == _seq && _stats == null) setState(() => _failed = true);
    }
  }

  static String _dmy(DateTime d) => '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year}';

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final showMoney = ref.watch(sessionProvider.select((s) => s?.canSeePurchase ?? false));
    const gap = SizedBox(height: 12);
    final s = _stats;
    final children = <Widget>[];
    // нет связи — плашка «Нет связи · Остатки на ЧЧ:ММ»; со связью — тихая строка «Остатки на ЧЧ:ММ»
    if (_offline) {
      children.addAll([LinkBanner(force: true, catalog: true), gap]);
    } else if (_at != null) {
      children.addAll([
        Align(
          alignment: Alignment.centerRight,
          child: Text(l.stockAsOf(TimeOfDay.fromDateTime(_at!).format(context)), style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
        ),
        const SizedBox(height: 6),
      ]);
    }
    if (s == null && _failed) {
      children.add(reportError(context, null, () => unawaited(_run())));
    } else if (s == null) {
      children.addAll([KpiTile(label: l.saPositions, hero: true), gap, InfoCardSkeleton(title: l.saByCategory)]);
    } else if (s.positions == 0) {
      children.add(InfoCard(title: l.saTitle, child: InfoEmpty(title: l.saEmpty, hint: l.saEmptyHint, icon: Icons.inventory_2_outlined)));
    } else {
      final topColors = ChartColors.assign([for (final t in s.top) t.$1]);
      // 2026-11-27: go, не push — «Склад» живёт во вкладках главной; push поверх открывал пустой экран (проверено на эмуляторе, так же ведёт себя плитка «Заканчиваются» в «Управлении»)
      void open(String filter) => context.go('/stock?filter=$filter');
      KpiTile attn(String label, int n, String filter, {Color? color}) =>
          KpiTile(label: label, value: '$n', valueColor: n > 0 ? color : null, onTap: () => open(filter));
      children.addAll([
        KpiTile(label: l.saPositions, value: '${s.positions}', hero: true, how: l.saHow),
        gap,
        KpiGrid([
          KpiTile(label: l.saUnits, value: qtyText(s.units)),
          KpiTile(label: l.saRetail, value: kpiMoney(context, s.retailValue), unit: l.som),
          if (showMoney && s.hasPurchase) KpiTile(label: l.saPurchase, value: kpiMoney(context, s.purchaseValue), unit: l.som),
          if (showMoney && s.markup != null)
            KpiTile(
              label: l.saMarkup,
              value: kpiMoney(context, s.markup!),
              unit: l.som,
              valueColor: s.markup! < 0 ? pal.danger : pal.success,
              note: s.markupPercent == null ? null : '${s.markupPercent!.toStringAsFixed(1).replaceAll('.', ',')} %',
            ),
        ]),
        if (showMoney && s.purchaseMissing > 0) ...[const SizedBox(height: 8), InfoNote(l.saPurchaseMissing(s.purchaseMissing))],
        gap,
        Text(l.saAttention, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
        const SizedBox(height: 2),
        Text(l.saAttentionHint, style: TextStyle(fontSize: 12, color: pal.textSoft)),
        const SizedBox(height: 8),
        KpiGrid([
          attn(l.saZero, s.zero, 'zero', color: pal.warn),
          attn(l.saLow, s.low, 'low', color: pal.warn),
          attn(l.saMinus, s.minus, 'minus', color: pal.danger),
          attn(l.saNoPrice, s.noPrice, 'noprice', color: pal.warn),
          attn(l.saNoCode, s.noCode, 'nocode', color: pal.warn),
        ]),
        gap,
        DonutCard(
          title: l.saByCategory,
          subtitle: l.saBySale,
          semanticsName: l.saByCategory,
          unit: l.som,
          slices: [for (final c in s.categories) Slice(c.$1.isEmpty ? '_none' : c.$1, c.$1.isEmpty ? l.saNoCategory : c.$1, c.$2)],
        ),
        gap,
        InfoCard(
          title: l.saTop,
          subtitle: '${l.saBySale}, ${l.som}',
          child: s.top.isEmpty
              ? InfoEmpty(title: l.infNoData, hint: l.saEmptyHint, icon: Icons.inventory_2_outlined)
              : TopBars(rows: [for (var i = 0; i < s.top.length; i++) TopBarRow(s.top[i].$1, s.top[i].$2, topColors[i])], unit: l.som),
        ),
        if (showMoney) ...[gap, _recentCard(context)],
      ]);
    }
    return Scaffold(
      appBar: ScreenTitle(l.saTitle),
      body: RefreshIndicator(
        onRefresh: _run,
        child: ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 24), physics: const AlwaysScrollableScrollPhysics(), children: children),
      ),
    );
  }

  Widget _recentCard(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final r = _receipts;
    Widget body;
    if (r == null) {
      body = Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: Text(_receiptsFailed ? l.saRecentNoLink : '…', textAlign: TextAlign.center, style: TextStyle(fontSize: 13, color: pal.textSoft)),
      );
    } else if (r.isEmpty) {
      body = InfoEmpty(title: l.saRecentNone, icon: Icons.move_to_inbox_outlined);
    } else {
      body = Column(
        children: [
          for (final x in r)
            ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          x.supplier.isEmpty ? '№ ${x.number}' : '№ ${x.number} · ${x.supplier}',
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w500),
                        ),
                        if (x.at != null) Text(_dmy(x.at!), style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  if (x.sum > 0) Text('${money(x.sum)} ${l.som}', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, fontFeatures: [FontFeature.tabularFigures()])),
                ],
              ),
            ),
        ],
      );
    }
    return InfoCard(title: l.saRecent, child: body);
  }
}
