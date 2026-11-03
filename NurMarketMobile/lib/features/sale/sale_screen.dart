import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ui/link_banner.dart';
import '../../core/util/access.dart';
import '../../core/api/session_store.dart';
import '../../core/catalog/global_products.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/admin_repository.dart';
import '../admin/admin_screens.dart';
import '../scan/code_lookup.dart';
import '../scan/scan_animations.dart';
import '../scan/weigh_camera.dart';
import '../../core/tester/tester_mode.dart' show TesterGate;
import '../stock/product_packages.dart';
import 'piece_sale_mode.dart';
import 'cart.dart';
import 'catalog_repository.dart';
import 'variant_picker.dart';
import 'variant_service.dart';
import '../../core/shop_mode.dart';
import '../../core/util/audit.dart';

/// Продажа (Т2): поиск, избранное и категории, плитки, чек; смахнуть строку влево — убрать.
class SaleScreen extends ConsumerStatefulWidget {
  const SaleScreen({super.key});
  @override
  ConsumerState<SaleScreen> createState() => _SaleScreenState();
}

class _SaleScreenState extends ConsumerState<SaleScreen> {
  final _search = TextEditingController();
  final _searchFocus = FocusNode(); // 2026-10-26 (живой аудит, П3, В-2): фокус возвращается в поле после скана
  String? _category; // null — всё, '' — избранное
  List<Product> _products = [];
  List<String> _categories = [];
  Map<String, PiecePack> _packs = {}; // 2026-10-27 (М4): поштучные упаковки товаров — остаток плитки как «12 пачек + 5 шт»
  Map<String, ({int count, double qty})> _variants = {}; // 2026-10-31 (одежда): у каких товаров на плитке есть размеры и сколько штук всего
  bool _clothingHint = false; // 2026-10-31: в магазине есть размеры, а режим «Одежда» не включён — подсказка
  int _prefetchSeq = 0;
  Timer? _debounce;

  /// 2026-10-30 (оптимизация, п. 13): плитки берутся окном — первые [_page] по запросу, дальше при прокрутке (раньше на каждую
  /// букву поиска читались ВСЕ товары, до 20 000 объектов). Номер запроса отсекает устаревшие ответы.
  static const _page = 400;
  int _limit = _page;
  bool _hasMore = false;
  bool _moreBusy = false;
  int _seq = 0;
  final _scroll = ScrollController();

  /// 13.10, владелец: «в чеке отдельное таб-окно» — «Товары» и «Чек» отдельными вкладками.
  bool _showCheck = false;

  @override
  void dispose() {
    _debounce?.cancel();
    _scroll.dispose();
    HardwareKeyboard.instance.removeHandler(_onKey);
    _search.dispose();
    _searchFocus.dispose();
    super.dispose();
  }

  @override
  void initState() {
    super.initState();
    _reload(refreshMeta: true);
    _scroll.addListener(_onScrollMore);
    HardwareKeyboard.instance.addHandler(_onKey);
    unawaited(ScaleRulesLoader.ensure(ref.read(databaseProvider), ref.read(apiProvider)).catchError((_) {}));
  }

  /// 2026-10-22 (аудит П5): ручной сканер-клавиатура (Bluetooth, ТСД Sunmi/Urovo). Сканер «печатает» код быстро
  /// (между знаками меньше 60 мс) и жмёт Enter. Если поле поиска не в фокусе — ловим такие нажатия сами; в поле поиска
  /// код приходит через [_submitScan] (Enter). Раньше код только фильтровал список, товар в чек не попадал.
  final _keys = StringBuffer();
  DateTime _lastKey = DateTime(2000);

  bool _onKey(KeyEvent e) {
    if (e is! KeyDownEvent) return false;
    // 2026-11-02 (аудит №2, М3-01): экран оплаты (или другое окно) поверх продажи — сканер в чек не пишет
    if (!mounted || ModalRoute.of(context)?.isCurrent != true) return false;
    final focused = FocusManager.instance.primaryFocus?.context?.widget;
    if (focused is EditableText) return false; // печатает человек или сканер в поле — там свой Enter
    final now = DateTime.now();
    if (now.difference(_lastKey) > const Duration(milliseconds: 80)) _keys.clear();
    _lastKey = now;
    if (e.logicalKey == LogicalKeyboardKey.enter || e.logicalKey == LogicalKeyboardKey.numpadEnter) {
      final code = _keys.toString();
      _keys.clear();
      if (code.length >= 4) {
        _submitScan(code);
        return true;
      }
      return false;
    }
    final ch = e.character;
    if (ch != null && ch.length == 1 && RegExp(r'[0-9A-Za-zА-Яа-я\-]').hasMatch(ch)) _keys.write(ch);
    return false;
  }

  bool _scanBusy = false;

