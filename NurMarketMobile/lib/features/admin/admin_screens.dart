import 'dart:async';

import 'package:drift/drift.dart' hide Column;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:share_plus/share_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import 'admin_repository.dart';
import 'new_item.dart';
import 'admin_receive.dart';
import '../sale/catalog_repository.dart';
import '../../core/util/audit.dart';

final adminRepoProvider = Provider((ref) => AdminRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));

/// WhatsApp: местный номер «0700…» → «996700…», текст — в ссылке wa.me.
Future<void> openWhatsApp(String phone, String text) async {
  var d = phone.replaceAll(RegExp(r'\D'), '');
  if (d.length == 10 && d.startsWith('0')) d = '996${d.substring(1)}';
  if (d.length == 9) d = '996$d';
  // 14.10, снимок владельца: номер «+9960770…» (лишний 0 после кода страны) — WhatsApp его не находит.
  if (d.length == 13 && d.startsWith('9960')) d = '996${d.substring(4)}';
  final uri = Uri.parse(d.isEmpty ? 'https://wa.me/?text=${Uri.encodeComponent(text)}' : 'https://wa.me/$d?text=${Uri.encodeComponent(text)}');
  await launchUrl(uri, mode: LaunchMode.externalApplication);
}

/// Т8 + Т11: «Управление» — показатели дня и все разделы администратора.
class AdminScreen extends ConsumerStatefulWidget {
  const AdminScreen({super.key});
  @override
  ConsumerState<AdminScreen> createState() => _AdminScreenState();
}

