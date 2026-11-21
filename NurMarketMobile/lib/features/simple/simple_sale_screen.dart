import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/access.dart';
import '../../core/util/audit.dart';
import '../../core/util/ui.dart';
import '../../core/api/session_store.dart';
import '../payment/quick_pay_preset.dart';
import '../payment/sales_repository.dart' show PayMethod;
import '../sale/cart.dart';
import '../sale/sale_screen.dart' show addProduct, clearCartAudited;
import 'quick_product.dart';
import 'simple_math.dart';
import 'simple_sheets.dart';

/// 2026-11-20 (Простой режим, М3): частота продаж товаров на телефоне — плитки «Частые». Лежит в настройках (`simple.freq`, JSON
/// «id → сколько раз положили в чек»), схема базы не меняется.
class SimpleFrequent {
  static const key = 'simple.freq';
  static const _cap = 200;

  static Future<Map<String, int>> read(AppDatabase db) async {
    try {
      final raw = await db.getSetting(key);
      if (raw == null || raw.isEmpty) return {};
      return {for (final e in (jsonDecode(raw) as Map).entries) '${e.key}': (e.value as num).toInt()};
    } catch (_) {
      return {};
    }
  }

  static Future<void> bump(AppDatabase db, String id) async {
    try {
      final m = await read(db);
      m[id] = (m[id] ?? 0) + 1;
      if (m.length > _cap) {
        final keep = m.entries.toList()..sort((a, b) => b.value.compareTo(a.value));
        m
          ..clear()
          ..addEntries(keep.take(_cap));
      }
      await db.setSetting(key, jsonEncode(m));
    } catch (_) {
      // частота — удобство
    }
  }
}

Future<bool>? _openingShift;

/// 2026-11-20 (Простой режим, М2): смена открывается сама при первой продаже, без вопроса про остаток. Касса выбрана заранее (одна касса
/// выбирается сама на экране выбора). Остаток на начало — «по системе» (итог прошлой закрытой смены) или 0 (сервер принимает смену без
/// остатка, ответ Умара 20.11). Нет связи и своей смены нет — продавать нельзя, сообщение.
Future<bool> ensureShift(BuildContext context, WidgetRef ref) {
  if (ref.read(shiftProvider).isOpen) return Future.value(true);
  return _openingShift ??= _openShift(context, ref).whenComplete(() => _openingShift = null);
}

Future<bool> _openShift(BuildContext context, WidgetRef ref) async {
  final notifier = ref.read(shiftProvider.notifier);
  final repo = ref.read(shiftRepoProvider);
  final box = ref.read(sessionProvider)?.cashboxId;
  final msg = ScaffoldMessenger.maybeOf(context);
  final lang = Localizations.localeOf(context).languageCode;
  String text(String ru, String ky, String uz, String en, String tr) => switch (lang) {
    'ky' => ky,
    'uz' => uz,
    'en' => en,
    'tr' => tr,
    _ => ru,
  };
  await notifier.refresh(); // смена могла быть уже открыта на сервере
  if (ref.read(shiftProvider).isOpen) return true;
  if (ref.read(shiftProvider).error != null) {
    msg?.showSnackBar(
      SnackBar(
        content: Text(
          text(
            'Нет связи: смену открыть нельзя. Подключитесь к интернету',
            'Байланыш жок: смена ачылбайт. Интернетке туташыңыз',
            'Aloqa yo\'q: smenani ochib bo\'lmaydi. Internetga ulaning',
            'No connection: the shift cannot be opened. Connect to the internet',
            'Bağlantı yok: vardiya açılamıyor. İnternete bağlanın',
          ),
        ),
        backgroundColor: T.danger,
      ),
    );
    return false;
  }
  final system = await repo.lastClosedCash(box).timeout(const Duration(seconds: 5), onTimeout: () => null);
  final cash = system?.cash ?? 0.0;
  try {
    await notifier.open(cash);
    msg?.showSnackBar(
      SnackBar(
        content: Text(
          text(
            'Смена открыта сама. Наличных на начало: ${money(cash)} сом${system == null ? ' — сверьте деньги в кассе' : ''}',
            'Смена өзү ачылды. Башталышта накталай: ${money(cash)} сом${system == null ? ' — кассадагы акчаны текшериңиз' : ''}',
            'Smena o\'zi ochildi. Boshida naqd: ${money(cash)} som${system == null ? ' — kassadagi pulni tekshiring' : ''}',
            'The shift opened by itself. Starting cash: ${money(cash)} som${system == null ? ' — check the cash in the drawer' : ''}',
            'Vardiya kendiliğinden açıldı. Başlangıç nakit: ${money(cash)} som${system == null ? ' — kasadaki parayı kontrol edin' : ''}',
          ),
        ),
      ),
    );
  } on ApiException catch (e) {
    if (e.status == 400) await notifier.refresh(); // «уже открыта» — смена появится
    if (!ref.read(shiftProvider).isOpen) msg?.showSnackBar(SnackBar(content: Text(e.message), backgroundColor: T.danger));
  }
  return ref.read(shiftProvider).isOpen;
}

