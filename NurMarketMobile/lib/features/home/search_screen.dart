import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/util/ui.dart';
import '../ops/ops_repository.dart';
import '../payment/sales_repository.dart';

/// 13.10, макет О8 «Поиск и отмена»: одно поле — товары («В чек»), клиенты, разделы приложения и отложенные чеки («Вернуть»).
class SearchScreen extends ConsumerStatefulWidget {
  const SearchScreen({super.key});
  @override
  ConsumerState<SearchScreen> createState() => _SearchScreenState();
}

class _SearchScreenState extends ConsumerState<SearchScreen> {
  final _q = TextEditingController();
  List<Product> _products = [];
  List<Client> _clients = [];
  List<HeldCart> _held = [];
  Timer? _debounce;

  @override
  void initState() {
    super.initState();
    ref.read(opsRepoProvider).held().then((h) {
      if (mounted) setState(() => _held = h);
    });
  }

  @override
  void dispose() {
    _debounce?.cancel();
    super.dispose();
  }

  void _changed(String q) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 250), () async {
      final text = q.trim();
      final products = text.isEmpty ? <Product>[] : await ref.read(databaseProvider).searchProducts(text, limit: 8);
      if (mounted) setState(() => _products = products);
      if (text.length < 2) {
        if (mounted) setState(() => _clients = []);
        return;
      }
      try {
        final clients = await ref.read(salesRepoProvider).clients(text);
        if (mounted && _q.text.trim() == text) setState(() => _clients = clients.take(5).toList());
      } on ApiException {
        // без связи — только товары и разделы
      }
    });
  }

  List<(IconData, String, String)> _sections(BuildContext context) {
    final l = context.l;
    final s = ref.read(sessionProvider);
    final admin = s?.showAdmin ?? false;
    final all = <(IconData, String, String)>[
      (Icons.shopping_cart_outlined, l.tabSale, '/sale'),
      (Icons.qr_code_scanner_rounded, l.tabScan, '/scan'),
      (Icons.receipt_long_outlined, l.tabChecks, '/checks'),
      (Icons.inventory_2_outlined, l.tabStock, '/stock'),
      (Icons.move_to_inbox_outlined, l.receiveByScanner, '/stock/receive'),
      (Icons.fact_check_outlined, l.audit, '/stock/audit'),
      (Icons.upload_file_rounded, l.bulkImport, '/stock/import'),
      (Icons.swap_vert_rounded, l.cashInOut, '/cash'),
      (Icons.account_balance_wallet_outlined, l.debts, '/debts'),
      (Icons.pause_circle_outline_rounded, l.heldChecks, '/held'),
      (Icons.event_repeat_rounded, l.rentals, '/rentals'),
      (Icons.lock_clock_outlined, l.closeShift, '/close-shift'),
      (Icons.person_outline_rounded, l.profileTitle, '/profile'),
      if (admin) ...[
        (Icons.dashboard_customize_outlined, l.admin, '/admin'),
        (Icons.sell_outlined, l.productPrice, '/admin/product'),
        (Icons.move_to_inbox_outlined, l.receiveWithPrices, '/admin/receive'),
        (Icons.photo_camera_rounded, l.smartCam, '/admin/smartcam'),
        (Icons.auto_awesome_rounded, l.advisorTitle, '/admin/advisor'),
        (Icons.dashboard_outlined, l.summaryTitle, '/admin/summary'),
        (Icons.bar_chart_rounded, l.abcTitle, '/admin/abc'),
        (Icons.shopping_bag_outlined, l.siteOrders, '/admin/orders'),
        (Icons.people_alt_outlined, l.clientsBonuses, '/admin/clients'),
        (Icons.schedule_rounded, l.shifts, '/admin/shifts'),
        (Icons.badge_outlined, l.staff, '/admin/staff'),
        (Icons.local_shipping_outlined, l.supplierOrder, '/admin/purchase'),
        (Icons.request_quote_outlined, l.quotesTitle, '/admin/quotes'),
        (Icons.account_balance_outlined, l.esfTitle, '/admin/esf'),
      ],
    ];
    // 13.10: закрытые сотруднику разделы в поиске не показываем.
    all.removeWhere((x) => s != null && !s.canRoute(x.$3));
    final q = _q.text.trim().toLowerCase();
    return q.isEmpty ? all.take(6).toList() : all.where((x) => x.$2.toLowerCase().contains(q)).toList();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    final q = _q.text.trim().toLowerCase();
    final held = q.isEmpty ? _held : _held.where((h) => h.note.toLowerCase().contains(q) || h.cart.lines.any((x) => x.name.toLowerCase().contains(q))).toList();
    final sections = _sections(context);
    Widget head(String t) => Padding(
      padding: const EdgeInsets.fromLTRB(4, 14, 4, 6),
      child: Text(
        t.toUpperCase(),
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: p.textSoft),
      ),
    );
    return Scaffold(
      appBar: ScreenTitle(l.searchAll),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        children: [
          TextField(
            controller: _q,
            autofocus: true,
            decoration: InputDecoration(hintText: l.searchAll, prefixIcon: const Icon(Icons.search_rounded)),
            onChanged: _changed,
          ),
          if (_products.isNotEmpty) head(l.productsTitle),
          for (final prod in _products)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Panel(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                child: Row(
                  children: [
                    ProductThumb(name: prod.name, url: prod.imageUrl, size: 40),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(prod.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                          Text('${qtyText(prod.quantity)} ${prod.unit} · ${money(prod.price)} ${l.som}', style: TextStyle(fontSize: 12, color: p.textSoft)),
                        ],
                      ),
                    ),
                    FilledButton(
                      style: FilledButton.styleFrom(minimumSize: const Size(80, 38)),
                      onPressed: () {
                        if (prod.isWeight) {
                          context.go('/sale');
                          return;
                        }
                        ref.read(cartProvider.notifier).add(prod);
                        toast(context, l.addedToCheckName(prod.name));
                      },
                      child: Text(l.toCheck),
                    ),
                  ],
                ),
              ),
            ),
          if (_clients.isNotEmpty) head(l.clientsTitle),
          for (final c in _clients)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Panel(
                onTap: () => context.push((ref.read(sessionProvider)?.isAdmin ?? false) ? '/admin/clients' : '/debts'),
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(c.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                    ),
                    Text(c.phone, style: TextStyle(color: p.textSoft, fontSize: 12)),
                  ],
                ),
              ),
            ),
          if (sections.isNotEmpty) head(l.sectionsTitle),
          for (final (icon, title, route) in sections)
            ListTile(
              contentPadding: const EdgeInsets.symmetric(horizontal: 4),
              leading: Icon(icon),
              title: Text(title),
              trailing: Text(
                l.openAction,
                style: TextStyle(color: context.pal.action, fontWeight: FontWeight.w700),
              ),
              onTap: () => context.push(route),
            ),
          if (held.isNotEmpty) head(l.heldChecks),
          for (final h in held)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Panel(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(
                        '${h.note.isEmpty ? h.cart.lines.map((x) => x.name).take(2).join(', ') : h.note} · ${money(h.cart.total)} ${l.som}',
                        style: const TextStyle(fontWeight: FontWeight.w600),
                      ),
                    ),
                    TextButton(
                      onPressed: () async {
                        ref.read(cartProvider.notifier).restore(h.cart);
                        await ref.read(opsRepoProvider).removeHeld(h.id);
                        if (context.mounted) context.go('/sale');
                      },
                      child: Text(l.returnAction),
                    ),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }
}