class _AdminScreenState extends ConsumerState<AdminScreen> {
  TodayStats? _today;
  double? _debts;
  int _low = 0;
  final _find = TextEditingController();

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final db = ref.read(databaseProvider);
    final low = await (db.select(db.products)..where((p) => p.quantity.isSmallerOrEqualValue(3) & p.isService.equals(false))).get();
    if (mounted) setState(() => _low = low.length);
    try {
      final t = await ref.read(adminRepoProvider).today();
      if (mounted) setState(() => _today = t);
    } on ApiException {
      // без связи — показатели появятся позже
    }
    try {
      final d = await ref.read(opsRepoProvider).debtors();
      if (mounted) setState(() => _debts = d.fold<double>(0, (s, x) => s + x.debt));
    } on ApiException {
      // без связи
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final s = ref.watch(sessionProvider);
    Widget kpi(String label, String value, {Color? color, String? route}) => Expanded(
      child: Panel(
        onTap: route == null ? null : () => context.push(route),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(label, style: TextStyle(fontSize: 12, color: context.pal.textSoft)),
            const SizedBox(height: 4),
            Text(
              value,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800, color: color),
            ),
          ],
        ),
      ),
    );
    // 14.10, владелец: «удобство 10 из 10». Было 20 пунктов одним длинным списком — теперь группы плитками по 3 в ряд
    // (всё на 1–2 экранах), поиск раздела, показатели сверху нажимаются (выручка → сводка, заканчиваются → склад,
    // долги → долги клиентов).
    final groups = <(String, List<(IconData, String, String)>)>[
      (
        l.grpAi,
        [
          (Icons.auto_awesome_rounded, l.advisorTitle, '/admin/advisor'),
          (Icons.dashboard_outlined, l.summaryTitle, '/admin/summary'),
          (Icons.bar_chart_rounded, l.abcTitle, '/admin/abc'),
          (Icons.notifications_none_rounded, l.notifications, '/admin/notifications'),
          (Icons.delete_sweep_outlined, l.deletedFromCheck, '/admin/deletions'),
        ],
      ),
      (
        l.grpStock,
        [
          (Icons.move_to_inbox_outlined, l.receiveWithPrices, '/admin/receive'),
          (Icons.add_box_outlined, l.newProduct, '/admin/new-product'),
          (Icons.sell_outlined, l.productPrice, '/admin/product'),
          (Icons.photo_camera_rounded, l.smartCam, '/admin/smartcam'),
          (Icons.local_shipping_outlined, l.supplierOrder, '/admin/purchase'),
        ],
      ),
      (
        l.grpMoney,
        [
          (Icons.schedule_rounded, l.shifts, '/admin/shifts'),
          (Icons.shopping_bag_outlined, l.siteOrders, '/admin/orders'),
          (Icons.swap_vert_rounded, l.cashInOut, '/cash'),
          (Icons.account_balance_wallet_outlined, l.debts, '/debts'),
          (Icons.qr_code_2_rounded, l.payQrTitle, '/admin/payqr'),
          (Icons.lock_person_outlined, l.debtAccessTitle, '/admin/debt-access'),
        ],
      ),
      (
        l.grpClients,
        [
          (Icons.people_alt_outlined, l.clientsBonuses, '/admin/clients'),
          (Icons.request_quote_outlined, l.quotesTitle, '/admin/quotes'),
          (Icons.account_balance_outlined, l.esfTitle, '/admin/esf'),
        ],
      ),
      (l.grpTeam, [(Icons.badge_outlined, l.staff, '/admin/staff'), (Icons.fact_check_outlined, l.auditTitle, '/admin/audit')]),
    ];
    final q = _find.text.trim().toLowerCase();
    // 13.10: только разделы, открытые сотруднику на сайте (NurCRM → Сотрудники → доступы).
    final shown = [
      for (final (title, items) in groups)
        (
          title,
          [
            for (final x in items)
              if ((s == null || s.canRoute(x.$3)) && (q.isEmpty || x.$2.toLowerCase().contains(q))) x,
          ],
        ),
    ].where((g) => g.$2.isNotEmpty).toList();
    Widget tile((IconData, String, String) x) => Material(
      color: context.pal.panel,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(color: context.pal.border),
      ),
      child: InkWell(
        borderRadius: BorderRadius.circular(16),
        onTap: () => context.push(x.$3),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(8, 12, 8, 10),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Container(
                width: 42,
                height: 42,
                decoration: BoxDecoration(color: context.pal.accentSoft, borderRadius: BorderRadius.circular(12)),
                child: Icon(x.$1, color: context.pal.action, size: 22),
              ),
              const SizedBox(height: 8),
              Text(
                x.$2,
                textAlign: TextAlign.center,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 12.5),
              ),
            ],
          ),
        ),
      ),
    );
    return Scaffold(
      appBar: ScreenTitle(l.admin, subtitle: s?.companyName),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
          children: [
            Row(
              children: [
                kpi(l.revenueToday, _today == null ? '…' : '${money(_today!.revenue)} ${l.som}', color: context.pal.price, route: '/admin/summary'),
                const SizedBox(width: 10),
                kpi(l.checksToday, _today == null ? '…' : '${_today!.checks}', route: '/checks'),
              ],
            ),
            const SizedBox(height: 10),
            Row(
              children: [
                kpi(l.runningOut(_low), '$_low', color: _low > 0 ? T.warn : null, route: '/stock?filter=low'),
                const SizedBox(width: 10),
                kpi(l.clientsOwe, _debts == null ? '…' : '${money(_debts!)} ${l.som}', color: (_debts ?? 0) > 0 ? T.danger : null, route: '/debts'),
              ],
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _find,
              decoration: InputDecoration(hintText: l.findSection, prefixIcon: const Icon(Icons.search_rounded), isDense: true),
              onChanged: (_) => setState(() {}),
            ),
            for (final (title, items) in shown) ...[
              Padding(
                padding: const EdgeInsets.fromLTRB(4, 16, 4, 8),
                child: Text(
                  title.toUpperCase(),
                  style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w800, letterSpacing: .6, color: context.pal.textSoft),
                ),
              ),
              GridView.count(
                crossAxisCount: 3,
                shrinkWrap: true,
                physics: const NeverScrollableScrollPhysics(),
                mainAxisSpacing: 8,
                crossAxisSpacing: 8,
                childAspectRatio: 1.05,
                children: [for (final x in items) tile(x)],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// Т12: товар — найти (название или штрихкод), увидеть остаток, поменять цену продажи и закупки.
class ProductPriceScreen extends ConsumerStatefulWidget {
  const ProductPriceScreen({super.key});
  @override
  ConsumerState<ProductPriceScreen> createState() => _ProductPriceScreenState();
}

class _ProductPriceScreenState extends ConsumerState<ProductPriceScreen> {
  final _q = TextEditingController();
  List<Product> _rows = [];
  // 14.10, владелец: «фильтр везде, где есть товары» — остаток и категория.
  StockFilter _stock = StockFilter.all;
  String? _category;

  @override
  void initState() {
    super.initState();
    _search('');
  }

  Future<void> _search(String q) async {
    final db = ref.read(databaseProvider);
    final byCode = q.trim().isEmpty ? null : await db.productByBarcode(q.trim());
    final rows = byCode != null ? [byCode] : await db.searchProducts(q, category: _category, limit: 300);
    if (mounted) setState(() => _rows = rows.where((p) => stockFilterMatch(p, _stock)).toList());
  }

  Future<void> _edit(Product p) async {
    final l = context.l;
    Map<String, dynamic>? full;
    try {
      full = await ref.read(adminRepoProvider).product(p.id);
    } on ApiException {
      full = null;
    }
    if (!mounted) return;
    final price = TextEditingController(text: money(p.price).replaceAll(' ', '').replaceAll(' ', ''));
    final cost = num0(full, 'purchase_price');
    final purchase = TextEditingController(text: cost > 0 ? money(cost).replaceAll(' ', '').replaceAll(' ', '') : '');
    final whole = num0(full, 'wholesale_price');
    final wholesale = TextEditingController(text: whole > 0 ? money(whole).replaceAll(' ', '').replaceAll(' ', '') : '');
    // 14.10, владелец: «мало — надо количество, категорию, бренд, фото; кнопка сохранить уходит вниз». Поля прокручиваются,
    // «Сохранить» всегда видна внизу окна. Категория/бренд/остаток уходят на сервер, только если их поменяли.
    String nameOf(String key) {
      final v = full?['${key}_name'] ?? full?[key];
      if (v is Map) return '${v['name'] ?? ''}';
      return v is String ? v : '';
    }

    final isService = full?['kind'] == 'service';
    final startQty = full == null ? p.quantity : num0(full, 'quantity');
    final qty = TextEditingController(text: qtyText(startQty).replaceAll(' ', ''));
    final startCategory = nameOf('category');
    final startBrand = nameOf('brand');
    final category = TextEditingController(text: startCategory);
    final brand = TextEditingController(text: startBrand);
    final imageUrl = full == null ? null : CatalogRepository.productImageUrl(full);
    final photos = <Uint8List>[];
    Future<void> addPhoto(void Function(void Function()) set, Future<Uint8List?> Function() pick) async {
      final bytes = await pick();
      if (bytes != null) set(() => photos.add(bytes));
    }

    final ok = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) {
          final pr = parseNum(price.text) ?? 0;
          final co = parseNum(purchase.text) ?? 0;
          return ConstrainedBox(
            constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(c).height * .9),
            child: SafeArea(
              child: Padding(
                padding: EdgeInsets.fromLTRB(20, 20, 20, MediaQuery.viewInsetsOf(c).bottom + 12),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Flexible(
                      child: SingleChildScrollView(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            Text(p.name, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
                            Text(
                              '${l.onStockLabel}: ${qtyText(p.quantity)} ${p.unit}${p.barcode == null ? '' : ' · ${p.barcode}'}',
                              style: TextStyle(color: c.pal.textSoft, fontSize: 12.5),
                            ),
                            const SizedBox(height: 14),
                            FieldLabel(l.sellingPrice),
                            TextField(controller: price, keyboardType: const TextInputType.numberWithOptions(decimal: true), onChanged: (_) => set(() {})),
                            FieldLabel(l.purchasePrice),
                            TextField(controller: purchase, keyboardType: const TextInputType.numberWithOptions(decimal: true), onChanged: (_) => set(() {})),
                            FieldLabel(l.wholesalePrice),
                            TextField(controller: wholesale, keyboardType: const TextInputType.numberWithOptions(decimal: true), onChanged: (_) => set(() {})),
                            if ((parseNum(wholesale.text) ?? 0) > 0 && co > 0 && (parseNum(wholesale.text) ?? 0) < co)
                              Text(
                                l.belowCost,
                                style: const TextStyle(color: T.danger, fontWeight: FontWeight.w700, fontSize: 12),
                              ),
                            const SizedBox(height: 8),
                            if (co > 0 && pr > 0)
                              Text(
                                pr < co ? l.belowCost : l.markup(((pr - co) / co * 100).toStringAsFixed(0)),
                                style: TextStyle(color: pr < co ? T.danger : T.success, fontWeight: FontWeight.w700),
                              ),
                            if (!isService) ...[
                              FieldLabel(l.quantityLabel),
                              TextField(
                                controller: qty,
                                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                                decoration: InputDecoration(suffixText: p.unit),
                              ),
                            ],
                            FieldLabel(l.categoryLabel),
                            TextField(controller: category, textCapitalization: TextCapitalization.sentences),
                            FieldLabel(l.brandLabel),
                            TextField(controller: brand, textCapitalization: TextCapitalization.sentences),
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
                                  for (final b in photos)
                                    Padding(
                                      padding: const EdgeInsets.only(right: 8),
                                      child: ClipRRect(
                                        borderRadius: BorderRadius.circular(10),
                                        child: Image.memory(b, width: 72, height: 72, fit: BoxFit.cover, cacheWidth: 200),
                                      ),
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
                                  onPressed: () => addPhoto(set, () => pickPhoto(ImageSource.camera)),
                                ),
                                ActionChip(
                                  avatar: const Icon(Icons.photo_library_outlined, size: 18),
                                  label: Text(l.photoGallery),
                                  onPressed: () => addPhoto(set, () => pickPhoto(ImageSource.gallery)),
                                ),
                                ActionChip(
                                  avatar: const Icon(Icons.travel_explore_rounded, size: 18),
                                  label: Text(l.photoInternet),
                                  onPressed: () => addPhoto(set, () => pickInternetPhoto(c, p.name, p.barcode ?? '')),
                                ),
                              ],
                            ),
                          ],
                        ),
                      ),
                    ),
                    const SizedBox(height: 12),
                    FilledButton(
                      style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                      onPressed: () => Navigator.pop(c, true),
                      child: Text(l.save),
                    ),
                    const SizedBox(height: 6),
                    TextButton.icon(
                      style: TextButton.styleFrom(foregroundColor: T.danger),
                      onPressed: () async {
                        Navigator.pop(c, false);
                        if (await confirmDeleteProduct(context, ref, p)) setState(() {});
                      },
                      icon: const Icon(Icons.delete_outline_rounded),
                      label: Text(l.deleteProduct),
                    ),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
    if (ok != true || !mounted) return;
    final newPrice = parseNum(price.text);
    final newCost = parseNum(purchase.text);
    final newWholesale = parseNum(wholesale.text);
    final q = parseNum(qty.text);
    final newQty = !isService && q != null && (q - startQty).abs() > 0.0005 ? q : null;
    final newCategory = category.text.trim() != startCategory.trim() ? category.text : null;
    final newBrand = brand.text.trim() != startBrand.trim() ? brand.text : null;
    try {
      await ref
          .read(adminRepoProvider)
          .setPrices(p.id, price: newPrice, purchase: newCost, wholesale: newWholesale, quantity: newQty, category: newCategory, brand: newBrand);
      if (newQty != null) await ref.read(databaseProvider).customStatement('UPDATE products SET quantity = ? WHERE id = ?', [newQty, p.id]);
      for (var i = 0; i < photos.length; i++) {
        await ref.read(adminReceiveRepoProvider).uploadImage(p.id, photos[i], primary: imageUrl == null && i == 0);
      }
      if (newPrice != null) {
        final db = ref.read(databaseProvider);
        await db.customStatement('UPDATE products SET price = ? WHERE id = ?', [newPrice, p.id]);
      }
      if (newWholesale != null) await ref.read(databaseProvider).customStatement('UPDATE products SET wholesale_price = ? WHERE id = ?', [newWholesale, p.id]);
      Audit.log(
        'price',
        [
          p.name,
          if (newPrice != null && (newPrice - p.price).abs() > 0.004) '${l.sellingPrice}: ${money(p.price)} → ${money(newPrice)}',
          if (newQty != null) '${l.quantityLabel}: ${qtyText(startQty)} → ${qtyText(newQty)}',
          if (newCategory != null) '${l.categoryLabel}: $newCategory',
          if (newBrand != null) '${l.brandLabel}: $newBrand',
          if (photos.isNotEmpty) '${l.photoLabel}: +${photos.length}',
        ].join(' · '),
      );
      if (!mounted) return;
      toast(context, l.priceSaved);
      await _search(_q.text);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      appBar: ScreenTitle(l.productPrice),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
            child: TextField(
              controller: _q,
              decoration: InputDecoration(hintText: l.nameOrBarcode, prefixIcon: const Icon(Icons.search_rounded)),
              onChanged: _search,
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 6),
            child: ProductFilterBar(
              stock: _stock,
              category: _category,
              onChanged: (st, c) {
                setState(() {
                  _stock = st;
                  _category = c;
                });
                _search(_q.text);
              },
            ),
          ),
          Expanded(
            child: ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
              itemCount: _rows.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (c, i) {
                final p = _rows[i];
                return Panel(
                  onTap: () => _edit(p),
                  child: Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              p.name,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: const TextStyle(fontWeight: FontWeight.w700),
                            ),
                            Text(
                              '${l.onStockLabel}: ${qtyText(p.quantity)} ${p.unit}',
                              style: TextStyle(fontSize: 12, color: p.quantity <= 3 ? T.warn : c.pal.textSoft),
                            ),
                          ],
                        ),
                      ),
                      // 14.10, владелец: «цены справа не видны» — тёмно-синий на тёмном фоне; цвет палитры и отступ от названия.
                      const SizedBox(width: 10),
                      Text(
                        '${money(p.price)} ${l.som}',
                        style: TextStyle(fontWeight: FontWeight.w800, color: context.pal.price),
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

/// Т14: смены — кто, когда открыл и закрыл, продажи, наличные.
class ShiftsScreen extends ConsumerStatefulWidget {
  const ShiftsScreen({super.key});
  @override
  ConsumerState<ShiftsScreen> createState() => _ShiftsScreenState();
}

class _ShiftsScreenState extends ConsumerState<ShiftsScreen> {
  List<ShiftRow>? _rows;
  String? _error;

  /// 18.10, владелец («вернуть: просмотр открытых и закрытых смен и закрытие открытых»; на десктопе — с 16.10): all | open | closed.
  String _state = 'all';

  /// Закрыть чужую открытую смену (право настроек, как ShiftsWindow десктопа): сколько денег пересчитано в кассе —
  /// по умолчанию «должно быть»; разница попадёт в отчёт смены.
  Future<void> _close(ShiftRow s, double? expected) async {
    final l = context.l;
    final cash = await askNumber(context, title: '${l.closeShift}: ${[s.cashbox, s.cashier].where((e) => e.isNotEmpty).join(' · ')}', hint: l.shCounted, initial: expected ?? s.cash, ok: l.closeShift);
    if (cash == null || !mounted) return;
    try {
      await ref.read(shiftRepoProvider).close(s.id, cash);
      Audit.log('shift', '${l.closeShift}: ${s.cashier}', amount: cash);
      if (!mounted) return;
      Navigator.of(context).maybePop();
      toast(context, l.shiftClosedOk);
      await _load();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await ref.read(adminRepoProvider).shifts();
      if (mounted) setState(() => _rows = rows);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  /// 14.10, владелец: «невозможно открыть и посмотреть подробную информацию о смене» — отчёт смены по нажатию:
  /// выручка, наличные/безнал/долг, скидки, возвраты, внесения/изъятия, расход, остаток в кассе и разница.
  Future<void> _details(ShiftRow s, String Function(DateTime?) t) async {
    final l = context.l;
    final future = ref.read(adminRepoProvider).shiftReport(s.id);
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (c) => ConstrainedBox(
        constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(c).height * .85),
        child: SafeArea(
          child: FutureBuilder<Map<String, dynamic>>(
            future: future,
            builder: (c, snap) {
              if (snap.hasError) {
                final e = snap.error;
                return Padding(padding: const EdgeInsets.all(24), child: Text(e is ApiException ? e.message : '$e'));
              }
              if (!snap.hasData) {
                return const Padding(
                  padding: EdgeInsets.all(32),
                  child: Center(child: CircularProgressIndicator()),
                );
              }
              final m = snap.data!;
              double? n(String k) => m[k] == null ? null : num0(m, k);
              final pay = m['by_payment'] is Map ? (m['by_payment'] as Map).cast<String, dynamic>() : const <String, dynamic>{};
              double pv(String k) => pay[k] == null ? 0 : num0(pay, k);
              final otherNonCash = pay.keys.where((k) => !const ['cash', 'mixed_cash', 'mixed_card', 'debt'].contains(k)).fold(0.0, (a, k) => a + pv(k));
              final cash = n('cash_sales') ?? pv('cash') + pv('mixed_cash');
              final nonCash = n('noncash_sales') ?? otherNonCash + pv('mixed_card');
              final rows = <(String, double?, Color?)>[
                (l.shOpeningCash, n('opening_cash'), null),
                (l.revenue, n('sales_total') ?? s.sales, c.pal.price),
                (l.cash, cash, null),
                (l.shNonCash, nonCash, null),
                (l.shDebt, pv('debt'), null),
                (l.discount, n('discounts'), null),
                (l.shReturns, n('returns_total'), T.danger),
                (l.cashIn, n('deposits'), null),
                (l.cashOut, n('withdrawals'), null),
                (l.shIncome, n('income_total'), null),
                (l.reasonExpense, n('expense_total'), null),
                (l.shExpected, n('expected_cash') ?? s.cash, null),
                (l.shCounted, n('counted_cash'), null),
                (l.shDifference, n('difference'), (n('difference') ?? 0) < 0 ? T.danger : null),
              ];
              return ListView(
                shrinkWrap: true,
                padding: const EdgeInsets.fromLTRB(20, 20, 20, 16),
                children: [
                  Text(l.shiftDetails, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                  const SizedBox(height: 4),
                  Text([s.cashbox, s.cashier].where((e) => e.isNotEmpty).join(' · '), style: const TextStyle(fontWeight: FontWeight.w700)),
                  Text(
                    '${l.shOpened}: ${t(s.openedAt)}${s.isOpen ? ' · ${l.shiftOpenNow}' : ' · ${l.shClosed}: ${t(s.closedAt)}'} · ${l.checksCount(n('sales_count')?.round() ?? s.checks)}',
                    style: TextStyle(fontSize: 12.5, color: c.pal.textSoft),
                  ),
                  if (s.isOpen && (ref.read(sessionProvider)?.can(Session.viewSettings) ?? false)) ...[
                    const SizedBox(height: 10),
                    OutlinedButton.icon(
                      style: OutlinedButton.styleFrom(foregroundColor: T.danger, minimumSize: const Size.fromHeight(46)),
                      onPressed: () => _close(s, n('expected_cash')),
                      icon: const Icon(Icons.lock_clock_outlined),
                      label: Text(l.closeShift),
                    ),
                  ],
                  const SizedBox(height: 12),
                  for (final (label, v, color) in rows)
                    if (v != null && (v.abs() > 0.004 || label == l.revenue || label == l.cash))
                      Padding(
                        padding: const EdgeInsets.symmetric(vertical: 7),
                        child: Row(
                          children: [
                            Expanded(
                              child: Text(label, style: TextStyle(color: c.pal.textSoft)),
                            ),
                            const SizedBox(width: 10),
                            Text(
                              '${money(v)} ${l.som}',
                              style: TextStyle(fontWeight: FontWeight.w800, color: color ?? c.pal.text),
                            ),
                          ],
                        ),
                      ),
                ],
              );
            },
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    String t(DateTime? d) =>
        d == null ? '' : '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')} ${TimeOfDay.fromDateTime(d).format(context)}';
    final shown = _rows?.where((s) => _state == 'all' || (_state == 'open') == s.isOpen).toList();
    return Scaffold(
      appBar: ScreenTitle(l.shifts),
      body: _rows == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error!))
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView.separated(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                itemCount: shown!.length + 1,
                separatorBuilder: (_, _) => const SizedBox(height: 8),
                itemBuilder: (c, i) {
                  if (i == 0) {
                    final open = _rows!.where((s) => s.isOpen).length;
                    return SegmentedButton<String>(
                      segments: [
                        ButtonSegment(value: 'all', label: Text('${l.all} ${_rows!.length}')),
                        ButtonSegment(value: 'open', label: Text('${l.shiftsOpen} $open')),
                        ButtonSegment(value: 'closed', label: Text('${l.shiftsClosed} ${_rows!.length - open}')),
                      ],
                      selected: {_state},
                      showSelectedIcon: false,
                      onSelectionChanged: (v) => setState(() => _state = v.first),
                    );
                  }
                  final s = shown[i - 1];
                  return Panel(
                    onTap: () => _details(s, t),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            Icon(Icons.circle, size: 9, color: s.isOpen ? T.success : c.pal.textSoft),
                            const SizedBox(width: 8),
                            Expanded(
                              child: Text([s.cashbox, s.cashier].where((e) => e.isNotEmpty).join(' · '), style: const TextStyle(fontWeight: FontWeight.w700)),
                            ),
                            // 14.10, владелец: «цены справа не видны» — тёмно-синий на тёмном фоне; цвет палитры и отступ от названия.
                            const SizedBox(width: 10),
                            Text(
                              '${money(s.sales)} ${l.som}',
                              style: TextStyle(fontWeight: FontWeight.w800, color: context.pal.price),
                            ),
                          ],
                        ),
                        const SizedBox(height: 4),
                        Text(
                          '${t(s.openedAt)} — ${s.isOpen ? l.shiftOpenNow : l.shiftClosedAt(t(s.closedAt))} · ${l.checksCount(s.checks)}',
                          style: TextStyle(fontSize: 12, color: c.pal.textSoft),
                        ),
                      ],
                    ),
                  );
                },
              ),
            ),
    );
  }
}