  /// Enter в поиске или код ручного сканера: точный штрихкод (на телефоне, потом на сервере) — сразу в чек.
  /// Не код — обычный поиск.
  Future<void> _submitScan(String raw) async {
    final q = raw.trim();
    if (q.isEmpty || _scanBusy) return;
    // 2026-11-02 (аудит №2, М3-01): идёт оплата — чек заморожен, код не принимаем
    if (ref.read(cartProvider.notifier).frozen) {
      if (mounted) toast(context, context.l.payInProgressScan, error: true);
      return;
    }
    _scanBusy = true;
    _hideNotice();
    // 2026-10-26 (живой аудит, П3, В-2): после «не найден» код оставался в поле и следующий скан дописывался к нему —
    // товар не находился. Теперь после любого скана штрихкода поле очищено, а фокус (если он был в поле) вернулся.
    final hadFocus = _searchFocus.hasFocus;
    final isCode = RegExp(r'^\d{6,}$').hasMatch(q.replaceAll(' ', ''));
    try {
      final l = context.l;
      final hit = await CodeLookup.find(ref.read(databaseProvider), ref.read(apiProvider), q);
      if (!mounted) return;
      final p = hit.product;
      if (p != null) {
        final before = ref.read(cartProvider).quantity;
        await addProduct(context, ref, p, qty: hit.qty, code: q);
        if (!mounted) return;
        // 2026-10-31: «Добавлено» — только если в чеке что-то прибавилось (размер закончился или отключён — там своё сообщение)
        if (ref.read(cartProvider).quantity != before) toast(context, l.addedToCheckName(p.name));
        await _afterScan(hadFocus);
        return;
      }
      if (isCode) {
        await _afterScan(hadFocus);
        if (!mounted) return;
        final s = ref.read(sessionProvider);
        // администратору — сразу «Создать товар с этим кодом» (новый товар открывается с этим штрихкодом)
        final canCreate = !hit.unavailable && s != null && s.isAdmin && s.canRoute('/admin/new-product');
        // 2026-10-27: код есть во встроенном справочнике NurCRM — ненавязчивая подсказка в той же плашке (без ожидания сети)
        final known = canCreate ? await ref.read(globalProductsProvider).localIfReady(q) : null;
        if (!mounted) return;
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(
            SnackBar(
              content: Text(
                hit.unavailable
                    ? l.notFoundOffline(q)
                    : known == null
                    ? l.barcodeNotFound(q)
                    : '${l.barcodeNotFound(q)}\n${l.globalFoundAsk(known.name)}',
              ),
              backgroundColor: T.danger,
              // 2026-10-26 (приёмка 1.18.6, В-2н): у SnackBar с кнопкой по умолчанию persist: true — плашка висела, пока не
              // нажмут, и закрывала «Оплатить» и нижний ряд. Теперь сама уходит через 5 с (а при следующем скане, вводе в
              // поиск или касании плитки — сразу, см. _hideNotice).
              persist: false,
              duration: const Duration(seconds: 5),
              // плашка «плавает» НАД нижней панелью с «Оплатить» (её высота измерена по ключу), а не лежит на ней
              behavior: SnackBarBehavior.floating,
              margin: EdgeInsets.fromLTRB(12, 0, 12, (_payBarKey.currentContext?.size?.height ?? 72) + 8),
              action: canCreate
                  ? SnackBarAction(
                      label: l.createProductWithCode,
                      textColor: Colors.white,
                      onPressed: () => context.push('/admin/new-product?barcode=${Uri.encodeQueryComponent(q)}'),
                    )
                  : null,
            ),
          );
      }
    } finally {
      _scanBusy = false;
    }
  }

  /// 2026-10-26 (приёмка 1.18.6, В-2н): любое новое действие кассира убирает красную плашку «Штрихкод не найден» —
  /// она не должна закрывать «Оплатить» и плитки, когда уже не нужна.
  final _payBarKey = GlobalKey();

  void _hideNotice() {
    if (mounted) ScaffoldMessenger.maybeOf(context)?.hideCurrentSnackBar();
  }

  /// Поле поиска — пустое и (если скан пришёл из поля) снова в фокусе: следующий скан сразу учитывается.
  Future<void> _afterScan(bool refocus) async {
    if (_search.text.isNotEmpty) {
      _search.clear();
      await _reload();
    }
    if (refocus && mounted) _searchFocus.requestFocus();
  }

  /// Дошли почти до конца плиток — окно растёт на страницу.
  void _onScrollMore() {
    if (_showCheck || !_hasMore || _moreBusy || !_scroll.hasClients) return;
    if (_scroll.position.extentAfter < 900) {
      _moreBusy = true;
      _limit += _page;
      _reload(keepWindow: true).whenComplete(() => _moreBusy = false);
    }
  }

  /// [keepWindow] — окно не сбрасывается (после продажи, при подгрузке); [refreshMeta] — заново прочитать категории и упаковки
  /// (при открытии и после продажи; пока человек набирает поиск, они не меняются и между буквами не читаются).
  Future<void> _reload({bool keepWindow = false, bool refreshMeta = false}) async {
    final seq = ++_seq;
    if (!keepWindow) _limit = _page;
    final db = ref.read(databaseProvider);
    final cats = refreshMeta || _categories.isEmpty ? await db.categories() : null;
    final list = await db.searchProducts(_search.text, category: _category == '' ? null : _category, favorites: _category == '', limit: _limit + 1);
    final packs = refreshMeta || cats != null ? await PackageStore.read(db) : null;
    final shown = list.length > _limit ? list.sublist(0, _limit) : list;
    final variants = await db.variantSummary(shown.map((x) => x.id));
    final hint = await _needClothingHint(db);
    if (!mounted || seq != _seq) return; // пришёл более новый запрос — этот ответ устарел
    setState(() {
      if (cats != null) _categories = cats;
      _hasMore = list.length > _limit;
      _products = shown;
      if (packs != null) _packs = packs;
      _variants = variants;
      _clothingHint = hint;
    });
    unawaited(_prefetchVariants(shown, seq));
  }

  /// 2026-10-31 (режим магазина): размеры есть, режим «Одежда» выключен и на подсказку не отвечали «Не нужно».
  Future<bool> _needClothingHint(AppDatabase db) async {
    if (ref.read(prefsProvider).clothing) return false;
    if ((await db.getSetting(ShopModeSync.hintOffKey)) == '1') return false;
    return db.hasAnyVariants();
  }

