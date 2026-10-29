import 'package:drift/drift.dart' hide Column;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/ui/infographic/infographics.dart' show PaymentSplit;
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../staff/staff_providers.dart' show myTasksProvider;
import '../staff/tasks_screen.dart' show TasksTodayPlate;

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
    ref.invalidate(myTasksProvider); // 2026-10-26 (В7): плашка «Задач на сегодня» обновляется вместе с главной
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
    final l = context.l;
    // 2026-10-26 (живой аудит, П2, Н-10): у поля суммы теперь есть подпись «Сумма в кассе на начало смены» и «сом».
    final cash = await askNumber(
      context,
      title: l.openShift,
      hint: l.openingCashHint,
      label: l.openingCashField,
      suffix: l.som,
      initial: 0,
      ok: l.openShift,
    );
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
    // 2026-10-26 (живой аудит, П2, С-2): большой шрифт — кнопки операций по одной в ряд, а не по две (подписи не обрезаются);
    // плитки — высота от шрифта вместо фиксированного соотношения сторон.
    final textScale = MediaQuery.textScalerOf(context).scale(1.0);
    final oneColumn = textScale > 1.15;
    final tileExtent = 64 + textScale * (15 + 3 * 11.5) * 1.35;
    final isAdmin = s?.showAdmin ?? false;
    final canOrder = s?.canRoute('/admin/purchase') ?? false;
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        // 13.10: viewPadding — отступ под строку состояния не «съедается» Scaffold оболочки (часы наезжали на шапку).
        padding: EdgeInsets.fromLTRB(16, MediaQuery.viewPaddingOf(context).top + 12, 16, 16),
        children: [
          Row(
            children: [
              // 2026-10-26 (живой аудит, П1/П2, С-7): аватар в тёмной теме — серая заливка и жёлтая рамка (был navy на чёрном).
              Semantics(
                button: true,
                label: l.profileTitle,
                excludeSemantics: true,
                child: InkWell(
                  onTap: () => context.push('/profile'),
                  borderRadius: BorderRadius.circular(24),
                  child: Container(
                    width: 48,
                    height: 48,
                    alignment: Alignment.center,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: p.avatarBg,
                      border: Border.all(color: p.avatarBorder, width: 2),
                    ),
                    child: Text(
                      initials(s?.userName ?? '?').toUpperCase(),
                      style: TextStyle(color: p.onAvatar, fontWeight: FontWeight.w800, fontSize: 14),
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(s?.userName ?? '', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
                    Text(
                      [s?.companyName, s?.cashboxName].whereType<String>().where((e) => e.isNotEmpty).join(' · '),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(fontSize: 12, color: p.textSoft),
                    ),
                  ],
                ),
              ),
              // 13.10, макет О8: поиск товаров, клиентов, разделов и отложенных чеков.
              IconButton.outlined(style: _hdrBtn, tooltip: l.searchAll, onPressed: () => context.push('/search'), icon: const Icon(Icons.search_rounded)),
              const SizedBox(width: 4),
              Badge(
                isLabelVisible: pending > 0,
                label: Text('$pending'),
                child: IconButton.outlined(style: _hdrBtn, tooltip: l.sync, onPressed: () => context.push('/sync'), icon: const Icon(Icons.sync_rounded)),
              ),
              // 2026-10-26 (живой аудит, П2, Н-6): профиль был доступен только касанием аватара — теперь и значок ⚙ в шапке.
              const SizedBox(width: 4),
              IconButton.outlined(style: _hdrBtn, tooltip: l.profileTitle, onPressed: () => context.push('/profile'), icon: const Icon(Icons.settings_outlined)),
            ],
          ),
          const SizedBox(height: 16),
          Container(
            padding: const EdgeInsets.all(18),
            decoration: BoxDecoration(
              color: p.heroBg,
              borderRadius: BorderRadius.circular(20),
              border: Border.all(color: p.heroBorder),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.circle, size: 9, color: shift.isOpen ? const Color(0xFF34D399) : const Color(0xFFF87171)),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        shift.isOpen ? l.shiftOpenSince(opened == null ? '' : TimeOfDay.fromDateTime(opened).format(context)) : l.shiftClosed,
                        style: const TextStyle(color: Colors.white, fontSize: 13, fontWeight: FontWeight.w600),
                      ),
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
                      child: FittedBox(
                        fit: BoxFit.scaleDown,
                        alignment: Alignment.centerLeft,
                        child: Text(
                          '${money(shift.shift?.salesTotal ?? 0)} ${l.som}',
                          style: const TextStyle(color: T.accent, fontSize: 30, fontWeight: FontWeight.w800),
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Text(l.checksCount(shift.shift?.salesCount ?? 0), style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12)),
                  ],
                ),
                // 2026-10-29 (инфографика, спека Fable): чем платили в этой смене — полоса и проценты (нет продаж — не показываем).
                if (shift.shift != null && (shift.shift!.cashSales + shift.shift!.nonCashSales + shift.shift!.debtSales) > 0) ...[
                  const SizedBox(height: 12),
                  PaymentSplit(cash: shift.shift!.cashSales, nonCash: shift.shift!.nonCashSales, debt: shift.shift!.debtSales, onDark: true),
                ],
                const SizedBox(height: 14),
                if (shift.isOpen)
                  OutlinedButton(
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size.fromHeight(48),
                      backgroundColor: Colors.transparent,
                      foregroundColor: Colors.white,
                      side: const BorderSide(color: Color(0x55FFFFFF)),
                    ),
                    onPressed: () => context.push('/close-shift'),
                    child: Text(l.closeShift),
                  )
                else
                  FilledButton(
                    style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                    onPressed: _openShift,
                    child: Text(l.openShift),
                  ),
              ],
            ),
          ),
          // 2026-10-26 (голос-управление, В7): «Задач на сегодня: 2 · просрочено 1» и напоминание — тапом в «Мои задачи».
          TasksTodayPlate(
            onTap: () async {
              await context.push('/tasks/my');
              if (mounted) ref.invalidate(myTasksProvider); // вернулись — плашка пересчитывается
            },
          ),
          // 2026-10-26 (живой аудит, П2, 5.1 «Главная»): администратору и владельцу «Управление» сразу под карточкой смены,
          // а не после прокрутки (макеты Т8, Т11).
          if (isAdmin) ...[
            const SizedBox(height: 10),
            Panel(
              color: p.heroBg,
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
          // 2026-10-22 (мобилка, деньги, К-1): на этой кассе смена другого кассира — продавать в неё нельзя, откройте свою.
          if (!shift.isOpen && shift.foreignShift) ...[
            const SizedBox(height: 10),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(color: context.pal.warnSoft, borderRadius: BorderRadius.circular(12)),
              child: Row(
                children: [
                  Icon(Icons.person_off_outlined, color: p.warn, size: 18),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(l.foreignShiftHint, style: TextStyle(color: p.warn, fontSize: 12.5)),
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
                  Icon(Icons.wifi_off_rounded, color: p.warn, size: 18),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(l.offlineWorking, style: TextStyle(color: p.warn, fontSize: 12.5)),
                  ),
                ],
              ),
            ),
          ],
          const SizedBox(height: 14),
          GridView(
            shrinkWrap: true,
            physics: const NeverScrollableScrollPhysics(),
            gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(crossAxisCount: 2, mainAxisSpacing: 10, crossAxisSpacing: 10, mainAxisExtent: tileExtent),
            children: [
              _Tile(icon: Icons.shopping_cart_outlined, title: l.tabSale, sub: l.newCheck, accent: true, onTap: () => context.go('/sale')),
              _Tile(icon: Icons.qr_code_scanner_rounded, title: l.tabScan, sub: l.scanTileSub, onTap: () => context.go('/scan?mode=price')),
              _Tile(icon: Icons.receipt_long_outlined, title: l.tabChecks, sub: l.checksTileSub, onTap: () => context.go('/checks')),
              _Tile(icon: Icons.inventory_2_outlined, title: l.tabStock, sub: l.stockTileSub, onTap: () => context.go('/stock')),
            ],
          ),
          // 13.10: операции старой кассы — внесение/изъятие, долги, отложенные чеки. 2026-10-26 (П2, С-2): подписи в 2 строки.
          const SizedBox(height: 10),
          LayoutBuilder(
            builder: (c, box) {
              final half = (box.maxWidth - 8) / 2;
              Widget action(IconData icon, String label, String route, {bool full = false}) => SizedBox(
                width: full || oneColumn ? box.maxWidth : half,
                child: _ActionButton(icon: icon, label: label, onTap: () => context.push(route)),
              );
              return Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  action(Icons.swap_vert_rounded, l.cashInOut, '/cash'),
                  action(Icons.account_balance_wallet_outlined, l.debts, '/debts'),
                  action(Icons.pause_circle_outline_rounded, l.heldChecks, '/held'),
                  // 13.10, макет С8: прокат.
                  action(Icons.event_repeat_rounded, l.rentals, '/rentals'),
                ],
              );
            },
          ),
          if (_low > 0) ...[
            const SizedBox(height: 12),
            Panel(
              // 2026-10-26 (П2, Н-5): «Сообщите администратору» — только кассиру. Администратору и владельцу — «заказать»
              // со ссылкой на заказ поставщику (если раздел открыт), иначе — список «Заканчиваются».
              onTap: () => isAdmin && canOrder ? context.push('/admin/purchase') : context.go('/stock?filter=low'),
              child: Row(
                children: [
                  Container(
                    width: 36,
                    height: 36,
                    decoration: BoxDecoration(color: context.pal.dangerSoft, borderRadius: BorderRadius.circular(10)),
                    alignment: Alignment.center,
                    child: Text(
                      '!',
                      style: TextStyle(color: p.danger, fontWeight: FontWeight.w800, fontSize: 18),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(l.runningOut(_low), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14)),
                        Text(isAdmin ? (canOrder ? l.supplierOrder : l.tabStock) : l.tellAdmin, style: TextStyle(fontSize: 12, color: p.textSoft)),
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

/// 2026-10-26 (живой аудит, П2, Н-2): значки шапки главной — касание 48 dp (было 40).
final _hdrBtn = IconButton.styleFrom(fixedSize: const Size(48, 48));

/// 2026-10-26 (живой аудит, П2, С-2): кнопка операции — значок и подпись до трёх строк (раньше одна строка с «…»; на кыргызском «Кийинкиге калтырылган чектер» не помещалось в две).
class _ActionButton extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback onTap;
  const _ActionButton({required this.icon, required this.label, required this.onTap});
  @override
  Widget build(BuildContext context) => OutlinedButton.icon(
    style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8)),
    onPressed: onTap,
    icon: Icon(icon, size: 18),
    label: Text(label, maxLines: 3, overflow: TextOverflow.ellipsis, textAlign: TextAlign.center, style: const TextStyle(fontSize: 13)),
  );
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
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(fontWeight: FontWeight.w800, fontSize: 15, color: accent ? T.navy : null),
            ),
            // 2026-10-26 (П2, С-2): подпись плитки — до трёх строк (кыргызский, шрифт 1,3×), высота плитки считается от шрифта.
            Text(
              sub,
              maxLines: 3,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(fontSize: 11.5, color: accent ? T.navy : context.pal.textSoft),
            ),
          ],
        ),
      ],
    ),
  );
}
