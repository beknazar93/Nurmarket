import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/shop_mode.dart';
import '../../core/util/ui.dart';
import '../sale/variant_picker.dart' show compareSizes;

/// 2026-10-31 (владелец: «режимы должны отличаться в интерфейсе»): блок в карточке товара под режим магазина.
/// • «Одежда» — размеры и цвета товара с остатками (из базы телефона, при необходимости подтягиваются с сервера);
/// • «Продукты» — весовой товар (цена за кг, код весов PLU) и срок годности;
/// • «Хозтовары» — единица измерения (м, м², упаковка …).
/// «Услуги» и режим «не выбран» — блока нет.
class ModeProductInfo extends ConsumerStatefulWidget {
  final Product product;
  const ModeProductInfo({super.key, required this.product});
  @override
  ConsumerState<ModeProductInfo> createState() => _ModeProductInfoState();
}

class _ModeProductInfoState extends ConsumerState<ModeProductInfo> {
  List<VariantRow>? _variants;
  bool _started = false;

  Future<void> _loadVariants() async {
    _started = true;
    final prefs = ref.read(prefsProvider);
    if (!prefs.shopChosen || prefs.shop != ShopMode.clothing || widget.product.isService || widget.product.isWeight) return;
    final db = ref.read(databaseProvider);
    final cached = await db.variantsOf(widget.product.id);
    if (mounted) setState(() => _variants = cached);
    final set = await ref.read(variantServiceProvider).forProduct(widget.product.id);
    if (set.refresh != null) await set.refresh;
    final fresh = await db.variantsOf(widget.product.id);
    if (mounted) setState(() => _variants = fresh);
  }

  @override
  Widget build(BuildContext context) {
    final prefs = ref.watch(prefsProvider);
    if (!prefs.shopChosen || widget.product.isService) return const SizedBox.shrink();
    final mode = prefs.shop;
    // режим «Одежда» включили (или он пришёл с сервера) уже после открытия карточки — размеры берутся сразу
    if (mode == ShopMode.clothing && !_started) Future.microtask(_loadVariants);
    final l = context.l;
    final pal = context.pal;
    final p = widget.product;
    String? title;
    Widget? body;
    switch (mode) {
      case ShopMode.clothing:
        title = l.cardSizesTitle;
        final rows = (_variants ?? const <VariantRow>[]).where((v) => v.isActive).toList();
        if (rows.isEmpty) {
          body = Text(l.cardSizesNone, style: TextStyle(fontSize: 12.5, color: pal.textSoft));
        } else {
          final colors = <String>[];
          for (final v in rows) {
            if (!colors.contains(v.color)) colors.add(v.color);
          }
          body = Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              for (final c in colors) ...[
                if (c.isNotEmpty) Padding(padding: const EdgeInsets.only(top: 4, bottom: 2), child: Text(c, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 12.5))),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final v in (rows.where((x) => x.color == c).toList()..sort((a, b) => compareSizes(a.size, b.size))))
                      Container(
                        constraints: const BoxConstraints(minWidth: 56, minHeight: 36),
                        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                        alignment: Alignment.center,
                        decoration: BoxDecoration(
                          color: v.quantity > 0 ? mode.accent.withValues(alpha: 0.12) : pal.window,
                          borderRadius: BorderRadius.circular(10),
                          border: Border.all(color: v.quantity > 0 ? mode.accent.withValues(alpha: 0.5) : pal.border),
                        ),
                        child: Text('${v.size.isEmpty ? '—' : v.size} · ${qtyText(v.quantity)}', style: TextStyle(fontWeight: FontWeight.w700, fontSize: 12.5, color: v.quantity > 0 ? null : pal.textSoft)),
                      ),
                  ],
                ),
              ],
            ],
          );
        }
      case ShopMode.grocery:
        title = p.isWeight ? l.cardWeightTitle : l.cardExpiryTitle;
        final exp = p.expiresOn;
        final soon = daysToExpiry(exp) != null && daysToExpiry(exp)! <= 7;
        body = Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (p.isWeight) Text(l.cardWeightInfo(p.unit, (p.plu ?? '').isEmpty ? l.cardNoPlu : p.plu!), style: const TextStyle(fontSize: 13)),
            if (p.isWeight) const SizedBox(height: 6),
            Text(l.cardExpiryTitle, style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
            Text(
              exp == null ? l.cardExpiryNone : expiryText(exp),
              key: const Key('cardExpiry'),
              style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: exp == null ? pal.textSoft : (soon ? pal.danger : null)),
            ),
          ],
        );
      case ShopMode.hardware:
        title = l.cardUnitTitle;
        body = Text(l.cardUnitInfo(p.unit), style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700));
      case ShopMode.services:
        return const SizedBox.shrink();
    }
    return Container(
      key: const Key('modeProductInfo'),
      margin: const EdgeInsets.only(top: 8, bottom: 4),
      padding: const EdgeInsets.fromLTRB(12, 10, 12, 12),
      decoration: BoxDecoration(color: mode.accent.withValues(alpha: 0.07), borderRadius: BorderRadius.circular(14), border: Border.all(color: mode.accent.withValues(alpha: 0.4))),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(mode.icon, size: 18, color: mode.accent),
              const SizedBox(width: 8),
              Expanded(child: Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14))),
            ],
          ),
          const SizedBox(height: 6),
          body,
        ],
      ),
    );
  }
}