String orderStatusText(BuildContext context, String s) => switch (s) {
  'new' => context.l.orderNew,
  'accepted' || 'pending' => context.l.orderPicking,
  'ready' => context.l.orderReady,
  'done' || 'completed' => context.l.orderDone,
  'canceled' => context.l.orderCanceled,
  _ => s,
};

/// Т22: заказы с сайта и бота — новые, в сборке, готовые; «Собрать заказ» (СЗ5), «Выдать».
class OrdersScreen extends ConsumerStatefulWidget {
  const OrdersScreen({super.key});
  @override
  ConsumerState<OrdersScreen> createState() => _OrdersScreenState();
}

class _OrdersScreenState extends ConsumerState<OrdersScreen> {
  List<SiteOrder>? _rows;
  String? _error;
  bool _showDone = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await ref.read(adminRepoProvider).orders();
      if (mounted) setState(() => _rows = rows);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  Future<void> _status(SiteOrder o, String status) async {
    try {
      await ref.read(adminRepoProvider).setOrderStatus(o.id, status);
      if (!mounted) return;
      toast(context, context.l.statusSaved);
      await _load();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final rows = (_rows ?? []).where((o) => _showDone || !o.finished).toList();
    return Scaffold(
      appBar: ScreenTitle(
        l.siteOrders,
        actions: [
          IconButton(onPressed: () => setState(() => _showDone = !_showDone), icon: Icon(_showDone ? Icons.filter_alt_off_rounded : Icons.filter_alt_rounded)),
        ],
      ),
      body: _rows == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error!))
          : RefreshIndicator(
              onRefresh: _load,
              child: rows.isEmpty
                  ? ListView(
                      children: [
                        Padding(
                          padding: const EdgeInsets.all(32),
                          child: Center(child: Text(l.empty)),
                        ),
                      ],
                    )
                  : ListView.separated(
                      padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                      itemCount: rows.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 8),
                      itemBuilder: (c, i) {
                        final o = rows[i];
                        return Panel(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              Row(
                                children: [
                                  Expanded(
                                    child: Text('№${o.number} · ${o.customer}', style: const TextStyle(fontWeight: FontWeight.w800)),
                                  ),
                                  // 14.10, владелец: «цены справа не видны» — тёмно-синий на тёмном фоне; цвет палитры и отступ от названия.
                                  const SizedBox(width: 10),
                                  Text(
                                    '${money(o.total)} ${l.som}',
                                    style: TextStyle(fontWeight: FontWeight.w800, color: context.pal.price),
                                  ),
                                ],
                              ),
                              Text(
                                '${o.phone} · ${o.items.length} · ${orderStatusText(context, o.status)}',
                                style: TextStyle(fontSize: 12, color: c.pal.textSoft),
                              ),
                              const SizedBox(height: 8),
                              if (!o.finished)
                                Row(
                                  children: [
                                    if (o.status != 'ready')
                                      Expanded(
                                        child: FilledButton(
                                          onPressed: () async {
                                            if (o.status == 'new') await _status(o, 'accepted');
                                            if (!context.mounted) return;
                                            await context.push('/admin/pick', extra: o);
                                            await _load();
                                          },
                                          child: Text(l.pickOrder),
                                        ),
                                      ),
                                    if (o.status == 'ready') ...[
                                      Expanded(
                                        // 14.10, владелец: сообщение сразу с текстом «заказ готов», а не пустой чат.
                                        child: OutlinedButton(
                                          onPressed: () => openWhatsApp(
                                            o.phone,
                                            l.orderReadyNotify(o.customer.trim().isEmpty ? '' : o.customer.trim(), o.number, money(o.total)),
                                          ),
                                          child: Text(l.notifyCustomer),
                                        ),
                                      ),
                                      const SizedBox(width: 8),
                                      Expanded(
                                        child: FilledButton(onPressed: () => _status(o, 'done'), child: Text(l.handOver)),
                                      ),
                                    ],
                                  ],
                                ),
                            ],
                          ),
                        );
                      },
                    ),
            ),
    );
  }
}