/// Продажа в Простом режиме: плитки (фото, название, цена), «+» «−», кнопка «Оплатить N сом» — наличными сразу; торг, «Получено» со сдачей,
/// «Другая оплата» (карта, в долг). Главные действия — внизу. Цены, остатки, скидки и оплата считаются теми же правилами, что в обычной продаже.
class SimpleSaleScreen extends ConsumerStatefulWidget {
  const SimpleSaleScreen({super.key});
  @override
  ConsumerState<SimpleSaleScreen> createState() => _SimpleSaleScreenState();
}

class _SimpleSaleScreenState extends ConsumerState<SimpleSaleScreen> {
  final _search = TextEditingController();
  Timer? _debounce;
  bool _showAll = false; // false — «Частые», true — «Все»
  List<Product> _products = [];
  bool _loaded = false;
  bool _payBusy = false;
  Timer? _busyTimer;
  int _seq = 0;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _busyTimer?.cancel();
    _search.dispose();
    super.dispose();
  }

  Future<void> _reload() async {
    final seq = ++_seq;
    final db = ref.read(databaseProvider);
    final q = _search.text.trim();
    List<Product> list;
    if (q.isNotEmpty || _showAll) {
      list = await db.searchProducts(q, limit: 300);
    } else {
      final freq = await SimpleFrequent.read(db);
      final favs = await db.searchProducts('', favorites: true, limit: 60);
      final byId = {for (final f in favs) f.id: f};
      final top = freq.entries.toList()..sort((a, b) => b.value.compareTo(a.value));
      for (final e in top.take(24)) {
        if (byId.containsKey(e.key)) continue;
        final p = await db.productById(e.key);
        if (p != null) byId[e.key] = p;
      }
      list = byId.values.toList()
        ..sort((a, b) {
          final c = (freq[b.id] ?? 0).compareTo(freq[a.id] ?? 0);
          return c != 0 ? c : a.name.compareTo(b.name);
        });
      // пока ничего не продавали и избранных нет — показываем весь каталог
      if (list.isEmpty) list = await db.searchProducts('', limit: 300);
    }
    if (!mounted || seq != _seq) return;
    setState(() {
      _products = list;
      _loaded = true;
    });
  }

  Future<void> _add(Product p) async {
    if (ref.read(cartProvider.notifier).frozen) return;
    if (!await ensureShift(context, ref) || !mounted) return;
    final before = ref.read(cartProvider).quantity;
    await addProduct(context, ref, p);
    if (ref.read(cartProvider).quantity != before) unawaited(SimpleFrequent.bump(ref.read(databaseProvider), p.id));
  }

  Future<void> _pay(QuickPayPreset preset) async {
    if (_payBusy) return;
    final cart = ref.read(cartProvider);
    if (cart.isEmpty || ref.read(cartProvider.notifier).frozen) return;
    _payBusy = true;
    var pushed = false;
    try {
      if (!await ensureShift(context, ref) || !mounted) return;
      // экран оплаты сам закрывается (pushReplacement на «Оплачено») — результат не ждём; повторное нажатие в первые 800 мс не открывает второй экран
      pushed = true;
      unawaited(context.push('/pay', extra: preset));
      _busyTimer?.cancel();
      _busyTimer = Timer(const Duration(milliseconds: 800), () => _payBusy = false);
    } finally {
      if (!pushed) _payBusy = false;
    }
  }

  Future<void> _received() async {
    final total = ref.read(cartProvider).total;
    final got = await askReceived(context, total: total);
    if (got != null && mounted) await _pay(QuickPayPreset(received: got));
  }

  Future<void> _haggle() async {
    if (!checkAccess(context, ref, Session.discount)) return;
    final cart = ref.read(cartProvider);
    final base = cartBase(cart);
    final r = await askHaggle(context, base: base, currentDiscount: cart.orderDiscount);
    if (r == null || !mounted) return;
    final pct = discountPercentOf(base, r.discountSum);
    if (r.discountSum > 0 && pct > haggleWarnPercent && !await confirmBigDiscount(context, pct)) return;
    if (!mounted) return;
    ref.read(cartProvider.notifier).setOrderDiscount(sum: r.discountSum);
    Audit.log('discount', 'Торг: ${money(r.discountSum)}', amount: ref.read(cartProvider).orderDiscount);
  }

  Future<void> _other() async {
    final v = await askOtherPay(context);
    if (v == null || !mounted) return;
    switch (v) {
      case OtherPay.cashAmount:
        await _received();
      case OtherPay.card:
        await _pay(const QuickPayPreset(method: PayMethod.card, auto: false));
      case OtherPay.debt:
        final d = await askDebt(context);
        if (d != null && mounted) await _pay(QuickPayPreset(method: PayMethod.debt, client: d.client, debtDue: d.due));
    }
  }

  Future<void> _scan() async {
    if (!await ensureShift(context, ref) || !mounted) return;
    await context.push('/scan?mode=sale');
  }

  void _openCart() => showModalBottomSheet<void>(context: context, isScrollControlled: true, showDragHandle: true, useSafeArea: true, builder: (_) => const SimpleCartSheet());

  Future<void> _newProduct() async {
    final made = await showQuickProduct(context);
    if (made == true && mounted) _reload();
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final cart = ref.watch(cartProvider);
    final session = ref.watch(sessionProvider);
    final canCreate = session != null && session.canRoute('/admin/new-product');
    final inCart = <String, double>{};
    for (final ln in cart.lines) {
      if (!ln.custom) inCart[ln.productId] = (inCart[ln.productId] ?? 0) + ln.qty;
    }
    ref.listen<Cart>(cartProvider, (prev, next) {
      if (prev != null && !prev.isEmpty && next.isEmpty && _search.text.isNotEmpty) {
        _search.clear();
        _reload();
      }
    });
    final textScale = MediaQuery.textScalerOf(context).scale(1.0).clamp(1.0, 1.6);
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(12, 4, 12, 4),
          child: Row(
            children: [
              Expanded(
                child: TextField(
                  key: const Key('simpleSearch'),
                  controller: _search,
                  textInputAction: TextInputAction.search,
                  style: const TextStyle(fontSize: 16),
                  onChanged: (_) {
                    _debounce?.cancel();
                    _debounce = Timer(const Duration(milliseconds: 300), _reload);
                  },
                  decoration: InputDecoration(
                    hintText: context.t5('Поиск', 'Издөө', 'Qidirish', 'Search', 'Ara'),
                    prefixIcon: const Icon(Icons.search_rounded),
                    suffixIcon: ValueListenableBuilder<TextEditingValue>(
                      valueListenable: _search,
                      builder: (c, v, _) => v.text.isEmpty
                          ? const SizedBox.shrink()
                          : IconButton(
                              tooltip: MaterialLocalizations.of(c).clearButtonTooltip,
                              icon: const Icon(Icons.close_rounded),
                              onPressed: () {
                                _search.clear();
                                _reload();
                              },
                            ),
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 8),
              _SquareBtn(key: const Key('simpleScan'), icon: Icons.qr_code_scanner_rounded, tooltip: context.l.a11yScanner, onTap: _scan),
              if (canCreate) ...[
                const SizedBox(width: 8),
                _SquareBtn(
                  key: const Key('simpleNewProduct'),
                  icon: Icons.add_rounded,
                  tooltip: context.t5('Новый товар', 'Жаңы товар', 'Yangi mahsulot', 'New product', 'Yeni ürün'),
                  onTap: _newProduct,
                ),
              ],
            ],
          ),
        ),
        SizedBox(
          height: 52,
          child: ListView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.fromLTRB(12, 2, 12, 2),
            children: [
              for (final (all, label) in [(false, context.t5('Частые', 'Көп сатылган', 'Tez-tez', 'Frequent', 'Sık satılan')), (true, context.l.all)])
                Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: ChoiceChip(
                    key: Key(all ? 'simpleChipAll' : 'simpleChipFreq'),
                    label: Text(label),
                    selected: _showAll == all,
                    labelStyle: TextStyle(color: _showAll == all ? pal.onAction : pal.text, fontWeight: FontWeight.w700, fontSize: 15),
                    onSelected: (_) {
                      setState(() => _showAll = all);
                      _reload();
                    },
                  ),
                ),
            ],
          ),
        ),
        Expanded(
          child: !_loaded
              ? const Center(child: CircularProgressIndicator())
              : _products.isEmpty
              ? Center(
                  child: Padding(
                    padding: const EdgeInsets.all(24),
                    child: Text(
                      context.l.nothingFound,
                      key: const Key('simpleEmpty'),
                      textAlign: TextAlign.center,
                      style: TextStyle(color: pal.textSoft, fontSize: 16),
                    ),
                  ),
                )
              : GridView.builder(
                  padding: const EdgeInsets.fromLTRB(12, 4, 12, 8),
                  gridDelegate: SliverGridDelegateWithMaxCrossAxisExtent(maxCrossAxisExtent: 190, mainAxisSpacing: 8, crossAxisSpacing: 8, mainAxisExtent: 118 + 62 * textScale),
                  itemCount: _products.length,
                  itemBuilder: (c, i) => _SimpleTile(product: _products[i], inCart: inCart[_products[i].id], onTap: () => _add(_products[i])),
                ),
        ),
        if (!cart.isEmpty) _PayBar(cart: cart, onPay: () => _pay(const QuickPayPreset()), onOther: _other, onReceived: _received, onHaggle: _haggle, onCart: _openCart),
      ],
    );
  }
}

