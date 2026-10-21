import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/util/access.dart';
import '../../core/api/session_store.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/admin_repository.dart';
import '../admin/admin_screens.dart';
import '../scan/scan_animations.dart';
import '../scan/weigh_camera.dart';
import 'cart.dart';
import 'catalog_repository.dart';
import '../../core/util/audit.dart';

/// Продажа (Т2): поиск, избранное и категории, плитки, чек; смахнуть строку влево — убрать.
class SaleScreen extends ConsumerStatefulWidget {
  const SaleScreen({super.key});
  @override
  ConsumerState<SaleScreen> createState() => _SaleScreenState();
}

class _SaleScreenState extends ConsumerState<SaleScreen> {
  final _search = TextEditingController();
  String? _category; // null — всё, '' — избранное
  List<Product> _products = [];
  List<String> _categories = [];
  Timer? _debounce;

  /// 13.10, владелец: «в чеке отдельное таб-окно» — «Товары» и «Чек» отдельными вкладками.
  bool _showCheck = false;

  @override
  void dispose() {
    _debounce?.cancel();
    super.dispose();
  }

  @override
  void initState() {
    super.initState();
    _reload();
  }

  Future<void> _reload() async {
    final db = ref.read(databaseProvider);
    final cats = await db.categories();
    final list = await db.searchProducts(_search.text, category: _category == '' ? null : _category, favorites: _category == '', limit: 5000);
    if (mounted) {
      setState(() {
        _categories = cats;
        _products = list;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final cart = ref.watch(cartProvider);
    final p = context.pal;
    return Column(
      children: [
        ScreenTitle(l.newCheck, back: false),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
          child: SegmentedButton<bool>(
            showSelectedIcon: false,
            segments: [
              ButtonSegment(value: false, icon: const Icon(Icons.grid_view_rounded, size: 18), label: Text(l.productsTitle)),
              ButtonSegment(value: true, icon: const Icon(Icons.receipt_long_rounded, size: 18), label: Text(l.checkTab(cart.count))),
            ],
            selected: {_showCheck},
            onSelectionChanged: (v) => setState(() => _showCheck = v.first),
          ),
        ),
        if (!_showCheck) ...[
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _search,
                    // поиск после паузы в наборе, а не на каждую букву (на слабых телефонах ввод подтормаживал)
                    onChanged: (_) {
                      _debounce?.cancel();
                      _debounce = Timer(const Duration(milliseconds: 250), _reload);
                    },
                    decoration: InputDecoration(hintText: l.searchProduct, prefixIcon: const Icon(Icons.search_rounded)),
                  ),
                ),
                const SizedBox(width: 8),
                Material(
                  color: T.navy,
                  borderRadius: BorderRadius.circular(12),
                  child: InkWell(
                    borderRadius: BorderRadius.circular(12),
                    onTap: () => context.push('/scan?mode=sale'),
                    child: const SizedBox(width: 48, height: 48, child: Icon(Icons.qr_code_scanner_rounded, color: T.accent)),
                  ),
                ),
                // 2026-10-20, владелец: весовой товар умной камерой — товар и вес с табло весов сразу в чек.
                const SizedBox(width: 8),
                Material(
                  color: T.navy,
                  borderRadius: BorderRadius.circular(12),
                  child: InkWell(
                    borderRadius: BorderRadius.circular(12),
                    onTap: () => weighForSale(context, ref),
                    child: Tooltip(
                      message: l.weighTitle,
                      child: const SizedBox(width: 48, height: 48, child: Icon(Icons.scale_rounded, color: T.accent)),
                    ),
                  ),
                ),
              ],
            ),
          ),
          SizedBox(
            height: 48,
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
              children: [
                for (final (value, label) in [(null, l.all), ('', l.favorites), ..._categories.map((c) => (c, c))])
                  Padding(
                    padding: const EdgeInsets.only(right: 6),
                    child: ChoiceChip(
                      label: Text(label),
                      selected: _category == value,
                      labelStyle: TextStyle(color: _category == value ? Colors.white : p.text, fontWeight: FontWeight.w600, fontSize: 13),
                      onSelected: (_) {
                        setState(() => _category = value);
                        _reload();
                      },
                    ),
                  ),
              ],
            ),
          ),
        ],
        Expanded(
          child: CustomScrollView(
            slivers: [
              if (!_showCheck)
                SliverPadding(
                  padding: const EdgeInsets.fromLTRB(16, 6, 16, 8),
                  sliver: _products.isEmpty
                      ? SliverToBoxAdapter(
                          child: Padding(
                            padding: const EdgeInsets.all(24),
                            child: Center(
                              child: Text(l.nothingFound, style: TextStyle(color: p.textSoft)),
                            ),
                          ),
                        )
                      : SliverGrid.builder(
                          gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
                            maxCrossAxisExtent: 140,
                            mainAxisSpacing: 8,
                            crossAxisSpacing: 8,
                            childAspectRatio: .82,
                          ),
                          itemCount: _products.length,
                          itemBuilder: (c, i) => _ProductTile(_products[i], onTap: () => addProduct(context, ref, _products[i])),
                        ),
                ),
              if (_showCheck) ...[
                SliverToBoxAdapter(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(16, 8, 16, 6),
                    child: Row(
                      children: [
                        Text(l.inCheck, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
                        const SizedBox(width: 8),
                        Text(l.positions(cart.count), style: TextStyle(color: p.textSoft, fontSize: 12.5)),
                        const Spacer(),
                        // 13.10, владелец: «продажа оптом и в розницу» — как переключатель «Розница / Опт» на кассе.
                        SegmentedButton<bool>(
                          showSelectedIcon: false,
                          style: const ButtonStyle(visualDensity: VisualDensity.compact, tapTargetSize: MaterialTapTargetSize.shrinkWrap),
                          segments: [
                            ButtonSegment(value: false, label: Text(l.retail)),
                            ButtonSegment(value: true, label: Text(l.wholesale)),
                          ],
                          selected: {ref.read(cartProvider.notifier).wholesaleMode},
                          onSelectionChanged: (v) {
                            final ctrl = ref.read(cartProvider.notifier);
                            ctrl.setWholesaleMode(v.first);
                            if (v.first) {
                              final any = cart.lines.any((x) => x.canWholesale);
                              toast(context, cart.lines.isEmpty || any ? l.wholesaleOn : l.wholesaleNone, error: !(cart.lines.isEmpty || any));
                            }
                          },
                        ),
                      ],
                    ),
                  ),
                ),
                SliverList.builder(
                  itemCount: cart.lines.length,
                  itemBuilder: (c, i) => _CartRow(index: i),
                ),
                if (cart.lines.isNotEmpty)
                  SliverToBoxAdapter(
                    child: Padding(
                      padding: const EdgeInsets.symmetric(vertical: 8),
                      child: Center(
                        child: Text(l.swipeToRemove, style: TextStyle(fontSize: 12, color: p.textSoft)),
                      ),
                    ),
                  ),
                if (cart.orderDiscount > 0)
                  SliverToBoxAdapter(
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                      child: Row(
                        children: [
                          Text(l.orderDiscount, style: TextStyle(color: p.textSoft)),
                          const Spacer(),
                          Text(
                            '−${money(cart.orderDiscount)} ${l.som}',
                            style: const TextStyle(color: T.success, fontWeight: FontWeight.w700),
                          ),
                        ],
                      ),
                    ),
                  ),
                if (cart.lines.isEmpty)
                  SliverToBoxAdapter(
                    child: Padding(
                      padding: const EdgeInsets.all(32),
                      child: Center(
                        child: Text(l.positions(0), style: TextStyle(color: p.textSoft)),
                      ),
                    ),
                  ),
              ],
            ],
          ),
        ),
        Container(
          padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
          decoration: BoxDecoration(
            color: p.window,
            border: Border(top: BorderSide(color: p.border)),
          ),
          child: Row(
            children: [
              Material(
                color: p.panel,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(14),
                  side: BorderSide(color: p.border),
                ),
                child: PopupMenuButton<String>(
                  icon: const Icon(Icons.more_horiz_rounded),
                  onSelected: (v) => _menu(context, v),
                  itemBuilder: (c) => [
                    PopupMenuItem(value: 'discount', child: Text(l.orderDiscount)),
                    PopupMenuItem(value: 'custom', child: Text(l.extraService)),
                    PopupMenuItem(value: 'hold', enabled: !cart.isEmpty, child: Text(l.holdCheck)),
                    PopupMenuItem(value: 'held', child: Text(l.heldChecks)),
                    PopupMenuItem(value: 'clear', enabled: !cart.isEmpty, child: Text(l.clearCheck)),
                  ],
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: FilledButton(
                  onPressed: cart.isEmpty ? null : () => context.push('/pay'),
                  child: AnimatedSwitcher(
                    duration: const Duration(milliseconds: 220),
                    transitionBuilder: (c, a) => ScaleTransition(
                      scale: Tween(begin: .8, end: 1.0).animate(CurvedAnimation(parent: a, curve: Curves.easeOutBack)),
                      child: FadeTransition(opacity: a, child: c),
                    ),
                    child: Text(l.payAmount(money(cart.total)), key: ValueKey(cart.total)),
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  Future<void> _menu(BuildContext context, String v) async {
    final l = context.l;
    final cart = ref.read(cartProvider.notifier);
    switch (v) {
      case 'discount':
        if (!checkAccess(context, ref, Session.discount)) return;
        // 13.10, владелец: «скидку в соме тоже добавь, а не только процент».
        final state = ref.read(cartProvider);
        final base = state.subtotal - state.lineDiscounts;
        final d = await askDiscount(context, title: l.orderDiscount, base: base, percent: state.orderDiscountPercent, sum: state.orderDiscountSum);
        if (d != null) {
          cart.setOrderDiscount(percent: d.percent, sum: d.sum);
          Audit.log(
            'discount',
            '${l.orderDiscount}: ${d.percent > 0 ? '${qtyText(d.percent)} %' : '${money(d.sum)} ${l.som}'}',
            amount: ref.read(cartProvider).orderDiscount,
          );
        }
      case 'custom':
        final price = await askNumber(context, title: l.extraService, hint: l.price);
        if (price != null && price > 0 && context.mounted) cart.addCustom(l.extraService, price, 1);
      case 'hold':
        // 13.10: «Отложить чек» — как в кассе: чек уходит в отложенные на телефоне, касса свободна для следующего покупателя.
        final note = TextEditingController();
        final ok = await showDialog<bool>(
          context: context,
          builder: (c) => AlertDialog(
            title: Text(l.holdCheck),
            content: TextField(
              controller: note,
              autofocus: true,
              decoration: InputDecoration(hintText: l.holdNote),
            ),
            actions: [
              TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
              FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.holdCheck)),
            ],
          ),
        );
        if (ok != true) return;
        await ref.read(opsRepoProvider).hold(ref.read(cartProvider), note.text.trim());
        cart.clear();
        if (context.mounted) toast(context, l.holdCheck);
      case 'held':
        if (context.mounted) context.push('/held');
      case 'clear':
        cart.clear();
    }
  }
}

/// Добавить товар в чек: весовой — с весом; без открытой смены — сначала открыть смену.
/// Скидка процентом или суммой в сомах (13.10): быстрые 5 / 10 / 15 %, своя — % или сом, итог со скидкой сразу.
/// Возвращает (percent, sum) — заполнено одно из двух; (0, 0) — скидку убрали; null — отмена.
Future<({double percent, double sum})?> askDiscount(BuildContext context, {required String title, required double base, double percent = 0, double sum = 0}) {
  final l = context.l;
  var inSom = percent <= 0 && sum > 0;
  final ctrl = TextEditingController(text: inSom ? qtyText(sum) : (percent > 0 ? qtyText(percent) : ''));
  return showModalBottomSheet<({double percent, double sum})>(
    context: context,
    isScrollControlled: true,
    builder: (c) => StatefulBuilder(
      builder: (c, set) {
        final v = parseNum(ctrl.text) ?? 0;
        final off = inSom ? v.clamp(0, base).toDouble() : roundMoney(base * v.clamp(0, 100) / 100);
        return Padding(
          padding: EdgeInsets.fromLTRB(20, 20, 20, MediaQuery.viewInsetsOf(c).bottom + 20),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                const SizedBox(height: 12),
                Row(
                  children: [
                    for (final pct in [5, 10, 15]) ...[
                      Expanded(
                        child: OutlinedButton(onPressed: () => Navigator.pop(c, (percent: pct.toDouble(), sum: 0.0)), child: Text('$pct %')),
                      ),
                      const SizedBox(width: 8),
                    ],
                  ],
                ),
                const SizedBox(height: 12),
                SegmentedButton<bool>(
                  showSelectedIcon: false,
                  segments: [
                    ButtonSegment(value: false, label: Text(l.discountInPercent)),
                    ButtonSegment(value: true, label: Text(l.discountInSom)),
                  ],
                  selected: {inSom},
                  onSelectionChanged: (x) => set(() => inSom = x.first),
                ),
                const SizedBox(height: 10),
                TextField(
                  controller: ctrl,
                  autofocus: true,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800),
                  decoration: InputDecoration(suffixText: inSom ? l.som : '%'),
                  onChanged: (_) => set(() {}),
                ),
                const SizedBox(height: 8),
                Text(l.discountResult(money((base - off).clamp(0, double.infinity).toDouble())), style: const TextStyle(fontWeight: FontWeight.w700)),
                const SizedBox(height: 14),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(onPressed: () => Navigator.pop(c, (percent: 0.0, sum: 0.0)), child: Text(l.discountRemove)),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: FilledButton(
                        onPressed: () =>
                            Navigator.pop(c, inSom ? (percent: 0.0, sum: v.clamp(0, base).toDouble()) : (percent: v.clamp(0, 100).toDouble(), sum: 0.0)),
                        child: Text(l.done),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        );
      },
    ),
  );
}

Future<void> addProduct(BuildContext context, WidgetRef ref, Product p, {double? qty}) async {
  final l = context.l;
  if (!ref.read(shiftProvider).isOpen) {
    toast(context, l.openShiftFirst, error: true);
    return;
  }
  var q = qty ?? 1;
  if (p.isWeight && qty == null) {
    final kg = await askNumber(context, title: l.weightOf(p.name), hint: l.kg);
    if (kg == null || kg <= 0) return;
    q = kg;
  }
  // 13.10, макет С5: одежда — у товара с размерами сначала выбирается размер и цвет (как VariantPickerWindow кассы).
  if (!p.isWeight && ref.read(prefsProvider).clothing) {
    List<ProductVariant> variants = const [];
    try {
      variants = (await ref.read(catalogRepoProvider).variants(p.id)).where((v) => v.active).toList();
    } catch (_) {
      // без связи — продаём без размера
    }
    if (variants.isNotEmpty && context.mounted) {
      final v = await pickVariant(context, p, variants);
      if (v == null) return;
      ref.read(cartProvider.notifier).add(p, qty: q, variantId: v.id, variantLabel: v.label, variantPrice: v.price);
      return;
    }
  }
  ref.read(cartProvider.notifier).add(p, qty: q);
}

/// С5: цвет чипами, размеры с остатком; «В чек · 44». Мало осталось — переспросить.
Future<ProductVariant?> pickVariant(BuildContext context, Product p, List<ProductVariant> variants) {
  final l = context.l;
  final colors = variants.map((v) => v.color).toSet().toList();
  var color = colors.first;
  ProductVariant? chosen;
  return showModalBottomSheet<ProductVariant>(
    context: context,
    isScrollControlled: true,
    builder: (c) => StatefulBuilder(
      builder: (c, set) {
        final sizes = variants.where((v) => v.color == color).toList();
        return SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(p.name, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
                Text(l.chooseSize, style: TextStyle(color: c.pal.textSoft)),
                if (colors.length > 1 || colors.first.isNotEmpty) ...[
                  FieldLabel(l.colorLabel),
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      for (final col in colors)
                        ChoiceChip(
                          label: Text(col.isEmpty ? '—' : col),
                          selected: col == color,
                          onSelected: (_) => set(() {
                            color = col;
                            chosen = null;
                          }),
                        ),
                    ],
                  ),
                ],
                FieldLabel(l.sizeLabel),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final v in sizes)
                      ChoiceChip(
                        label: Text('${v.size.isEmpty ? '—' : v.size} · ${qtyText(v.quantity)}'),
                        selected: chosen?.id == v.id,
                        onSelected: (_) => set(() => chosen = v),
                      ),
                  ],
                ),
                const SizedBox(height: 16),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: chosen == null
                      ? null
                      : () async {
                          final v = chosen!;
                          if (v.quantity < 1) {
                            final ok = await showDialog<bool>(
                              context: c,
                              builder: (d) => AlertDialog(
                                content: Text(l.sizeLowStock(qtyText(v.quantity))),
                                actions: [
                                  TextButton(onPressed: () => Navigator.pop(d, false), child: Text(l.cancel)),
                                  FilledButton(onPressed: () => Navigator.pop(d, true), child: Text(l.done)),
                                ],
                              ),
                            );
                            if (ok != true) return;
                          }
                          if (c.mounted) Navigator.pop(c, v);
                        },
                  child: Text(chosen == null ? l.chooseSize : l.toCheckSize(chosen!.label)),
                ),
              ],
            ),
          ),
        );
      },
    ),
  );
}