/// СЗ5: сборка заказа сканером — камера сверху, строки заказа снизу, скан отмечает строку; «Заказ собран» → готов, WhatsApp покупателю.
class PickScreen extends ConsumerStatefulWidget {
  final SiteOrder order;
  const PickScreen({super.key, required this.order});
  @override
  ConsumerState<PickScreen> createState() => _PickScreenState();
}

class _PickScreenState extends ConsumerState<PickScreen> {
  final Map<int, double> _picked = {};
  final Map<String, String> _barcodes = {};
  final _camera = MobileScannerController(detectionSpeed: DetectionSpeed.normal);
  String _last = '';
  DateTime _lastAt = DateTime(2000);

  @override
  void initState() {
    super.initState();
    _loadBarcodes();
  }

  @override
  void dispose() {
    _camera.dispose();
    super.dispose();
  }

  Future<void> _loadBarcodes() async {
    final db = ref.read(databaseProvider);
    for (final it in widget.order.items) {
      final p = await db.productById(it.productId);
      if (p?.barcode != null) _barcodes[p!.barcode!] = it.productId;
    }
  }

  void _onScan(String code) {
    final now = DateTime.now();
    if (code == _last && now.difference(_lastAt).inMilliseconds < 1500) return;
    _last = code;
    _lastAt = now;
    final pid = _barcodes[code];
    final items = widget.order.items;
    for (var i = 0; i < items.length; i++) {
      if (items[i].productId == pid && (_picked[i] ?? 0) < items[i].qty) {
        setState(() => _picked[i] = (_picked[i] ?? 0) + 1);
        return;
      }
    }
    toast(context, context.l.notInOrder, error: true);
  }

