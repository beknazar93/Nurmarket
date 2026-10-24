import 'package:drift/drift.dart' hide Column;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';

/// Главная кассира (Т1): смена и мои продажи за смену, четыре плитки, «Заканчиваются N товаров».
class HomeScreen extends ConsumerStatefulWidget {
  const HomeScreen({super.key});
  @override
  ConsumerState<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends ConsumerState<HomeScreen> {
  int _low = 0;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _load());
  }

  /// 13.10, владелец: «открытие программы очень долгое». Раньше главный экран сначала ждал сервер (смена — до 3–7 с),
  /// и только потом показывал «Заканчиваются» из базы телефона. Теперь локальное — сразу, смена — параллельно.
  Future<void> _load() async {
    final shift = ref.read(shiftProvider.notifier).refresh();
    final db = ref.read(databaseProvider);
    await _countLow(db);
    if (await db.productCount() == 0) {
      try {
        await ref.read(catalogRepoProvider).sync();
        await _countLow(db);
      } on ApiException {
        // каталог подтянется при связи
      }
    }
    await shift;
  }

  Future<void> _countLow(AppDatabase db) async {
    // 2026-10-22 (мобилка, админ, П11): порог как на ПК — остаток меньше 10 (lowStockLimit), было ≤ 3.
    final low = await (db.select(db.products)..where((p) => lowStockSql(p) & p.isService.equals(false))).get();
    if (mounted) setState(() => _low = low.length);
  }

  Future<void> _openShift() async {
    final cash = await askNumber(context, title: context.l.openShift, hint: context.l.openingCashHint, initial: 0, ok: context.l.openShift);
    if (cash == null || !mounted) return;
    try {
      await ref.read(shiftProvider.notifier).open(cash);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final s = ref.watch(sessionProvider);
    final shift = ref.watch(shiftProvider);
    final pending = ref.watch(pendingCountProvider).value ?? 0;
    final p = context.pal;
    final opened = shift.shift?.openedAt;
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        // 13.10: viewPadding — отступ под строку состояния не «съедается» Scaffold оболочки (часы наезжали на шапку).
        padding: EdgeInsets.fromLTRB(16, MediaQuery.viewPaddingOf(context).top + 12, 16, 16),
        children: [
          Row(
            children: [
              InkWell(
                onTap: () => context.push('/profile'),
                borderRadius: BorderRadius.circular(24),
                child: CircleAvatar(
                  radius: 22,
                  backgroundColor: T.navy,
                  child: Text(
                    initials(s?.userName ?? '?').toUpperCase(),
                    style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 14),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(s?.userName ?? '', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
                    Text(
                      [s?.companyName, s?.cashboxName].whereType<String>().where((e) => e.isNotEmpty).join(' · '),
                      style: TextStyle(fontSize: 12, color: p.textSoft),
                    ),
                  ],
                ),
              ),
              // 13.10, макет О8: поиск товаров, клиентов, разделов и отложенных чеков.
              IconButton.outlined(onPressed: () => context.push('/search'), icon: const Icon(Icons.search_rounded)),
              const SizedBox(width: 6),
              Badge(
                isLabelVisible: pending > 0,
                label: Text('$pending'),
                child: IconButton.outlined(onPressed: () => context.push('/sync'), icon: const Icon(Icons.sync_rounded)),
              ),
            ],
          ),
          const SizedBox(height: 16),
          Container(
            padding: const EdgeInsets.all(18),
            decoration: BoxDecoration(color: T.navy, borderRadius: BorderRadius.circular(20)),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.circle, size: 9, color: shift.isOpen ? const Color(0xFF34D399) : const Color(0xFFF87171)),
                    const SizedBox(width: 8),
                    Text(
                      shift.isOpen ? l.shiftOpenSince(opened == null ? '' : TimeOfDay.fromDateTime(opened).format(context)) : l.shiftClosed,
                      style: const TextStyle(color: Colors.white, fontSize: 13, fontWeight: FontWeight.w600),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Text(l.mySalesForShift, style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12)),
                const SizedBox(height: 4),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    Expanded(
                      child: Text(
                        '${money(shift.shift?.salesTotal ?? 0)} ${l.som}',
                        style: const TextStyle(color: T.accent, fontSize: 30, fontWeight: FontWeight.w800),
                      ),
                    ),
                    Text(l.checksCount(shift.shift?.salesCount ?? 0), style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12)),
                  ],
                ),
                const SizedBox(height: 14),
                if (shift.isOpen)
                  OutlinedButton(
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size.fromHeight(46),
                      backgroundColor: Colors.transparent,
                      foregroundColor: Colors.white,
                      side: const BorderSide(color: Color(0x55FFFFFF)),
                    ),
                    onPressed: () => context.push('/close-shift'),
                    child: Text(l.closeShift),
                  )
                else
                  FilledButton(
                    style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(46)),
                    onPressed: _openShift,
                    child: Text(l.openShift),
                  ),
              ],
            ),
          ),
          // 2026-10-22 (мобилка, деньги, К-1): на этой кассе смена другого кассира — продавать в неё нельзя, откройте свою.
          if (!shift.isOpen && shift.foreignShift) ...[
            const SizedBox(height: 10),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(color: context.pal.warnSoft, borderRadius: BorderRadius.circular(12)),
              child: Row(
                children: [
                  const Icon(Icons.person_off_outlined, color: T.warn, size: 18),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(l.foreignShiftHint, style: const TextStyle(color: T.warn, fontSize: 12.5)),
                  ),
                ],
              ),
            ),
          ],
          if (shift.error != null) ...[
            const SizedBox(height: 10),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(color: context.pal.warnSoft, borderRadius: BorderRadius.circular(12)),
              child: Row(
                children: [
                  const Icon(Icons.wifi_off_rounded, color: T.warn, size: 18),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(l.offlineWorking, style: const TextStyle(color: T.warn, fontSize: 12.5)),
                  ),
                ],
              ),
            ),
          ],
          const SizedBox(height: 14),
          GridView.count(
            crossAxisCount: 2,
            shrinkWrap: true,
            physics: const NeverScrollableScrollPhysics(),
            mainAxisSpacing: 10,
            crossAxisSpacing: 10,
            childAspectRatio: 1.55,
            children: [
              _Tile(icon: Icons.shopping_cart_outlined, title: l.tabSale, sub: l.newCheck, accent: true, onTap: () => context.go('/sale')),
              _Tile(icon: Icons.qr_code_scanner_rounded, title: l.tabScan, sub: l.scanTileSub, onTap: () => context.go('/scan?mode=price')),
              _Tile(icon: Icons.receipt_long_outlined, title: l.tabChecks, sub: l.checksTileSub, onTap: () => context.go('/checks')),
              _Tile(icon: Icons.inventory_2_outlined, title: l.tabStock, sub: l.stockTileSub, onTap: () => context.go('/stock')),
            ],
          ),
          // 13.10: операции старой кассы — внесение/изъятие, долги, отложенные чеки.
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: () => context.push('/cash'),
                  icon: const Icon(Icons.swap_vert_rounded, size: 18),
                  label: Text(l.cashInOut, maxLines: 1, overflow: TextOverflow.ellipsis),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: () => context.push('/debts'),
                  icon: const Icon(Icons.account_balance_wallet_outlined, size: 18),
                  label: Text(l.debts, maxLines: 1, overflow: TextOverflow.ellipsis),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: () => context.push('/held'),
                  icon: const Icon(Icons.pause_circle_outline_rounded, size: 18),
                  label: Text(l.heldChecks, maxLines: 1, overflow: TextOverflow.ellipsis),
                ),
              ),
              const SizedBox(width: 8),
              // 13.10, макет С8: прокат.
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: () => context.push('/rentals'),
                  icon: const Icon(Icons.event_repeat_rounded, size: 18),
                  label: Text(l.rentals, maxLines: 1, overflow: TextOverflow.ellipsis),
                ),
              ),
            ],
          ),
          // 13.10, макет С7: услуги и запись к мастерам.
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: () => context.push('/bookings'),
            icon: const Icon(Icons.content_cut_rounded, size: 18),
            label: Text(l.bookingsTitle, maxLines: 1, overflow: TextOverflow.ellipsis),
          ),
          // 13.10, этап 2: администратору и владельцу — «Управление» (макеты Т8, Т11).
          if (s?.showAdmin ?? false) ...[
            const SizedBox(height: 10),
            Panel(
              color: T.navy,
              onTap: () => context.push('/admin'),
              child: Row(
                children: [
                  const Icon(Icons.dashboard_customize_outlined, color: T.accent),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          l.admin,
                          style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 15),
                        ),
                        Text(l.adminSub, style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12)),
                      ],
                    ),
                  ),
                  const Icon(Icons.chevron_right_rounded, color: Colors.white),
                ],
              ),
            ),
          ],
          if (_low > 0) ...[
            const SizedBox(height: 12),
            Panel(
              onTap: () => context.go('/stock?filter=low'),
              child: Row(
                children: [
                  Container(
                    width: 36,
                    height: 36,
                    decoration: BoxDecoration(color: context.pal.dangerSoft, borderRadius: BorderRadius.circular(10)),
                    alignment: Alignment.center,
                    child: const Text(
                      '!',
                      style: TextStyle(color: T.danger, fontWeight: FontWeight.w800, fontSize: 18),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(l.runningOut(_low), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14)),
                        Text(l.tellAdmin, style: TextStyle(fontSize: 12, color: p.textSoft)),
                      ],
                    ),
                  ),
                  const Icon(Icons.chevron_right_rounded),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _Tile extends StatelessWidget {
  final IconData icon;
  final String title;
  final String sub;
  final bool accent;
  final VoidCallback onTap;
  const _Tile({required this.icon, required this.title, required this.sub, required this.onTap, this.accent = false});
  @override
  Widget build(BuildContext context) => Panel(
    color: accent ? T.accent : null,
    onTap: onTap,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Icon(icon, color: accent ? T.navy : null),
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              title,
              style: TextStyle(fontWeight: FontWeight.w800, fontSize: 15, color: accent ? T.navy : null),
            ),
            Text(
              sub,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(fontSize: 11.5, color: accent ? T.navy : context.pal.textSoft),
            ),
          ],
        ),
      ],
    ),
  );
}