  /// 2026-10-31 (одежда): в режиме «Одежда» размеры показанных товаров подтягиваются в фоне (сервер не говорит, у каких товаров они
  /// есть), чтобы на плитках была пометка «N размеров/цветов» и остаток по размерам. По одному товару с паузой, не больше
  /// [_prefetchMax] за раз; новый запрос списка прерывает предыдущий обход.
  static const _prefetchMax = 40;
  Future<void> _prefetchVariants(List<Product> shown, int seq) async {
    if (!ref.read(prefsProvider).clothing) return;
    final db = ref.read(databaseProvider);
    final svc = ref.read(variantServiceProvider);
    final mine = ++_prefetchSeq;
    var done = 0;
    for (final x in shown) {
      if (!mounted || mine != _prefetchSeq) return;
      if (x.isWeight || x.isService) continue;
      if (done >= _prefetchMax) break;
      if (await db.variantCheck(x.id) != null) continue;
      final r = await svc.forProduct(x.id, timeout: const Duration(seconds: 3));
      if (r.unknown) break; // нет связи — дальше не пробуем
      done++;
      if (done % 8 == 0 && mounted && mine == _prefetchSeq) {
        final v = await db.variantSummary(shown.map((y) => y.id));
        if (mounted && mine == _prefetchSeq) setState(() => _variants = v);
      }
      await Future<void>.delayed(const Duration(milliseconds: 250));
    }
    if (done > 0 && mounted && mine == _prefetchSeq) {
      final v = await db.variantSummary(shown.map((y) => y.id));
      if (mounted) setState(() => _variants = v);
    }
  }