  Future<void> _done() async {
    final l = context.l;
    final o = widget.order;
    final code = (1000 + DateTime.now().millisecondsSinceEpoch % 9000).toString();
    final due = [for (var i = 0; i < o.items.length; i++) o.items[i].price * (_picked[i] ?? 0)].fold<double>(0, (s, x) => s + x);
    try {
      await ref.read(adminRepoProvider).setOrderStatus(o.id, 'ready');
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
      return;
    }
    if (o.phone.isNotEmpty) await openWhatsApp(o.phone, l.orderReadyMsg(o.number, money(due > 0 ? due : o.total), code));
    if (mounted) Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final o = widget.order;
    final need = o.items.fold<double>(0, (s, i) => s + i.qty);
    final got = [for (var i = 0; i < o.items.length; i++) (_picked[i] ?? 0)].fold<double>(0, (s, x) => s + x);
    return Scaffold(
      appBar: ScreenTitle('№${o.number}', subtitle: o.customer),
      body: Column(
        children: [
          SizedBox(
            height: 220,
            child: ClipRRect(
              borderRadius: BorderRadius.circular(16),
              child: MobileScanner(
                controller: _camera,
                onDetect: (cap) {
                  final v = cap.barcodes.firstOrNull?.rawValue;
                  if (v != null && v.isNotEmpty) _onScan(v);
                },
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 10, 16, 4),
            child: Row(
              children: [
                Expanded(
                  child: Text(l.pickScanHint, style: TextStyle(fontSize: 12, color: context.pal.textSoft)),
                ),
                Text(l.pickedOf(got.round(), need.round()), style: const TextStyle(fontWeight: FontWeight.w800)),
              ],
            ),
          ),
          Expanded(
            child: ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
              itemCount: o.items.length,
              separatorBuilder: (_, _) => const SizedBox(height: 6),
              itemBuilder: (c, i) {
                final it = o.items[i];
                final p = _picked[i] ?? 0;
                final full = p >= it.qty;
                return Panel(
                  color: full ? context.pal.successSoft : null,
                  padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                  child: Row(
                    children: [
                      Icon(full ? Icons.check_circle_rounded : Icons.radio_button_unchecked_rounded, color: full ? T.success : c.pal.textSoft),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(it.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                      ),
                      IconButton(onPressed: p > 0 ? () => setState(() => _picked[i] = p - 1) : null, icon: const Icon(Icons.remove_circle_outline_rounded)),
                      Text('${qtyText(p)}/${qtyText(it.qty)}', style: const TextStyle(fontWeight: FontWeight.w800)),
                      IconButton(
                        onPressed: p < it.qty ? () => setState(() => _picked[i] = p + 1) : null,
                        icon: Icon(Icons.add_circle_rounded, color: context.pal.action),
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
              child: FilledButton(
                style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(56)),
                onPressed: _done,
                child: Text(l.orderPicked),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Т21 + Л5: клиенты — поиск, долг, бонусы на счёте, начислить бонусы, написать в WhatsApp.
class ClientsScreen extends ConsumerStatefulWidget {
  const ClientsScreen({super.key});
  @override
  ConsumerState<ClientsScreen> createState() => _ClientsScreenState();
}

class _ClientsScreenState extends ConsumerState<ClientsScreen> {
  final _q = TextEditingController();
  List<ClientRow>? _rows;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await ref.read(adminRepoProvider).clients(_q.text);
      if (mounted) setState(() => _rows = rows);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  Future<void> _bonus(ClientRow c) async {
    final l = context.l;
    final v = await askNumber(context, title: l.addBonus, hint: c.name, ok: l.addBonus);
    if (v == null || v == 0 || !mounted) return;
    try {
      await ref.read(adminRepoProvider).addBonus(c.id, v, 'Телефон администратора');
      if (!mounted) return;
      toast(context, l.bonusAdded);
      await _load();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      appBar: ScreenTitle(l.clientsBonuses),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
            child: TextField(
              controller: _q,
              decoration: InputDecoration(hintText: l.clientSearch, prefixIcon: const Icon(Icons.search_rounded)),
              onSubmitted: (_) => _load(),
            ),
          ),
          Expanded(
            child: _rows == null
                ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error!))
                : ListView.separated(
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
                    itemCount: _rows!.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 8),
                    itemBuilder: (c, i) {
                      final r = _rows![i];
                      return Panel(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            Text(r.name, style: const TextStyle(fontWeight: FontWeight.w800)),
                            Text(r.phone.isEmpty ? l.noPhone : r.phone, style: TextStyle(fontSize: 12, color: c.pal.textSoft)),
                            const SizedBox(height: 6),
                            Row(
                              children: [
                                Expanded(
                                  child: Text('${l.bonusBalance}: ${money(r.bonus)}', style: const TextStyle(fontWeight: FontWeight.w700)),
                                ),
                                if (r.debt > 0)
                                  Text(
                                    '${l.debt}: ${money(r.debt)} ${l.som}',
                                    style: const TextStyle(color: T.danger, fontWeight: FontWeight.w700),
                                  ),
                              ],
                            ),
                            const SizedBox(height: 8),
                            Row(
                              children: [
                                Expanded(
                                  child: OutlinedButton(onPressed: () => _bonus(r), child: Text(l.addBonus)),
                                ),
                                if (r.phone.isNotEmpty) ...[
                                  const SizedBox(width: 8),
                                  Expanded(
                                    child: OutlinedButton(onPressed: () => openWhatsApp(r.phone, ''), child: const Text('WhatsApp')),
                                  ),
                                ],
                              ],
                            ),
                          ],
                        ),
                      );
                    },
                  ),
          ),
        ],
      ),
    );
  }
}

/// Т26: заказ поставщику — товары, которые заканчиваются, количество, отправка текстом в WhatsApp.
class SupplierOrderScreen extends ConsumerStatefulWidget {
  const SupplierOrderScreen({super.key});
  @override
  ConsumerState<SupplierOrderScreen> createState() => _SupplierOrderScreenState();
}

class _SupplierOrderScreenState extends ConsumerState<SupplierOrderScreen> {
  final _name = TextEditingController();
  final _phone = TextEditingController();
  final Map<String, (String name, String unit, double left, double qty)> _lines = {};
  // 14.10, владелец: «заказ поставщику — поиск и выбор, редактирование количества товара».
  final _search = TextEditingController();
  List<Product> _found = [];
  Timer? _debounce;

  @override
  void dispose() {
    _debounce?.cancel();
    super.dispose();
  }

  void _onSearch(String q) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 250), () async {
      final rows = q.trim().isEmpty ? <Product>[] : await ref.read(databaseProvider).searchProducts(q.trim(), limit: 20);
      if (mounted) setState(() => _found = rows.where((p) => !p.isService).toList());
    });
  }

  void _add(Product p) => setState(() {
    final cur = _lines[p.id];
    _lines[p.id] = cur == null ? (p.name, p.unit, p.quantity, 1) : (cur.$1, cur.$2, cur.$3, cur.$4 + 1);
    _search.clear();
    _found = [];
  });

  void _step(String id, double d) => setState(() {
    final line = _lines[id]!;
    final v = line.$4 + d;
    if (v <= 0) {
      _lines.remove(id);
    } else {
      _lines[id] = (line.$1, line.$2, line.$3, v);
    }
  });

  Future<void> _pickSupplier() async {
    final l = context.l;
    final all = await ref.read(adminReceiveRepoProvider).supplierContacts().catchError((_) => <(String, String, String)>[]);
    if (!mounted) return;
    var q = '';
    final picked = await showModalBottomSheet<(String, String, String)>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) {
          final rows = all.where((s) => q.isEmpty || s.$2.toLowerCase().contains(q) || s.$3.contains(q)).toList();
          return ConstrainedBox(
            constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(c).height * .8),
            child: SafeArea(
              child: Padding(
                padding: EdgeInsets.fromLTRB(16, 16, 16, MediaQuery.viewInsetsOf(c).bottom + 8),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    TextField(
                      autofocus: true,
                      decoration: InputDecoration(hintText: l.pickSupplier, prefixIcon: const Icon(Icons.search_rounded)),
                      onChanged: (v) => set(() => q = v.trim().toLowerCase()),
                    ),
                    Flexible(
                      child: rows.isEmpty
                          ? Padding(padding: const EdgeInsets.all(24), child: Text(l.nothingFound))
                          : ListView(
                              shrinkWrap: true,
                              children: [
                                for (final s in rows)
                                  ListTile(
                                    leading: const Icon(Icons.local_shipping_outlined),
                                    title: Text(s.$2),
                                    subtitle: s.$3.isEmpty ? null : Text(s.$3),
                                    onTap: () => Navigator.pop(c, s),
                                  ),
                              ],
                            ),
                    ),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
    if (picked == null) return;
    setState(() {
      _supplierId = picked.$1;
      _supplierName = picked.$2;
      _name.text = picked.$2;
      if (picked.$3.isNotEmpty) _phone.text = picked.$3;
    });
  }

  // 14.10, владелец: «при выборе поставщика добавлять только его товары; без поставщика — все, что заканчиваются».
  String? _supplierId;
  String _supplierName = '';

  Future<void> _addLow() async {
    final db = ref.read(databaseProvider);
    var low = await (db.select(db.products)..where((p) => p.quantity.isSmallerOrEqualValue(3) & p.isService.equals(false))).get();
    final name = _name.text.trim();
    if (_supplierId != null || name.isNotEmpty) {
      try {
        final ids = await ref.read(adminReceiveRepoProvider).supplierProductIds(name == _supplierName ? _supplierId : null, name);
        low = low.where((p) => ids.contains(p.id)).toList();
      } on ApiException catch (e) {
        if (mounted) toast(context, e.message, error: true);
        return;
      }
      if (low.isEmpty) {
        if (mounted) toast(context, context.l.supplierNoLow);
        return;
      }
    }
    if (!mounted) return;
    setState(() {
      for (final p in low) {
        _lines.putIfAbsent(p.id, () => (p.name, p.unit, p.quantity, 10));
      }
    });
  }

  Future<void> _qty(String id) async {
    final line = _lines[id]!;
    final v = await askNumber(context, title: context.l.orderQty, hint: line.$1, initial: line.$4);
    if (v == null) return;
    setState(() => v <= 0 ? _lines.remove(id) : _lines[id] = (line.$1, line.$2, line.$3, v));
  }

  String _text() {
    final s = ref.read(sessionProvider);
    final b = StringBuffer('Здравствуйте! Заказ от ${s?.companyName ?? ''}:\n');
    var i = 0;
    for (final l in _lines.values) {
      b.writeln('${++i}. ${l.$1} — ${qtyText(l.$4)} ${l.$2}');
    }
    b.write('Спасибо!');
    return b.toString();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      appBar: ScreenTitle(l.supplierOrder),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        children: [
          OutlinedButton.icon(onPressed: _pickSupplier, icon: const Icon(Icons.local_shipping_outlined), label: Text(l.pickSupplier)),
          FieldLabel(l.supplierName),
          TextField(controller: _name, onChanged: (_) => setState(() {})),
          FieldLabel(l.supplierPhone),
          TextField(controller: _phone, keyboardType: TextInputType.phone),
          const SizedBox(height: 12),
          TextField(
            controller: _search,
            decoration: InputDecoration(hintText: l.nameOrBarcode, prefixIcon: const Icon(Icons.search_rounded), helperText: l.addProductsHint),
            onChanged: _onSearch,
          ),
          for (final p in _found)
            ListTile(
              contentPadding: const EdgeInsets.symmetric(horizontal: 4),
              title: Text(p.name, maxLines: 2, overflow: TextOverflow.ellipsis),
              subtitle: Text('${l.onStockLabel}: ${qtyText(p.quantity)} ${p.unit}'),
              trailing: Icon(Icons.add_circle_rounded, color: context.pal.action, size: 30),
              onTap: () => _add(p),
            ),
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: () async {
              final p = await pickProduct(context, noServices: true);
              if (p != null) _add(p);
            },
            icon: const Icon(Icons.grid_view_rounded),
            label: Text(l.catalogPick),
          ),
          const SizedBox(height: 6),
          OutlinedButton.icon(
            onPressed: _addLow,
            icon: const Icon(Icons.playlist_add_rounded),
            label: Text(_name.text.trim().isEmpty ? l.runningLowAdd : l.supplierLowAdd),
          ),
          const SizedBox(height: 8),
          for (final e in _lines.entries) ...[
            // 14.10, владелец: «удаление как чат в Telegram» — смахнуть влево, справа кнопка «Убрать».
            SwipeToRemove(
              key: ValueKey(e.key),
              label: l.removeAction,
              onRemove: () => setState(() => _lines.remove(e.key)),
              child: Panel(
                onTap: () => _qty(e.key),
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(e.value.$1, style: const TextStyle(fontWeight: FontWeight.w700)),
                          Text('${l.onStockLabel}: ${qtyText(e.value.$3)} ${e.value.$2}', style: TextStyle(fontSize: 12, color: context.pal.textSoft)),
                        ],
                      ),
                    ),
                    const SizedBox(width: 8),
                    IconButton.outlined(onPressed: e.value.$4 <= 1 ? null : () => _step(e.key, -1), icon: const Icon(Icons.remove)),
                    InkWell(
                      onTap: () => _qty(e.key),
                      borderRadius: BorderRadius.circular(10),
                      child: Container(
                        constraints: const BoxConstraints(minWidth: 52),
                        height: 44,
                        alignment: Alignment.center,
                        padding: const EdgeInsets.symmetric(horizontal: 8),
                        child: Text(qtyText(e.value.$4), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
                      ),
                    ),
                    IconButton.filled(
                      style: IconButton.styleFrom(backgroundColor: context.pal.action, foregroundColor: context.pal.onAction),
                      onPressed: () => _step(e.key, 1),
                      icon: const Icon(Icons.add),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 6),
          ],
          const SizedBox(height: 12),
          FilledButton.icon(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
            onPressed: _lines.isEmpty ? null : () => openWhatsApp(_phone.text, _text()),
            icon: const Icon(Icons.send_rounded),
            label: Text(l.orderToSupplier),
          ),
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: _lines.isEmpty ? null : () => SharePlus.instance.share(ShareParams(text: _text())),
            icon: const Icon(Icons.share_rounded),
            label: Text(l.share),
          ),
        ],
      ),
    );
  }
}

