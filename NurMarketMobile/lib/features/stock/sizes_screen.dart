import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/shop_mode.dart';
import '../../core/util/ui.dart';
import '../sale/sale_screen.dart' show addProduct;
import '../sale/variant_picker.dart' show compareSizes;

/// 2026-10-31 (режим «Одежда»): «Размеры и цвета» — товары с размерами и остаток по каждому размеру и цвету (из базы телефона).
/// Размеры попадают в базу, когда товар открыли в продаже, отсканировали штрихкод размера или включён режим «Одежда» (фоновая загрузка
/// показанных товаров). Касание товара — в чек (откроется выбор размера).
class SizesScreen extends ConsumerStatefulWidget {
  const SizesScreen({super.key});
  @override
  ConsumerState<SizesScreen> createState() => _SizesScreenState();
}

class _SizesScreenState extends ConsumerState<SizesScreen> {
  final _q = TextEditingController();
  Timer? _debounce;
  List<Product> _products = const [];
  Map<String, List<VariantRow>> _variants = const {};
  bool _loaded = false;
  int _seq = 0;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _q.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    final seq = ++_seq;
    final db = ref.read(databaseProvider);
    final list = await db.searchProducts(_q.text, limit: 200, extra: (p) => productHasVariants());
    final rows = await db.variantsOfMany(list.map((x) => x.id));
    if (!mounted || seq != _seq) return;
    setState(() {
      _products = list;
      _variants = rows;
      _loaded = true;
    });
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final mode = ShopMode.clothing;
    return Scaffold(
      appBar: ScreenTitle(l.sizesScreenTitle),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
            child: TextField(
              controller: _q,
              onChanged: (_) {
                _debounce?.cancel();
                _debounce = Timer(const Duration(milliseconds: 300), _load);
              },
              decoration: InputDecoration(hintText: mode.searchHint(l), prefixIcon: const Icon(Icons.search_rounded)),
            ),
          ),
          Expanded(
            child: !_loaded
                ? const Center(child: CircularProgressIndicator())
                : _products.isEmpty
                ? Padding(
                    padding: const EdgeInsets.all(24),
                    child: Center(child: Text(l.sizesEmpty, key: const Key('sizesEmpty'), textAlign: TextAlign.center, style: TextStyle(color: pal.textSoft))),
                  )
                : ListView.separated(
                    padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
                    itemCount: _products.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 8),
                    itemBuilder: (c, i) => _row(c, _products[i]),
                  ),
          ),
        ],
      ),
    );
  }

  Widget _row(BuildContext context, Product p) {
    final l = context.l;
    final pal = context.pal;
    final rows = (_variants[p.id] ?? const <VariantRow>[]).where((v) => v.isActive).toList();
    final total = rows.fold<double>(0, (s, v) => s + (v.quantity > 0 ? v.quantity : 0));
    final colors = <String>[];
    for (final v in rows) {
      if (!colors.contains(v.color)) colors.add(v.color);
    }
    return Panel(
      key: Key('sizesRow_${p.id}'),
      onTap: () => addProduct(context, ref, p),
      padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(child: Text(p.name, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w700))),
              const SizedBox(width: 8),
              Text(l.sizesTotal(qtyText(total)), style: TextStyle(fontSize: 12, color: pal.textSoft)),
            ],
          ),
          const SizedBox(height: 6),
          for (final c in colors) ...[
            if (c.isNotEmpty) Padding(padding: const EdgeInsets.only(bottom: 2), child: Text(c, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.textSoft))),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                for (final v in (rows.where((x) => x.color == c).toList()..sort((a, b) => compareSizes(a.size, b.size))))
                  Container(
                    constraints: const BoxConstraints(minWidth: 52, minHeight: 32),
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 5),
                    alignment: Alignment.center,
                    decoration: BoxDecoration(
                      color: v.quantity > 0 ? ShopMode.clothing.accent.withValues(alpha: 0.12) : pal.window,
                      borderRadius: BorderRadius.circular(10),
                      border: Border.all(color: v.quantity > 0 ? ShopMode.clothing.accent.withValues(alpha: 0.5) : pal.border),
                    ),
                    child: Text('${v.size.isEmpty ? '—' : v.size} · ${qtyText(v.quantity)}', style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: v.quantity > 0 ? null : pal.textSoft)),
                  ),
              ],
            ),
            const SizedBox(height: 6),
          ],
        ],
      ),
    );
  }
}
