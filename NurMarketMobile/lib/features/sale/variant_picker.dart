import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../core/db/database.dart';
import 'catalog_repository.dart';

/// Выбранный в окне размер и сколько штук класть в чек.
class VariantPick {
  final ProductVariant variant;
  final double qty;
  const VariantPick(this.variant, this.qty);
}

/// Сколько штук размера ещё можно продать: остаток минус то, что уже лежит в чеке. Не меньше нуля.
double variantAvailable(ProductVariant v, double inCart) => (v.quantity - inCart).clamp(0, double.infinity).toDouble();

/// Порядок размеров: числа по возрастанию (44, 46, 48 …), буквенные XXS … 5XL по росту, остальное — по алфавиту после них.
int compareSizes(String a, String b) {
  const letters = ['xxs', 'xs', 's', 'm', 'l', 'xl', 'xxl', '2xl', 'xxxl', '3xl', '4xl', '5xl'];
  int rank(String s) {
    final t = s.trim().toLowerCase();
    if (double.tryParse(t.replaceAll(',', '.')) != null) return 0;
    final i = letters.indexOf(t);
    return i >= 0 ? 1 : 2;
  }

  final ra = rank(a), rb = rank(b);
  if (ra != rb) return ra.compareTo(rb);
  if (ra == 0) return double.parse(a.trim().replaceAll(',', '.')).compareTo(double.parse(b.trim().replaceAll(',', '.')));
  if (ra == 1) return letters.indexOf(a.trim().toLowerCase()).compareTo(letters.indexOf(b.trim().toLowerCase()));
  return a.toLowerCase().compareTo(b.toLowerCase());
}

/// 2026-10-31 (одежда): окно «Размер и цвет» — сетка по цветам: ячейка = размер + остаток. Размеры без остатка (и уже набранные в
/// чек до нуля) неактивны, отключённые варианты не показываются совсем. [inCart] — сколько каждого размера уже в чеке (id → шт.).
/// [refresh] — свежие остатки с сервера придут, пока окно открыто (показываем «Обновляем остатки…»).
Future<VariantPick?> pickVariant(
  BuildContext context,
  Product p,
  List<ProductVariant> variants, {
  Map<String, double> inCart = const {},
  Future<List<ProductVariant>?>? refresh,
  String? preselect,
}) {
  return showModalBottomSheet<VariantPick>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * 0.88),
    builder: (c) => VariantPickerSheet(product: p, variants: variants, inCart: inCart, refresh: refresh, preselect: preselect),
  );
}

class VariantPickerSheet extends StatefulWidget {
  final Product product;
  final List<ProductVariant> variants;
  final Map<String, double> inCart;
  final Future<List<ProductVariant>?>? refresh;
  final String? preselect;
  const VariantPickerSheet({super.key, required this.product, required this.variants, this.inCart = const {}, this.refresh, this.preselect});

  @override
  State<VariantPickerSheet> createState() => _VariantPickerSheetState();
}

class _VariantPickerSheetState extends State<VariantPickerSheet> {
  late List<ProductVariant> _all = widget.variants.where((v) => v.active).toList();
  String? _chosenId;
  double _qty = 1;
  bool _updating = false;
  bool _stale = false;

  @override
  void initState() {
    super.initState();
    _chosenId = widget.preselect;
    final r = widget.refresh;
    if (r != null) {
      _updating = true;
      r.then((fresh) {
        if (!mounted) return;
        setState(() {
          _updating = false;
          if (fresh == null) {
            _stale = true;
            return;
          }
          _all = fresh.where((v) => v.active).toList();
          // выбранный размер мог закончиться или исчезнуть, пока шло обновление
          final cur = _byId(_chosenId);
          if (cur == null || variantAvailable(cur, widget.inCart[cur.id] ?? 0) < 1) _chosenId = null;
          _qty = _qty.clamp(1, _chosenCap).toDouble();
        });
      });
    }
  }

  ProductVariant? _byId(String? id) => id == null ? null : _all.where((v) => v.id == id).firstOrNull;