/// У3: удалено из чека — журнал этого телефона (что, сколько, почему, кто).
class DeletionsScreen extends ConsumerStatefulWidget {
  const DeletionsScreen({super.key});
  @override
  ConsumerState<DeletionsScreen> createState() => _DeletionsScreenState();
}

class _DeletionsScreenState extends ConsumerState<DeletionsScreen> {
  List<DeletionRow>? _rows;

  @override
  void initState() {
    super.initState();
    ref.read(adminRepoProvider).deletions().then((r) {
      if (mounted) setState(() => _rows = r);
    });
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final rows = _rows;
    return Scaffold(
      appBar: ScreenTitle(l.deletedFromCheck),
      body: rows == null
          ? const Center(child: CircularProgressIndicator())
          : rows.isEmpty
          ? Center(child: Text(l.noDeletions))
          : ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
              itemCount: rows.length,
              separatorBuilder: (_, _) => const SizedBox(height: 6),
              itemBuilder: (c, i) {
                final r = rows[i];
                final t = TimeOfDay.fromDateTime(r.at).format(context);
                return Panel(
                  padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                  child: Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text('${r.name} × ${qtyText(r.qty)}', style: const TextStyle(fontWeight: FontWeight.w700)),
                            Text(
                              '${r.at.day.toString().padLeft(2, '0')}.${r.at.month.toString().padLeft(2, '0')} $t · ${r.cashier} · ${r.reason}',
                              style: TextStyle(fontSize: 12, color: c.pal.textSoft),
                            ),
                          ],
                        ),
                      ),
                      Text('${money(r.sum)} ${l.som}', style: const TextStyle(fontWeight: FontWeight.w800)),
                    ],
                  ),
                );
              },
            ),
    );
  }
}