class _SquareBtn extends StatelessWidget {
  final IconData icon;
  final String tooltip;
  final VoidCallback onTap;
  const _SquareBtn({super.key, required this.icon, required this.tooltip, required this.onTap});
  @override
  Widget build(BuildContext context) => Material(
    color: T.navy,
    borderRadius: BorderRadius.circular(12),
    child: InkWell(
      borderRadius: BorderRadius.circular(12),
      onTap: onTap,
      child: Tooltip(
        message: tooltip,
        child: SizedBox(width: 56, height: 56, child: Icon(icon, color: T.accent, size: 28)),
      ),
    ),
  );
}

class _SimpleTile extends StatelessWidget {
  final Product product;
  final double? inCart;
  final VoidCallback onTap;
  const _SimpleTile({required this.product, required this.onTap, this.inCart});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final l = context.l;
    return Semantics(
      button: true,
      label: '${product.name}, ${money(product.price)} ${l.som}',
      excludeSemantics: true,
      child: Material(
        key: Key('simpleTile_${product.id}'),
        color: pal.panel,
        borderRadius: BorderRadius.circular(14),
        child: InkWell(
          borderRadius: BorderRadius.circular(14),
          onTap: onTap,
          child: Container(
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(14),
              border: Border.all(color: inCart != null ? T.accent : pal.border, width: inCart != null ? 2 : 1),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(
                  child: Stack(
                    fit: StackFit.expand,
                    children: [
                      ProductThumb(name: product.name, url: product.imageUrl, fontSize: 22),
                      if (inCart != null)
                        Positioned(
                          top: 4,
                          right: 4,
                          child: Container(
                            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                            decoration: BoxDecoration(color: T.accent, borderRadius: BorderRadius.circular(12)),
                            child: Text(
                              '×${qtyText(inCart!)}',
                              style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 14),
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  product.name,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600, height: 1.15),
                ),
                Text(
                  '${money(product.price)} ${l.som}${product.isWeight ? ' / ${product.unit}' : ''}',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontSize: 17, fontWeight: FontWeight.w800, color: pal.price),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Нижняя панель оплаты: последняя позиция с «−» «+», «Торг» / «Получено» / «Другая оплата», крупная кнопка «Оплатить N сом».
class _PayBar extends ConsumerWidget {
  final Cart cart;
  final VoidCallback onPay, onOther, onReceived, onHaggle, onCart;
  const _PayBar({required this.cart, required this.onPay, required this.onOther, required this.onReceived, required this.onHaggle, required this.onCart});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final pal = context.pal;
    final l = context.l;
    final last = cart.lines.last;
    final lastIndex = cart.lines.length - 1;
    final ctl = ref.read(cartProvider.notifier);
    final haggled = cart.orderDiscount > 0;
    final bold = TextStyle(fontSize: 13, fontWeight: FontWeight.w800, color: pal.text);
    Widget small(String key, IconData icon, String text, VoidCallback onTap) => Expanded(
      child: SizedBox(
        height: 56,
        child: OutlinedButton(
          key: Key(key),
          style: OutlinedButton.styleFrom(
            padding: const EdgeInsets.symmetric(horizontal: 4),
            side: BorderSide(color: pal.borderStrong, width: 1.5),
          ),
          onPressed: onTap,
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(icon, size: 20, color: pal.text),
              FittedBox(
                fit: BoxFit.scaleDown,
                child: Text(text, maxLines: 1, style: bold),
              ), // уменьшается по ширине кнопки, а не обрезается
            ],
          ),
        ),
      ),
    );
    return Container(
      key: const Key('simplePayBar'),
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
      decoration: BoxDecoration(
        color: pal.window,
        border: Border(top: BorderSide(color: pal.border)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // последняя позиция: «−» количество «+», остальной чек — по «Весь чек»
          Row(
            children: [
              if (!last.isWeight && !last.custom) ...[
                _RoundBtn(key: const Key('simpleMinus'), icon: Icons.remove_rounded, onTap: () => ctl.setQty(lastIndex, last.qty - 1)),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 10),
                  child: Text(
                    qtyText(last.qty),
                    key: const Key('simpleQty'),
                    style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800),
                  ),
                ),
                _RoundBtn(key: const Key('simplePlus'), icon: Icons.add_rounded, onTap: () => ctl.setQty(lastIndex, last.qty + 1)),
                const SizedBox(width: 10),
              ],
              Expanded(
                child: Text(
                  last.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w700),
                ),
              ),
              // весь чек: значок с числом позиций (кнопка 56×56, подпись — в подсказке)
              IconButton(
                key: const Key('simpleWholeCheck'),
                tooltip: context.t5('Весь чек (${cart.count})', 'Бүт чек (${cart.count})', 'Butun chek (${cart.count})', 'Whole check (${cart.count})', 'Tüm fiş (${cart.count})'),
                style: IconButton.styleFrom(minimumSize: const Size(56, 56), fixedSize: const Size(56, 56)),
                onPressed: onCart,
                icon: Badge(label: Text('${cart.count}'), child: const Icon(Icons.receipt_long_outlined, size: 28)),
              ),
            ],
          ),
          if (haggled)
            Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: Text(
                '${context.t5('Торг', 'Соодалашуу', 'Savdolashish', 'Haggle', 'Pazarlık')}: −${money(cart.orderDiscount)} ${l.som}',
                key: const Key('simpleHaggled'),
                style: TextStyle(fontSize: 14, fontWeight: FontWeight.w800, color: pal.success),
              ),
            ),
          Row(
            children: [
              small('simpleHaggleBtn', Icons.handshake_outlined, context.t5('Торг', 'Соодалашуу', 'Savdo', 'Haggle', 'Pazarlık'), onHaggle),
              const SizedBox(width: 8),
              small('simpleReceivedBtn', Icons.payments_outlined, context.t5('Получено', 'Алынды', 'Olindi', 'Received', 'Alındı'), onReceived),
              const SizedBox(width: 8),
              small('simpleOtherBtn', Icons.swap_horiz_rounded, context.t5('Другая оплата', 'Башка төлөм', 'Boshqa to\'lov', 'Other payment', 'Diğer ödeme'), onOther),
            ],
          ),
          const SizedBox(height: 8),
          FilledButton(
            key: const Key('simplePayBtn'),
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(64),
              textStyle: const TextStyle(fontSize: 21, fontWeight: FontWeight.w800),
            ),
            onPressed: onPay,
            onLongPress: onOther, // долгое нажатие — другой способ оплаты
            child: FittedBox(fit: BoxFit.scaleDown, child: Text(l.payAmount(money(cart.total)), maxLines: 1)),
          ),
        ],
      ),
    );
  }
}