class _ProductTile extends StatelessWidget {
  final Product product;
  final VoidCallback onTap;
  const _ProductTile(this.product, {required this.onTap});
  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    return Pressable(
      onTap: onTap,
      child: Panel(
        padding: const EdgeInsets.all(6),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Expanded(
              child: ProductThumb(name: product.name, url: product.imageUrl, fontSize: 18),
            ),
            const SizedBox(height: 4),
            Text(
              product.name,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              textAlign: TextAlign.center,
              style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, height: 1.15),
            ),
            Text(
              '${money(product.price)} ${context.l.som}',
              textAlign: TextAlign.center,
              style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w800, color: p.price),
            ),
          ],
        ),
      ),
    );
  }
}

/// 13.10, владелец: «сделай удобной корзину в мобилке». Строка чека — карточка: крупные «−»/«+» (44 точки), нажатие на
/// количество — ввод числа (и веса), видимые «%» (скидка строки) и корзина (удалить с причиной); «−» на последней штуке
/// предлагает убрать строку. Свайп влево по-прежнему удаляет.
Future<bool> _removeLine(BuildContext context, WidgetRef ref, int index) async {
  if (!checkAccess(context, ref, Session.deleteLine)) return false;
  final cart = ref.read(cartProvider);
  if (index >= cart.lines.length) return false;
  final line = cart.lines[index];
  // 13.10, макет У3: причина удаления строки — в журнал «Удалено из чека» (видит администратор).
  final reason = await askDeleteReason(context);
  if (reason == null) return false;
  final s = ref.read(sessionProvider);
  await ref
      .read(adminRepoProvider)
      .logDeletion(DeletionRow(DateTime.now(), line.name, line.qty, line.qty * line.price - line.discount, reason, s?.userName ?? ''));
  Audit.log('delete', '${line.name} × ${qtyText(line.qty)} · $reason', amount: line.qty * line.price - line.discount);
  return true;
}