/// У3 на продаже: причина удаления строки из чека (покупатель передумал, ошибка скана, не хватило денег, брак).
Future<String?> askDeleteReason(BuildContext context) {
  final l = context.l;
  final reasons = [l.reasonChanged, l.reasonScanError, l.reasonNoMoney, l.reasonDefect];
  return showModalBottomSheet<String>(
    context: context,
    builder: (c) => SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l.deleteReason, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
            const SizedBox(height: 10),
            for (final r in reasons) ...[
              OutlinedButton(
                style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48), alignment: Alignment.centerLeft),
                onPressed: () => Navigator.pop(c, r),
                child: Text(r),
              ),
              const SizedBox(height: 6),
            ],
            TextButton(onPressed: () => Navigator.pop(c), child: Text(l.cancel)),
          ],
        ),
      ),
    ),
  );
}

/// 13.10, владелец: «кнопку удалить товар добавь». Подтверждение и удаление; true — удалён.
Future<bool> confirmDeleteProduct(BuildContext context, WidgetRef ref, Product p) async {
  final l = context.l;
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(l.deleteProduct),
      content: Text(l.deleteProductAsk(p.name)),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: T.danger, foregroundColor: Colors.white),
          onPressed: () => Navigator.pop(c, true),
          child: Text(l.deleteProduct),
        ),
      ],
    ),
  );
  if (ok != true || !context.mounted) return false;
  try {
    await ref.read(adminRepoProvider).deleteProduct(p.id);
    Audit.log('product', '${l.deleteProduct}: ${p.name}');
    if (context.mounted) toast(context, l.productDeleted);
    return true;
  } on ApiException catch (e) {
    if (context.mounted) toast(context, e.message, error: true);
    return false;
  }
}

