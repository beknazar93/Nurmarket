import 'dart:async';

import 'package:drift/drift.dart' hide Column;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/scan/code_lookup.dart';
import '../db/database.dart';
import '../providers.dart';
import '../theme/tokens.dart';
import 'ui.dart';

/// 14.10, владелец: «в КП не выходит список товаров, добавь туда фильтр — и фильтр везде, где есть товары».
/// Остаток: все / в наличии / заканчиваются (≤ 3) / нет в наличии.
enum StockFilter { all, inStock, low, out }

/// 2026-10-22 (мобилка, админ, П11): порог «заканчивается» как на ПК (CatalogProductTileVm.LowStockQuantityThreshold = 10,
/// остаток меньше 10). Было ≤ 3 — телефон и ПК показывали разное. Минимум конкретного товара (minimum_quantity) в базе
/// телефона не хранится — для него нужна новая колонка (смена схемы, только с согласия владельца).
const lowStockLimit = 10.0;

/// 2026-10-22 (мобилка, админ): «заканчивается» с учётом минимума товара. Минимум не задан (0) — общий порог < 10;
/// задан — остаток дошёл до минимума или ниже.
bool isLowStock(double quantity, double minQuantity) => minQuantity > 0 ? quantity <= minQuantity : quantity < lowStockLimit;

/// То же условием для запроса к базе телефона.
Expression<bool> lowStockSql($ProductsTable p) =>
    (p.minQuantity.isSmallerOrEqualValue(0) & p.quantity.isSmallerThanValue(lowStockLimit)) |
    (p.minQuantity.isBiggerThanValue(0) & p.quantity.isSmallerOrEqual(p.minQuantity));

/// 2026-10-30 (оптимизация, п. 4): сколько товаров заканчивается — одним SELECT COUNT(*), без чтения полных строк (раньше
/// на главной и в «Управлении» читалось до 15 000 строк ради `.length`).
Future<int> countLowStock(AppDatabase db) async {
  final c = db.products.id.count();
  final q = db.selectOnly(db.products)
    ..addColumns([c])
    ..where(lowStockSql(db.products) & db.products.isService.equals(false));
  return (await q.getSingle()).read(c) ?? 0;
}

/// 2026-10-31 («в мобилке не отображаются все товары»): тот же фильтр остатка условием для запроса. Раньше фильтр применялся уже к
/// первым найденным 300–500 товарам: «Нет в наличии» на 15 000 товаров искал только среди первых 500 по алфавиту.
Expression<bool> stockFilterSql($ProductsTable p, StockFilter f) => switch (f) {
  StockFilter.all => const Constant(true),
  StockFilter.inStock => p.isService.equals(true) | p.quantity.isBiggerThanValue(0),
  StockFilter.low => p.isService.equals(false) & p.quantity.isBiggerThanValue(0) & lowStockSql(p),
  StockFilter.out => p.isService.equals(false) & p.quantity.isSmallerOrEqualValue(0),
};

bool stockFilterMatch(Product p, StockFilter f) => switch (f) {
  StockFilter.all => true,
  StockFilter.inStock => p.isService || p.quantity > 0,
  StockFilter.low => !p.isService && p.quantity > 0 && isLowStock(p.quantity, p.minQuantity),
  StockFilter.out => !p.isService && p.quantity <= 0,
};

/// Полоса фильтров товаров: остаток и категории — для списков товаров на любом экране.
class ProductFilterBar extends ConsumerStatefulWidget {
  final StockFilter stock;
  final String? category;
  final void Function(StockFilter stock, String? category) onChanged;

  /// 2026-10-26 (живой аудит, П7): отступ внутри прокрутки — крайние чипы не обрезаются по краю отступа экрана.
  final EdgeInsets padding;
  const ProductFilterBar({super.key, required this.stock, required this.category, required this.onChanged, this.padding = EdgeInsets.zero});
  @override
  ConsumerState<ProductFilterBar> createState() => _ProductFilterBarState();
}

class _ProductFilterBarState extends ConsumerState<ProductFilterBar> {
  List<String> _categories = [];