class _CartRow extends ConsumerWidget {
  final int index;
  const _CartRow({required this.index});
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final cart = ref.watch(cartProvider);
    if (index >= cart.lines.length) return const SizedBox.shrink();
    final line = cart.lines[index];
    final ctrl = ref.read(cartProvider.notifier);
    final p = context.pal;
    final l = context.l;

    Future<void> editQty() async {
      final v = await askNumber(context, title: line.name, hint: line.isWeight ? line.unit : l.packQty, initial: line.qty);
      if (v == null) return;
      // 14.10, проверка логики: штучный товар — только целое (сервер принимает штуки целыми; 1,5 уходило как 2 и сумма
      // на телефоне расходилась с NurCRM).
      final q = line.isWeight ? v : v.roundToDouble();
      if (q <= 0) {
        if (context.mounted && await _removeLine(context, ref, index)) ctrl.remove(index);
      } else {
        ctrl.setQty(index, q);
      }
    }

    Future<void> discount() async {
      // 18.10: у товара с акцией скидку считает сервер — ручная не применится.
      if (line.hasPromo) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(l.promoNoManualDiscount)));
        return;
      }
      if (!checkAccess(context, ref, Session.discount)) return;
      final d = await askDiscount(context, title: l.lineDiscount, base: line.gross, sum: line.discount);
      if (d != null) {
        ctrl.setLineDiscount(index, d.percent > 0 ? roundMoney(line.gross * d.percent / 100) : d.sum);
        Audit.log('discount', '${l.lineDiscount}: ${line.name}', amount: line.discount);
      }
    }

    return _Appear(
      key: ValueKey(identityHashCode(line)),
      child: Dismissible(
        key: ValueKey(identityHashCode(line)),
        direction: DismissDirection.endToStart,
        background: Container(
          margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
          decoration: BoxDecoration(color: T.danger, borderRadius: BorderRadius.circular(14)),
          alignment: Alignment.centerRight,
          padding: const EdgeInsets.only(right: 20),
          child: const Icon(Icons.delete_outline, color: Colors.white),
        ),
        confirmDismiss: (_) => _removeLine(context, ref, index),
        onDismissed: (_) => ctrl.remove(index),
        child: Container(
          margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
          padding: const EdgeInsets.fromLTRB(12, 10, 8, 10),
          decoration: BoxDecoration(
            color: p.panel,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: p.border),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          line.name,
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14.5),
                        ),
                        const SizedBox(height: 2),
                        Wrap(
                          spacing: 6,
                          crossAxisAlignment: WrapCrossAlignment.center,
                          children: [
                            Text('${money(line.price)} ${l.som}${line.isWeight ? ' / ${line.unit}' : ''}', style: TextStyle(fontSize: 12.5, color: p.textSoft)),
                            // строка ↔ опт (только если у товара есть оптовая цена)
                            if (line.canWholesale)
                              InkWell(
                                onTap: () => ctrl.toggleWholesale(index),
                                borderRadius: BorderRadius.circular(8),
                                child: Container(
                                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                                  decoration: BoxDecoration(
                                    color: line.wholesale ? T.navy : Colors.transparent,
                                    border: Border.all(color: line.wholesale ? T.navy : p.border),
                                    borderRadius: BorderRadius.circular(8),
                                  ),
                                  child: Text(
                                    l.wholesaleTag,
                                    style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: line.wholesale ? Colors.white : p.textSoft),
                                  ),
                                ),
                              ),
                            if (line.hasPromo)
                              Container(
                                padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                                decoration: BoxDecoration(color: T.success.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(8)),
                                child: Text(l.promoTag, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: T.success)),
                              ),
                            if (line.discount > 0)
                              Text(
                                '${l.discount} −${money(line.discount)}',
                                style: const TextStyle(fontSize: 12.5, color: T.success, fontWeight: FontWeight.w700),
                              ),
                          ],
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  Text(
                    '${money(line.total)} ${l.som}',
                    style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: p.price),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              Row(
                children: [
                  _Step(
                    icon: line.qty <= 1 && !line.isWeight ? Icons.delete_outline_rounded : Icons.remove,
                    onTap: () async {
                      if (line.isWeight) {
                        await editQty();
                      } else if (line.qty <= 1) {
                        if (await _removeLine(context, ref, index)) ctrl.remove(index);
                      } else {
                        ctrl.setQty(index, line.qty - 1);
                      }
                    },
                  ),
                  InkWell(
                    onTap: editQty,
                    borderRadius: BorderRadius.circular(10),
                    child: Container(
                      constraints: const BoxConstraints(minWidth: 64),
                      height: 44,
                      margin: const EdgeInsets.symmetric(horizontal: 6),
                      padding: const EdgeInsets.symmetric(horizontal: 10),
                      alignment: Alignment.center,
                      decoration: BoxDecoration(color: p.window, borderRadius: BorderRadius.circular(10)),
                      child: AnimatedSwitcher(
                        duration: const Duration(milliseconds: 180),
                        transitionBuilder: (c, a) => ScaleTransition(scale: a, child: c),
                        child: Text(
                          '${qtyText(line.qty)}${line.isWeight ? ' ${line.unit}' : ''}',
                          key: ValueKey(line.qty),
                          style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16),
                        ),
                      ),
                    ),
                  ),
                  _Step(icon: Icons.add, dark: true, onTap: () => line.isWeight ? editQty() : ctrl.setQty(index, line.qty + 1)),
                  const Spacer(),
                  IconButton(tooltip: l.lineDiscount, onPressed: discount, icon: const Icon(Icons.percent_rounded)),
                  IconButton(
                    tooltip: l.deleteProduct,
                    color: T.danger,
                    onPressed: () async {
                      if (await _removeLine(context, ref, index)) ctrl.remove(index);
                    },
                    icon: const Icon(Icons.delete_outline_rounded),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _Step extends StatelessWidget {
  final IconData icon;
  final VoidCallback onTap;
  final bool dark;
  const _Step({required this.icon, required this.onTap, this.dark = false});
  @override
  Widget build(BuildContext context) => Material(
    color: dark ? context.pal.action : context.pal.panel,
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(12),
      side: BorderSide(color: dark ? context.pal.action : context.pal.border),
    ),
    child: InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(12),
      child: SizedBox(width: 44, height: 44, child: Icon(icon, size: 20, color: dark ? context.pal.onAction : null)),
    ),
  );
}

/// Новая строка чека выезжает справа и проявляется (один раз — при появлении).
class _Appear extends StatelessWidget {
  final Widget child;
  const _Appear({super.key, required this.child});
  @override
  Widget build(BuildContext context) => TweenAnimationBuilder<double>(
    tween: Tween(begin: 0, end: 1),
    duration: const Duration(milliseconds: 320),
    curve: Curves.easeOutCubic,
    builder: (c, t, child) => Opacity(
      opacity: t,
      child: Transform.translate(offset: Offset(40 * (1 - t), 0), child: child),
    ),
    child: child,
  );
}