  /// Подсказка «Включить режим «Одежда»?» — когда в каталоге нашлись товары с размерами и цветами.
  Widget _clothingHintCard(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Container(
      key: const Key('clothingHint'),
      margin: const EdgeInsets.fromLTRB(16, 0, 16, 8),
      padding: const EdgeInsets.fromLTRB(12, 10, 12, 6),
      decoration: BoxDecoration(color: pal.panel, borderRadius: BorderRadius.circular(14), border: Border.all(color: T.accent)),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.checkroom_rounded, size: 20),
              const SizedBox(width: 8),
              Expanded(child: Text(l.clothingHintTitle, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 13.5))),
            ],
          ),
          const SizedBox(height: 4),
          Text(l.clothingHintAsk, style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
          Row(
            mainAxisAlignment: MainAxisAlignment.end,
            children: [
              TextButton(
                key: const Key('clothingHintNo'),
                style: TextButton.styleFrom(minimumSize: const Size(48, 48)),
                onPressed: () async {
                  await ref.read(databaseProvider).setSetting(ShopModeSync.hintOffKey, '1');
                  if (mounted) setState(() => _clothingHint = false);
                },
                child: Text(l.clothingHintNo),
              ),
              FilledButton(
                key: const Key('clothingHintYes'),
                style: FilledButton.styleFrom(minimumSize: const Size(48, 48)),
                onPressed: () async {
                  final ok = await ref.read(prefsProvider.notifier).setShop(ShopMode.clothing, toServer: true);
                  if (!mounted) return;
                  setState(() => _clothingHint = false);
                  toast(this.context, '${l.shopModeOn(ShopMode.clothing.title(l))}. ${ok ? l.shopModeSavedAll : l.shopModePhoneOnly}');
                  _reload(keepWindow: true);
                },
                child: Text(l.clothingHintYes),
              ),
            ],
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final cart = ref.watch(cartProvider);
    final p = context.pal;
    // 2026-10-31 (режим магазина): режим сменили (профиль, подсказка, сервер) — плитки, подсказки и размеры обновляются сразу
    ref.listen<ShopMode>(prefsProvider.select((x) => x.shop), (prev, next) {
      if (prev != next) _reload(keepWindow: true, refreshMeta: true);
    });
    // 2026-10-26 (живой аудит, П3, С-1): чек оплачен/отложен/очищен — поиск прошлого покупателя не остаётся в поле
    ref.listen<Cart>(cartProvider, (prev, next) {
      if (prev != null && !prev.isEmpty && next.isEmpty) {
        if (_search.text.isNotEmpty) _search.clear();
        // 2026-10-27 (аудит мобилки, С-2): после проданного чека остаток на плитке — из базы телефона (продажа уже вычла его
        // локально, sales_repository._applyStock); раньше список перечитывался только при очистке поиска и показывал старые «48 шт».
        _reload(keepWindow: true, refreshMeta: true);
      }
    });
    // сколько штук каждого товара уже в чеке — значок «×2» на плитке
    final inCart = <String, double>{};
    for (final ln in cart.lines) {
      if (!ln.custom) inCart[ln.productId] = (inCart[ln.productId] ?? 0) + ln.qty;
    }
    final textScale = MediaQuery.textScalerOf(context).scale(10) / 10;
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
            onSelectionChanged: (v) {
              _hideNotice();
              setState(() => _showCheck = v.first);
            },
          ),
        ),
        if (!_showCheck) ...[
          const LinkBanner(catalog: true, margin: EdgeInsets.fromLTRB(16, 0, 16, 8)), // 2026-10-29 (аудит, п. 5): «Остатки на ЧЧ:ММ»
          if (_clothingHint) _clothingHintCard(context),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _search,
                    focusNode: _searchFocus,
                    textInputAction: TextInputAction.search,
                    onSubmitted: _submitScan,
                    // поиск после паузы в наборе, а не на каждую букву (на слабых телефонах ввод подтормаживал)
                    onChanged: (_) {
                      _hideNotice();
                      _debounce?.cancel();
                      _debounce = Timer(const Duration(milliseconds: 300), _reload);
                    },
                    decoration: InputDecoration(
                      // 2026-10-31 (режим магазина): подсказка поиска под режим; режим не выбран — прежняя
                      hintText: ref.watch(prefsProvider).shopChosen ? ref.watch(prefsProvider).shop.searchHint(l) : l.searchProduct,
                      prefixIcon: const Icon(Icons.search_rounded),
                      // 2026-10-26 (С-1): кнопка «×» — очистить поиск
                      suffixIcon: ValueListenableBuilder<TextEditingValue>(
                        valueListenable: _search,
                        builder: (c, v, _) => v.text.isEmpty
                            ? const SizedBox.shrink()
                            : IconButton(
                                tooltip: MaterialLocalizations.of(c).clearButtonTooltip,
                                icon: const Icon(Icons.close_rounded),
                                onPressed: () {
                                  _search.clear();
                                  _searchFocus.requestFocus();
                                  _reload();
                                },
                              ),
                      ),
                    ),
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
                // 2026-10-31 (режим магазина): в «Одежде» и «Услугах» весов нет — кнопка не показывается
                if (ref.watch(prefsProvider).shop.weighing) ...[
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
              ],
            ),
          ),
          // 2026-10-26 (приёмка 1.18.6, Н-6н): чипы категорий были зажаты в 36 dp (высота списка 48 − отступы 12) — теперь
          // в списке есть место под область касания 48 dp у каждого чипа.
          SizedBox(
            height: 52,
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.fromLTRB(16, 2, 16, 2),
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
            controller: _scroll,
            slivers: [
              if (!_showCheck)
                SliverPadding(
                  padding: const EdgeInsets.fromLTRB(16, 6, 16, 8),
                  sliver: _products.isEmpty
                      ? SliverToBoxAdapter(
                          child: Padding(
                            padding: const EdgeInsets.all(24),
                            child: Center(
                              child: Text(
                                ref.watch(prefsProvider).shopChosen ? '${l.nothingFound}\n${ref.watch(prefsProvider).shop.emptyText(l)}' : l.nothingFound,
                                key: const Key('saleEmpty'),
                                textAlign: TextAlign.center,
                                style: TextStyle(color: p.textSoft),
                              ),
                            ),
                          ),
                        )
                      : SliverGrid.builder(
                          // 2026-10-26 (П3): высота плитки от шрифта (добавилась строка остатка), а не фиксированное соотношение
                          gridDelegate: SliverGridDelegateWithMaxCrossAxisExtent(
                            maxCrossAxisExtent: 140,
                            mainAxisSpacing: 8,
                            crossAxisSpacing: 8,
                            mainAxisExtent: 96 + (_variants.isEmpty ? 62 : 76) * textScale,
                          ),
                          itemCount: _products.length,
                          itemBuilder: (c, i) => _ProductTile(_products[i], inCart: inCart[_products[i].id], pack: _packs[_products[i].id], variants: _variants[_products[i].id], mode: ref.watch(prefsProvider).shopChosen ? ref.watch(prefsProvider).shop : null, onTap: () {
                            _hideNotice();
                            addProduct(context, ref, _products[i]).whenComplete(() {
                              if (mounted) _reload(keepWindow: true);
                            });
                          }),
                        ),
                ),
              if (_showCheck) ...[
                SliverToBoxAdapter(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(16, 8, 16, 6),
                    child: Row(
                      children: [
                        // 2026-10-26 (живой аудит, П3): при большом шрифте подпись переносится, а не вылезает за край
                        Expanded(
                          child: Wrap(
                            spacing: 8,
                            crossAxisAlignment: WrapCrossAlignment.center,
                            children: [
                              Text(l.inCheck, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
                              Text(l.positions(cart.count), style: TextStyle(color: p.textSoft, fontSize: 12.5)),
                            ],
                          ),
                        ),
                        const SizedBox(width: 8),
                        // 13.10, владелец: «продажа оптом и в розницу» — как переключатель «Розница / Опт» на кассе.
                        SegmentedButton<bool>(
                          showSelectedIcon: false,
                          // 2026-10-26 (приёмка 1.18.6, Н-6н): было compact + shrinkWrap = 32 dp по высоте; теперь касание 48 dp
                          style: const ButtonStyle(minimumSize: WidgetStatePropertyAll(Size(0, 48)), tapTargetSize: MaterialTapTargetSize.padded),
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
                            style: TextStyle(color: p.success, fontWeight: FontWeight.w700),
                          ),
                        ],
                      ),
                    ),
                  ),
                // 2026-10-26 (живой аудит, П3, С-11): пустой чек — значок, подсказка и кнопка «К товарам» вместо «0 позиций»
                if (cart.lines.isEmpty)
                  SliverToBoxAdapter(
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(32, 32, 32, 16),
                      child: Column(
                        children: [
                          Icon(Icons.shopping_cart_outlined, size: 52, color: p.textSoft),
                          const SizedBox(height: 12),
                          Text(l.cartEmptyHint, textAlign: TextAlign.center, style: TextStyle(color: p.textSoft, height: 1.35)),
                          const SizedBox(height: 16),
                          OutlinedButton(
                            style: OutlinedButton.styleFrom(minimumSize: const Size(160, 48)),
                            onPressed: () => setState(() => _showCheck = false),
                            child: Text(l.toProductsBtn),
                          ),
                        ],
                      ),
                    ),
                  ),
              ],
            ],
          ),
        ),
        Container(
          key: _payBarKey,
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

/// [code] — штрихкод, по которому нашли товар: если это штрихкод РАЗМЕРА, нужный размер сразу идёт в чек (2026-10-31, одежда).
Future<void> addProduct(BuildContext context, WidgetRef ref, Product p, {double? qty, String? code}) async {
  final l = context.l;
  if (!ref.read(shiftProvider).isOpen) {
    toast(context, l.openShiftFirst, error: true);
    return;
  }
  // 2026-10-31 (владелец: «почему продажа одежды в мобилке нету?»): товар с размерами и цветами — сначала выбор варианта
  if (!p.isWeight && !p.isService && await _addWithVariants(context, ref, p, qty: qty, code: code)) return;
  if (!context.mounted) return;
  var q = qty ?? 1;
  // 2026-10-27 (М4): у товара есть поштучная опция (packages) — сначала «Пачка / Штука», как PackageChoiceDialog кассы.
  // Без id упаковки на сервере штуку продать нельзя (sale_package_id) — такой товар продаётся целиком, как раньше.
  PiecePack? pack;
  var testerWarn = false;
  if (!p.isWeight) {
    final found = (await PackageStore.read(ref.read(databaseProvider)))[p.id];
    // 2026-10-27 (ревью М-6): флаг `pack_checkout_mode` = off — поштучная продажа выключена, товар идёт упаковкой целиком
    if (found?.id != null && await PieceSaleMode.isOn(ref.read(databaseProvider))) {
      pack = found;
      testerWarn = await TesterGate.isAllowed(ref.read(databaseProvider));
    }
  }
  if (pack != null && qty == null && context.mounted) {
    final piece = await askPackOrPiece(context, p, pack, testerWarn: testerWarn);
    if (piece == null) return;
    if (piece) {
      ref.read(cartProvider.notifier).add(p, qty: 1, piece: pack, packName: pack.label(l.pkWordPackage));
      return;
    }
  }
  if (!context.mounted) return;
  if (p.isWeight && qty == null) {
    // 2026-10-31 (режим «Продукты»): быстрый ввод веса — кнопки 0,25 / 0,5 / 1 / 2 / 5 и «Другой вес»
    final quick = ref.read(prefsProvider).shopChosen && ref.read(prefsProvider).shop == ShopMode.grocery;
    final kg = quick ? await askWeightQuick(context, p) : await askNumber(context, title: l.weightOf(p.name), hint: l.kg);
    if (kg == null || kg <= 0) return;
    q = kg;
  }
  ref.read(cartProvider.notifier).add(p, qty: q, packName: pack?.label(l.pkWordPackage));
  // 2026-10-31 (режим «Продукты»): срок годности близко (7 дней и меньше) — предупреждение при добавлении в чек
  final prefs = ref.read(prefsProvider);
  final d = daysToExpiry(p.expiresOn);
  if (prefs.shopChosen && prefs.shop == ShopMode.grocery && d != null && d <= 7 && context.mounted) {
    toast(context, l.expirySoonToast(p.name, expiryText(p.expiresOn!)), error: true);
  }
}

/// 2026-10-31 (режим «Продукты»): быстрый ввод веса весового товара — крупные кнопки и свой вес.
Future<double?> askWeightQuick(BuildContext context, Product p) {
  final l = context.l;
  return showModalBottomSheet<double>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (c) => SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 0, 20, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l.weightOf(p.name), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
            const SizedBox(height: 4),
            Text('${money(p.price)} ${l.som} / ${p.unit}', style: TextStyle(color: c.pal.textSoft)),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final w in const [0.25, 0.5, 1.0, 2.0, 5.0])
                  SizedBox(
                    width: 96,
                    height: 52,
                    child: FilledButton.tonal(
                      key: Key('quickWeight_$w'),
                      onPressed: () => Navigator.pop(c, w),
                      child: Text('${qtyText(w)} ${p.unit}', style: const TextStyle(fontWeight: FontWeight.w800)),
                    ),
                  ),
              ],
            ),
            const SizedBox(height: 12),
            OutlinedButton(
              key: const Key('quickWeightOther'),
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52)),
              onPressed: () async {
                final v = await askNumber(c, title: l.weightOf(p.name), hint: p.unit);
                if (c.mounted && v != null) Navigator.pop(c, v);
              },
              child: Text(l.kg == p.unit ? '${l.weightOf(p.name)}…' : '${p.unit}…'),
            ),
          ],
        ),
      ),
    ),
  );
}