  @override
  void initState() {
    super.initState();
    ref.read(databaseProvider).categories().then((c) => mounted ? setState(() => _categories = c) : null);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    Widget chip(String label, bool on, VoidCallback tap) => Padding(
      padding: const EdgeInsets.only(right: 6),
      child: ChoiceChip(label: Text(label), selected: on, onSelected: (_) => tap()),
    );
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        // 2026-10-26 (живой аудит, П7): 44 dp обрезали чип (область касания 48 dp, шрифт 1,3×) — теперь 52.
        SizedBox(
          height: 52,
          child: ListView(
            scrollDirection: Axis.horizontal,
            padding: widget.padding,
            children: [
              for (final (f, label) in [
                (StockFilter.all, l.filterAll),
                (StockFilter.inStock, l.inStockOnly),
                (StockFilter.low, l.runningOutShort),
                (StockFilter.out, l.outOfStock),
              ])
                chip(label, widget.stock == f, () => widget.onChanged(f, widget.category)),
            ],
          ),
        ),
        if (_categories.isNotEmpty)
          SizedBox(
            height: 52,
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: widget.padding,
              children: [
                chip(l.allCategories, widget.category == null, () => widget.onChanged(widget.stock, null)),
                for (final c in _categories) chip(c, widget.category == c, () => widget.onChanged(widget.stock, c)),
              ],
            ),
          ),
      ],
    );
  }
}

/// Выбор товара: список виден сразу (без ввода), поиск по названию/штрихкоду, фильтры остатка и категории.
/// [subtitle] — вторая строка (по умолчанию цена и остаток); [noServices] — без услуг (приёмка, заказ поставщику).
Future<Product?> pickProduct(BuildContext context, {String Function(Product p)? subtitle, bool noServices = false, String initialQuery = ''}) =>
    showModalBottomSheet<Product>(
      context: context,
      isScrollControlled: true,
      builder: (c) => _PickerSheet(subtitle: subtitle, noServices: noServices, initialQuery: initialQuery),
    );

class _PickerSheet extends ConsumerStatefulWidget {
  final String Function(Product p)? subtitle;
  final bool noServices;
  final String initialQuery;
  const _PickerSheet({this.subtitle, this.noServices = false, this.initialQuery = ''});
  @override
  ConsumerState<_PickerSheet> createState() => _PickerSheetState();
}

class _PickerSheetState extends ConsumerState<_PickerSheet> {
  late final _q = TextEditingController(text: widget.initialQuery);
  Timer? _debounce;
  StockFilter _stock = StockFilter.all;
  String? _category;
  List<Product>? _rows;

  /// 2026-10-31: список окном по [_page] товаров, дальше при прокрутке; фильтры остатка и «без услуг» — в SQL, а число «найдено» — из базы.
  static const _page = 500;
  int _limit = _page;
  bool _hasMore = false;
  bool _moreBusy = false;
  int _total = 0;
  int _seq = 0;
  final _scroll = ScrollController();

  @override
  void initState() {
    super.initState();
    _scroll.addListener(_onScrollMore);
    _load();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _scroll.dispose();
    super.dispose();
  }

  void _onScrollMore() {
    if (!_hasMore || _moreBusy || !_scroll.hasClients || _scroll.position.extentAfter > 600) return;
    _moreBusy = true;
    _limit += _page;
    _load(keepWindow: true).whenComplete(() => _moreBusy = false);
  }

  Future<void> _load({bool keepWindow = false}) async {
    final seq = ++_seq;
    if (!keepWindow) _limit = _page;
    final db = ref.read(databaseProvider);
    final stock = _stock;
    final noServices = widget.noServices;
    Expression<bool> extra($ProductsTable p) => (noServices ? p.isService.equals(false) : const Constant(true)) & stockFilterSql(p, stock);
    final rows = await db.searchProducts(_q.text, category: _category, limit: _limit + 1, extra: extra);
    final total = keepWindow ? _total : await db.countProducts(_q.text, category: _category, extra: extra);
    if (!mounted || seq != _seq) return;
    setState(() {
      _hasMore = rows.length > _limit;
      _rows = _hasMore ? rows.sublist(0, _limit) : rows;
      _total = total;
    });
  }