/// 14.10, владелец: удаление строки «как чат в Telegram» — смахнуть влево, открывается красная кнопка «Убрать»;
/// смахнуть обратно или нажать на строку — закрыть. Строка удаляется только по нажатию на кнопку.
class SwipeToRemove extends StatefulWidget {
  final Widget child;
  final String label;
  final VoidCallback onRemove;
  const SwipeToRemove({super.key, required this.child, required this.label, required this.onRemove});
  @override
  State<SwipeToRemove> createState() => _SwipeToRemoveState();
}

class _SwipeToRemoveState extends State<SwipeToRemove> {
  static const _width = 96.0;
  double _dx = 0;
  bool _drag = false;

  @override
  Widget build(BuildContext context) => GestureDetector(
    onHorizontalDragStart: (_) => setState(() => _drag = true),
    onHorizontalDragUpdate: (d) => setState(() => _dx = (_dx + d.delta.dx).clamp(-_width, 0)),
    onHorizontalDragEnd: (d) => setState(() {
      _drag = false;
      final v = d.primaryVelocity ?? 0;
      _dx = v < -300 || (v <= 300 && _dx < -_width / 2) ? -_width : 0;
    }),
    child: Stack(
      children: [
        Positioned.fill(
          child: Align(
            alignment: Alignment.centerRight,
            child: Material(
              color: T.danger,
              borderRadius: BorderRadius.circular(T.radiusCard),
              child: InkWell(
                borderRadius: BorderRadius.circular(T.radiusCard),
                onTap: widget.onRemove,
                child: SizedBox(
                  width: _width - 8,
                  height: double.infinity,
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      const Icon(Icons.delete_outline_rounded, color: Colors.white),
                      const SizedBox(height: 2),
                      Text(
                        widget.label,
                        style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w700, fontSize: 12.5),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
        AnimatedContainer(
          duration: _drag ? Duration.zero : const Duration(milliseconds: 180),
          curve: Curves.easeOut,
          transform: Matrix4.translationValues(_dx, 0, 0),
          child: _dx < 0
              ? GestureDetector(
                  onTap: () => setState(() => _dx = 0),
                  child: AbsorbPointer(child: widget.child),
                )
              : widget.child,
        ),
      ],
    ),
  );
}