/// Сколько штук каждого размера товара уже в чеке (id размера → штук).
Map<String, double> variantsInCart(Cart cart) {
  final m = <String, double>{};
  for (final ln in cart.lines) {
    final v = ln.variantId;
    if (v != null) m[v] = (m[v] ?? 0) + ln.qty;
  }
  return m;
}

/// 2026-10-31 (одежда): продажа товара с размерами и цветами. true — товар обработан (добавлен, отменён или сообщено, почему нельзя);
/// false — вариантов у товара нет, дальше идёт обычная продажа.
/// • штрихкод размера ([code]) кладёт этот размер в чек сразу, без окна;
/// • иначе окно «Размер и цвет» (сетка с остатками; остатки сразу с телефона, свежие с сервера подтягиваются в открытом окне);
/// • режим «Одежда» и размеры товара не загрузились без связи — продажа без размера не допускается;
///   в остальных режимах такой товар продаётся как раньше.
Future<bool> _addWithVariants(BuildContext context, WidgetRef ref, Product p, {double? qty, String? code}) async {
  final l = context.l;
  final db = ref.read(databaseProvider);
  final prefs = ref.read(prefsProvider);
  final hasAny = await db.hasAnyVariants();
  final cart = ref.read(cartProvider);
  // штрихкод размера этого товара — сразу в чек
  if (code != null) {
    final row = await db.variantByBarcode(code);
    if (row != null && row.productId == p.id) {
      if (!context.mounted) return true;
      return _addScannedVariant(context, ref, p, VariantService.fromRow(row), qty: qty);
    }
  }
  // Сервер не говорит, у каких товаров есть размеры, поэтому товар спрашивается при открытии. Режим выбран и это не «Одежда»,
  // размеров в магазине нет — не спрашиваем (хозяйственный магазин не ждёт лишний запрос).
  final discover = prefs.clothing || !prefs.shopChosen || hasAny;
  final svc = ref.read(variantServiceProvider);
  final set = await svc.forProduct(p.id, timeout: prefs.clothing ? VariantService.loadTimeout : const Duration(seconds: 2), allowServer: discover);
  if (!context.mounted) return true;
  if (set.unknown) {
    if (prefs.clothing) {
      toast(context, l.variantLoadFailed, error: true);
      return true;
    }
    return false;
  }
  if (!set.has) return false;
  if (set.active.isEmpty) {
    toast(context, l.variantPickAllOut, error: true);
    return true;
  }
  final pick = await pickVariant(context, p, set.active, inCart: variantsInCart(cart), refresh: set.refresh);
  if (pick == null) return true;
  ref.read(cartProvider.notifier).add(p, qty: pick.qty, variantId: pick.variant.id, variantLabel: pick.variant.lineLabel, variantPrice: pick.variant.price);
  return true;
}