  // 2026-10-21, владелец: «на Enter при добавлении товара в КП и счёт он удаляет или дублирует». Выбор — один раз: повторное
  // касание строки или второй Enter, пока окно закрывается, ничего не добавляют (раньше второй вызов закрывал и экран под окном).
  bool _picked = false;
  void _pick(Product p) {
    if (_picked) return;
    _picked = true;
    Navigator.pop(context, p);
  }

  /// Enter / «Найти» в поиске (и сканер-клавиатура: цифры + Enter): точный штрихкод или единственный найденный товар.
  Future<void> _submit(String q) async {
    if (_picked || q.trim().isEmpty) return;
    final db = ref.read(databaseProvider);
    // 2026-10-22 (аудит П7): код, которого нет на телефоне, — ещё и на сервере (товар с ПК, каталог не успел обновиться)
    final hit = (await CodeLookup.find(db, ref.read(apiProvider), q.trim())).product;
    if (!mounted) return;
    if (hit != null && (!widget.noServices || !hit.isService)) {
      _pick(hit);
      return;
    }
    _debounce?.cancel();
    await _load();
    if (mounted && _rows?.length == 1) _pick(_rows!.first);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return ConstrainedBox(
      constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * .88),
      child: SafeArea(
        child: Padding(
          padding: EdgeInsets.fromLTRB(16, 14, 16, MediaQuery.viewInsetsOf(context).bottom + 8),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextField(
                controller: _q,
                textInputAction: TextInputAction.search,
                onSubmitted: _submit,
                decoration: InputDecoration(hintText: l.nameOrBarcode, prefixIcon: const Icon(Icons.search_rounded)),
                onChanged: (_) {
                  _debounce?.cancel();
                  _debounce = Timer(const Duration(milliseconds: 200), _load);
                },
              ),
              const SizedBox(height: 6),
              ProductFilterBar(
                stock: _stock,
                category: _category,
                onChanged: (s, c) {
                  setState(() {
                    _stock = s;
                    _category = c;
                  });
                  _load();
                },
              ),
              // 2026-10-31: найдено больше, чем показано — видно, сколько всего (дальше список подгружается при прокрутке)
              if (_rows != null && _total > _rows!.length)
                Padding(
                  padding: const EdgeInsets.fromLTRB(4, 0, 4, 4),
                  child: Text(l.stockCountShown(_rows!.length, _total), style: TextStyle(fontSize: 12, color: pal.textSoft)),
                ),
              Flexible(
                child: _rows == null
                    ? const Padding(
                        padding: EdgeInsets.all(24),
                        child: Center(child: CircularProgressIndicator()),
                      )
                    : _rows!.isEmpty
                    ? Padding(
                        padding: const EdgeInsets.all(24),
                        child: Text(
                          l.nothingFound,
                          textAlign: TextAlign.center,
                          style: TextStyle(color: pal.textSoft),
                        ),
                      )
                    : ListView.builder(
                        controller: _scroll,
                        shrinkWrap: true,
                        itemCount: _rows!.length,
                        itemBuilder: (c, i) {
                          final p = _rows![i];
                          return ListTile(
                            contentPadding: const EdgeInsets.symmetric(horizontal: 4),
                            title: Text(p.name, maxLines: 2, overflow: TextOverflow.ellipsis),
                            subtitle: Text(
                              widget.subtitle?.call(p) ?? '${money(p.price)} ${l.som}${p.isService ? '' : ' · ${qtyText(p.quantity)} ${p.unit}'}',
                              style: TextStyle(color: !p.isService && isLowStock(p.quantity, p.minQuantity) ? T.warn : pal.textSoft, fontSize: 12.5),
                            ),
                            trailing: Icon(Icons.add_circle_rounded, color: pal.action, size: 28),
                            onTap: () => _pick(p),
                          );
                        },
                      ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