  double get _chosenCap {
    final v = _byId(_chosenId);
    return v == null ? 1 : variantAvailable(v, widget.inCart[v.id] ?? 0).floorToDouble().clamp(1, double.infinity).toDouble();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final colors = <String>[];
    for (final v in _all) {
      if (!colors.contains(v.color)) colors.add(v.color);
    }
    final single = colors.length <= 1;
    final chosen = _byId(_chosenId);
    final anyAvailable = _all.any((v) => variantAvailable(v, widget.inCart[v.id] ?? 0) >= 1);
    final price = chosen?.price ?? widget.product.price;
    return SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(widget.product.name, key: const Key('variantSheetTitle'), maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
                const SizedBox(height: 2),
                Text(l.chooseSize, style: TextStyle(color: pal.textSoft)),
                if (_updating || _stale)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(_updating ? l.variantPickUpdating : l.variantPickStale, key: const Key('variantSheetNote'), style: TextStyle(fontSize: 12, color: _stale ? pal.warn : pal.textSoft)),
                  ),
              ],
            ),
          ),
          Flexible(
            child: SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(20, 4, 20, 8),
              child: !anyAvailable
                  ? Padding(
                      padding: const EdgeInsets.symmetric(vertical: 24),
                      child: Text(l.variantPickAllOut, key: const Key('variantAllOut'), textAlign: TextAlign.center, style: TextStyle(color: pal.danger, fontWeight: FontWeight.w700)),
                    )
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        for (final color in colors) ...[
                          if (!single || color.isNotEmpty) FieldLabel(color.isEmpty ? l.colorLabel : '${l.colorLabel}: $color'),
                          Wrap(
                            spacing: 8,
                            runSpacing: 8,
                            children: [
                              for (final v in (_all.where((x) => x.color == color).toList()..sort((a, b) => compareSizes(a.size, b.size)))) _cell(context, v),
                            ],
                          ),
                          const SizedBox(height: 10),
                        ],
                      ],
                    ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 4, 20, 8),
            child: Row(
              children: [
                _stepper(context, Icons.remove, chosen == null || _qty <= 1 ? null : () => setState(() => _qty -= 1), 'qtyMinus'),
                Container(
                  constraints: const BoxConstraints(minWidth: 48),
                  alignment: Alignment.center,
                  child: Text(qtyText(_qty), key: const Key('variantQty'), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                ),
                _stepper(context, Icons.add, chosen == null || _qty >= _chosenCap ? null : () => setState(() => _qty += 1), 'qtyPlus'),
                const SizedBox(width: 12),
                Expanded(
                  child: FilledButton(
                    key: const Key('variantAdd'),
                    style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                    onPressed: chosen == null ? null : () => Navigator.pop(context, VariantPick(chosen, _qty)),
                    child: Text(
                      chosen == null ? l.chooseSize : '${l.toCheckSize(chosen.lineLabel)} · ${money(price * _qty)} ${l.som}',
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      textAlign: TextAlign.center,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _stepper(BuildContext context, IconData icon, VoidCallback? onTap, String key) => Semantics(
    button: true,
    enabled: onTap != null,
    label: key == 'qtyPlus' ? '+' : '−',
    child: IconButton.filledTonal(key: Key(key), onPressed: onTap, icon: Icon(icon), style: IconButton.styleFrom(fixedSize: const Size(48, 48))),
  );

  Widget _cell(BuildContext context, ProductVariant v) {
    final l = context.l;
    final pal = context.pal;
    final inChk = widget.inCart[v.id] ?? 0;
    final avail = variantAvailable(v, inChk);
    final enabled = avail >= 1;
    final selected = v.id == _chosenId;
    final size = v.size.isEmpty ? '—' : v.size;
    final sub = enabled ? l.variantPickLeft(qtyText(avail)) : l.variantPickNone;
    return Semantics(
      button: true,
      enabled: enabled,
      selected: selected,
      label: '${v.lineLabel.isEmpty ? size : v.lineLabel}, $sub${inChk > 0 ? ', ${l.variantPickInCheck(qtyText(inChk))}' : ''}',
      excludeSemantics: true,
      child: Opacity(
        opacity: enabled ? 1 : 0.4,
        child: Material(
          color: selected ? T.accent : pal.panel,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12), side: BorderSide(color: selected ? T.accent : pal.border)),
          child: InkWell(
            key: Key('variant_${v.id}'),
            borderRadius: BorderRadius.circular(12),
            onTap: enabled
                ? () => setState(() {
                    _chosenId = v.id;
                    _qty = _qty.clamp(1, _chosenCap).toDouble();
                  })
                : null,
            child: ConstrainedBox(
              constraints: const BoxConstraints(minWidth: 64, minHeight: 56),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Text(size, style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: selected ? T.navy : null)),
                    Text(sub, style: TextStyle(fontSize: 11, color: selected ? T.navy : pal.textSoft)),
                    if (v.price != null && v.price != widget.product.price)
                      Text('${money(v.price!)} ${l.som}', style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w700, color: selected ? T.navy : pal.price)),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