/// Размер найден по штрихкоду: неактивный или закончившийся в чек не кладётся (понятное сообщение), иначе — в чек количеством скана.
bool _addScannedVariant(BuildContext context, WidgetRef ref, Product p, ProductVariant v, {double? qty}) {
  final l = context.l;
  final label = '${p.name} · ${v.lineLabel}';
  if (!v.active) {
    toast(context, l.variantScanInactive(label), error: true);
    return true;
  }
  final double want = qty == null || qty < 1 ? 1.0 : qty.roundToDouble();
  final avail = variantAvailable(v, variantsInCart(ref.read(cartProvider))[v.id] ?? 0);
  if (avail < 1) {
    toast(context, l.variantScanOut(label), error: true);
    return true;
  }
  if (want > avail) {
    toast(context, l.variantNoMore(qtyText(v.quantity)), error: true);
  }
  ref.read(cartProvider.notifier).add(p, qty: want > avail ? avail.floorToDouble() : want, variantId: v.id, variantLabel: v.lineLabel, variantPrice: v.price);
  return true;
}

/// 2026-10-27 (М4): «Как продать?» — две крупные кнопки с ценами: упаковка целиком или штука из неё (остаток в штуках — упаковки × штук).
/// true — штука, false — упаковка, null — закрыли.
Future<bool?> askPackOrPiece(BuildContext context, Product p, PiecePack pack, {bool testerWarn = false}) {
  final l = context.l;
  final packWord = pack.label(l.pkWordPackage);
  final pieces = stockInPieces(p.quantity, pack);
  Widget option(Key key, {required String title, required String price, required String sub, required bool piece, bool empty = false}) {
    final style = (piece ? OutlinedButton.styleFrom : FilledButton.styleFrom)(
      minimumSize: const Size.fromHeight(72),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
    );
    final content = Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
              Text(sub, style: TextStyle(fontSize: 12.5, color: empty ? T.danger : null)),
            ],
          ),
        ),
        const SizedBox(width: 8),
        Text(price, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
      ],
    );
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Semantics(
        button: true,
        label: '$title, $price, $sub',
        excludeSemantics: true,
        child: piece
            ? OutlinedButton(key: key, style: style, onPressed: () => Navigator.pop(context, true), child: content)
            : FilledButton(key: key, style: style, onPressed: () => Navigator.pop(context, false), child: content),
      ),
    );
  }

  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    builder: (c) => SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
        child: Column(
          key: const Key('packChoiceSheet'),
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(p.name, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
            Text(
              '${l.pkChoiceTitle} · ${l.pkChoiceStock(stockWithPack(qtyText, p.quantity, pack, packWord: l.pkPackShort, pcs: l.pkPcs))}',
              style: TextStyle(color: c.pal.textSoft),
            ),
            const SizedBox(height: 12),
            // 2026-10-27 (ревью М-6): в режиме тестировщика — прямое предупреждение, что путь ещё не подтверждён сервером
            if (testerWarn)
              Padding(
                padding: const EdgeInsets.only(bottom: 10),
                child: Text(l.pieceSaleTesterWarn, key: const Key('packTesterWarn'), style: TextStyle(color: c.pal.warn, fontSize: 12.5, fontWeight: FontWeight.w700)),
              ),
            option(
              const Key('packChoiceWhole'),
              title: packWord,
              price: '${money(p.price)} ${l.som}',
              sub: '${qtyText(pack.qty)} ${l.pkPcs} · ${p.quantity <= 0 ? l.stockNone : qtyText(p.quantity)}',
              piece: false,
              empty: p.quantity <= 0,
            ),
            option(
              const Key('packChoicePiece'),
              title: l.pkChoicePiece,
              price: '${money(pack.piecePrice)} ${l.som}',
              sub: pieces < 1 ? l.stockNone : '${qtyText(pieces.floorToDouble())} ${l.pkPcs}',
              piece: true,
              empty: pieces < 1,
            ),
          ],
        ),
      ),
    ),
  );
}