class _RoundBtn extends StatelessWidget {
  final IconData icon;
  final VoidCallback onTap;
  const _RoundBtn({super.key, required this.icon, required this.onTap});
  @override
  Widget build(BuildContext context) => Material(
    color: context.pal.panel,
    shape: CircleBorder(side: BorderSide(color: context.pal.borderStrong, width: 1.5)),
    child: InkWell(
      customBorder: const CircleBorder(),
      onTap: onTap,
      child: SizedBox(width: 56, height: 56, child: Icon(icon, size: 28, color: context.pal.text)),
    ),
  );
}

/// Весь чек: строки с «−» «+», «Очистить».
class SimpleCartSheet extends ConsumerWidget {
  const SimpleCartSheet({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final pal = context.pal;
    final cart = ref.watch(cartProvider);
    final ctl = ref.read(cartProvider.notifier);
    if (cart.isEmpty) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (context.mounted && Navigator.of(context).canPop()) Navigator.of(context).pop();
      });
    }
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(context.l.inCheck, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
            const SizedBox(height: 6),
            Flexible(
              child: ListView.builder(
                shrinkWrap: true,
                itemCount: cart.lines.length,
                itemBuilder: (c, i) {
                  final ln = cart.lines[i];
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 4),
                    child: Row(
                      children: [
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                ln.name,
                                maxLines: 2,
                                overflow: TextOverflow.ellipsis,
                                style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w700),
                              ),
                              Text('${money(ln.price)} × ${qtyText(ln.qty)} = ${money(ln.total)} ${context.l.som}', style: TextStyle(fontSize: 13, color: pal.textSoft)),
                            ],
                          ),
                        ),
                        if (!ln.isWeight && !ln.custom) ...[
                          _RoundBtn(icon: Icons.remove_rounded, onTap: () => ctl.setQty(i, ln.qty - 1)),
                          const SizedBox(width: 6),
                          _RoundBtn(icon: Icons.add_rounded, onTap: () => ctl.setQty(i, ln.qty + 1)),
                        ] else
                          _RoundBtn(icon: Icons.delete_outline_rounded, onTap: () => ctl.remove(i)),
                      ],
                    ),
                  );
                },
              ),
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: Text('${money(cart.total)} ${context.l.som}', style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w800)),
                ),
                TextButton(
                  key: const Key('simpleClearCart'),
                  style: TextButton.styleFrom(minimumSize: const Size(64, 56), foregroundColor: pal.danger),
                  onPressed: () async {
                    await clearCartAudited(context, ref);
                  },
                  child: Text(context.l.clearCheck),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