class _ProductTile extends StatelessWidget {
  final Product product;
  final VoidCallback onTap;

  /// Сколько этого товара уже в чеке (null — нет): значок «×2».
  final double? inCart;

  /// 2026-10-27 (М4): поштучная опция товара — остаток «12 пачек + 5 шт»
  final PiecePack? pack;

  /// 2026-10-31 (одежда): у товара есть размеры — сколько их и сколько штук всего (остаток плитки — сумма по размерам).
  final ({int count, double qty})? variants;

  /// 2026-10-31 (режим магазина): режим-специфичный значок на фото: продукты — «кг» у весового и «до 05.11», если срок близко;
  /// хозтовары — единица (м, м², упаковка), если не штуки. null — режим не выбран, значка нет.
  final ShopMode? mode;
  const _ProductTile(this.product, {required this.onTap, this.inCart, this.pack, this.variants, this.mode});

  /// (текст, опасный) — значок режима для товара или null.
  (String, bool)? _badge() {
    if (mode == ShopMode.grocery) {
      final d = daysToExpiry(product.expiresOn);
      if (d != null && d <= 14) return (d < 0 ? '!' : expiryShort(product.expiresOn!), d <= 7);
      if (product.isWeight) return (product.unit, false);
    }
    if (mode == ShopMode.hardware && !product.isService && !isPieceUnit(product.unit)) return (product.unit, false);
    return null;
  }
  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    final l = context.l;
    // 2026-10-26 (живой аудит, П3): мелко остаток («12 шт» / «нет» серым) и «×2», если товар уже в чеке
    final v = variants;
    final stock = product.isService
        ? null
        : v != null
        ? (v.qty <= 0 ? l.stockNone : '${qtyText(v.qty)} ${product.unit}')
        : (product.quantity <= 0
              ? l.stockNone
              : pack == null
              ? '${qtyText(product.quantity)} ${product.unit}'
              : stockWithPack(qtyText, product.quantity, pack, packWord: l.pkPackShort, pcs: l.pkPcs));
    return Semantics(
      label: inCart == null ? null : '${l.inCheck}: ${qtyText(inCart!)}',
      child: Pressable(
        onTap: onTap,
        child: Panel(
          padding: const EdgeInsets.all(6),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(
                child: Stack(
                  fit: StackFit.expand,
                  children: [
                    ProductThumb(name: product.name, url: product.imageUrl, fontSize: 18),
                    if (_badge() case (final text, final danger)?)
                      Positioned(
                        top: 4,
                        left: 4,
                        child: Container(
                          key: Key('modeBadge_${product.id}'),
                          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                          decoration: BoxDecoration(color: danger ? T.danger : mode!.accent, borderRadius: BorderRadius.circular(8)),
                          child: Text(text, style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 10.5)),
                        ),
                      ),
                    if (inCart != null)
                      Positioned(
                        top: 4,
                        right: 4,
                        child: Container(
                          padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                          decoration: BoxDecoration(color: T.accent, borderRadius: BorderRadius.circular(10)),
                          child: Text('×${qtyText(inCart!)}', style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 11.5)),
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
                textAlign: TextAlign.center,
                style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, height: 1.15),
              ),
              Text(
                '${money(product.price)} ${l.som}',
                textAlign: TextAlign.center,
                style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w800, color: p.price),
              ),
              if (stock != null)
                Text(
                  stock,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  textAlign: TextAlign.center,
                  style: TextStyle(fontSize: 10.5, color: p.textSoft.withValues(alpha: (v?.qty ?? product.quantity) <= 0 ? .7 : 1)),
                ),
              if (v != null)
                // 2026-10-31: подпись сжимается по ширине плитки, а не обрезается («3 размера/цвета» целиком)
                FittedBox(
                  fit: BoxFit.scaleDown,
                  child: Text(
                    l.variantsTile(v.count),
                    key: Key('variantsTile_${product.id}'),
                    maxLines: 1,
                    textAlign: TextAlign.center,
                    style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w700, color: p.price),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// 13.10, владелец: «сделай удобной корзину в мобилке». Строка чека — карточка: крупные «−»/«+» (48 точек), нажатие на
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

    // 2026-10-31 (одежда): остаток размера (по базе телефона, с учётом проданного без связи) — выше него строку не поднять
    Future<double?> variantCap() async {
      final id = line.variantId;
      if (id == null) return null;
      return (await ref.read(databaseProvider).variantById(id))?.quantity;
    }

    Future<void> editQty() async {
      final v = await askNumber(context, title: line.name, hint: line.isWeight ? line.unit : l.packQty, initial: line.qty);
      if (v == null) return;
      // 14.10, проверка логики: штучный товар — только целое (сервер принимает штуки целыми; 1,5 уходило как 2 и сумма
      // на телефоне расходилась с NurCRM).
      var q = line.isWeight ? v : v.roundToDouble();
      if (q > 0) {
        final cap = await variantCap();
        if (cap != null && q > cap) {
          if (context.mounted) toast(context, l.variantNoMore(qtyText(cap)), error: true);
          q = cap.floorToDouble();
        }
      }
      if (q <= 0) {
        if (context.mounted && await _removeLine(context, ref, index)) ctrl.remove(index);
      } else {
        ctrl.setQty(index, q);
      }
    }

    Future<void> plusOne() async {
      final cap = await variantCap();
      if (cap != null && line.qty + 1 > cap) {
        if (context.mounted) toast(context, l.variantNoMore(qtyText(cap)), error: true);
        return;
      }
      ctrl.setQty(index, line.qty + 1);
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
          padding: const EdgeInsets.fromLTRB(12, 8, 8, 8),
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
                            Text('${money(line.price)} ${l.som}${line.isWeight ? ' / ${line.unit}' : (line.isPiece ? ' / ${l.pkPcs}' : '')}', style: TextStyle(fontSize: 12.5, color: p.textSoft)),
                            // 2026-10-27 (М4): товар с упаковкой — подпись «Пачка» или «шт. из «Пачка»»
                            if (line.packName != null)
                              Container(
                                key: Key('packTag_${line.isPiece ? 'piece' : 'whole'}'),
                                padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                                decoration: BoxDecoration(color: p.border.withValues(alpha: 0.5), borderRadius: BorderRadius.circular(8)),
                                child: Text(line.isPiece ? l.pkLinePiece(line.packName!) : line.packName!, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: p.textSoft)),
                              ),
                            if (line.hasPromo)
                              Container(
                                padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                                decoration: BoxDecoration(color: p.success.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(8)),
                                child: Text(l.promoTag, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: p.success)),
                              ),
                            if (line.discount > 0)
                              Text(
                                '${l.discount} −${money(line.discount)}',
                                style: TextStyle(fontSize: 12.5, color: p.success, fontWeight: FontWeight.w700),
                              ),
                          ],
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  // 2026-10-26 (приёмка 1.18.6, С-6н): чип «опт» с областью касания 48 dp стоял в строке цены и раздувал её
                  // (строка чека была 175 dp, на экран влезало 2,5 строки). Теперь он под суммой справа — рядом с ценой места
                  // уже не занимает, хитбокс 48 dp остался.
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      Text(
                        '${money(line.total)} ${l.som}',
                        style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: p.price),
                      ),
                      // строка ↔ опт (только если у товара есть оптовая цена)
                      if (line.canWholesale)
                        InkWell(
                          onTap: () => ctrl.toggleWholesale(index),
                          borderRadius: BorderRadius.circular(8),
                          // 2026-10-26 (живой аудит, П3, Н-2): чип «опт» — область касания 48 dp (был 40×24)
                          child: Container(
                            constraints: const BoxConstraints(minHeight: 48, minWidth: 48),
                            alignment: Alignment.center,
                            color: Colors.transparent,
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
                        ),
                    ],
                  ),
                ],
              ),
              SizedBox(height: line.canWholesale ? 0 : 6),
              Row(
                children: [
                  // 2026-10-26 (живой аудит, П3, Н-2): при количестве 1 «−» неактивна (корзина справа одна, а не две)
                  _Step(
                    icon: Icons.remove,
                    onTap: line.isWeight ? editQty : (line.qty <= 1 ? null : () => ctrl.setQty(index, line.qty - 1)),
                  ),
                  Flexible(
                    child: InkWell(
                    onTap: editQty,
                    borderRadius: BorderRadius.circular(10),
                    child: Container(
                      constraints: const BoxConstraints(minWidth: 64),
                      height: 48,
                      margin: const EdgeInsets.symmetric(horizontal: 6),
                      padding: const EdgeInsets.symmetric(horizontal: 10),
                      alignment: Alignment.center,
                      decoration: BoxDecoration(color: p.window, borderRadius: BorderRadius.circular(10)),
                      child: AnimatedSwitcher(
                        duration: const Duration(milliseconds: 180),
                        transitionBuilder: (c, a) => ScaleTransition(scale: a, child: c),
                        child: FittedBox(
                          fit: BoxFit.scaleDown,
                          key: ValueKey(line.qty),
                          child: Text(
                            '${qtyText(line.qty)}${line.isWeight || (ref.watch(prefsProvider).shopChosen && ref.watch(prefsProvider).shop == ShopMode.hardware && !isPieceUnit(line.unit)) ? ' ${line.unit}' : ''}',
                            style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16),
                          ),
                        ),
                      ),
                    ),
                  ),
                  ),
                  _Step(icon: Icons.add, dark: true, onTap: () => line.isWeight ? editQty() : plusOne()),
                  const Spacer(),
                  IconButton(tooltip: l.lineDiscount, style: _rowBtn, onPressed: discount, icon: const Icon(Icons.percent_rounded)),
                  IconButton(
                    tooltip: l.deleteProduct,
                    style: _rowBtn,
                    color: p.danger,
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

/// 2026-10-26 (живой аудит, П3, Н-2): значки действий строки чека — касание 48 dp.
final _rowBtn = IconButton.styleFrom(fixedSize: const Size(48, 48));

class _Step extends StatelessWidget {
  final IconData icon;
  final VoidCallback? onTap; // null — кнопка неактивна
  final bool dark;
  const _Step({required this.icon, required this.onTap, this.dark = false});
  @override
  Widget build(BuildContext context) => Opacity(
    opacity: onTap == null ? .4 : 1,
    child: Material(
      color: dark ? context.pal.action : context.pal.panel,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: dark ? context.pal.action : context.pal.border),
      ),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: SizedBox(width: 48, height: 48, child: Icon(icon, size: 20, color: dark ? context.pal.onAction : null)),
      ),
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
