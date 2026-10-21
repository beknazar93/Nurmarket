import 'package:dio/dio.dart' show DioMediaType, FormData, MultipartFile;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../../core/util/audit.dart';

/// Строка приёмки администратора: количество, закупка и продажа.
class ReceiveLine {
  String productId;
  final String name;
  final String barcode;
  final String unit;
  final double before;
  final double oldCost;
  final double oldPrice;
  double qty;
  double cost;
  double price;
  final bool isNew;

  /// 13.10: категория нового товара (массовая загрузка).
  final String category;
  ReceiveLine({
    required this.productId,
    required this.name,
    this.barcode = '',
    this.unit = 'шт',
    this.before = 0,
    this.oldCost = 0,
    this.oldPrice = 0,
    this.qty = 1,
    this.cost = 0,
    this.price = 0,
    this.isNew = false,
    this.category = '',
  });
  double get markup => cost > 0 ? (price - cost) / cost * 100 : 0;
}

/// 13.10, макеты ПР2, ПР4–ПР6: приёмка на телефоне администратора — тем же путём, что касса (PurchaseReceivingService):
/// с поставщиком — документ прихода POST api/main/suppliers/{id}/receipt/ (остаток прибавляет сервер), изменённая цена
/// продажи — PATCH товара; без поставщика — остаток, закупка и цена прямо в товар (свежий остаток с сервера + принятое).
class AdminReceiveRepository {
  final ApiClient api;
  final AppDatabase db;
  AdminReceiveRepository(this.api, this.db);

  Future<List<(String id, String name)>> suppliers() async {
    final data = await api.get('api/main/clients/', query: {'type': 'suppliers', 'page_size': 500});
    return [
      for (final m in asList(data))
        if ((str(m, 'id') ?? '').isNotEmpty) (str(m, 'id')!, str(m, 'full_name') ?? str(m, 'name') ?? str(m, 'llc') ?? str(m, 'phone') ?? ''),
    ];
  }

  /// 14.10: поставщики с телефоном — для «Заказа поставщику» (отправка в WhatsApp).
  Future<List<(String id, String name, String phone)>> supplierContacts() async {
    final data = await api.get('api/main/clients/', query: {'type': 'suppliers', 'page_size': 500});
    return [
      for (final m in asList(data))
        if ((str(m, 'id') ?? '').isNotEmpty)
          (str(m, 'id')!, str(m, 'full_name') ?? str(m, 'name') ?? str(m, 'llc') ?? str(m, 'phone') ?? '', str(m, 'phone') ?? ''),
    ];
  }

  /// 14.10: товары, которые приходовали от этого поставщика (приходы suppliers/receipts/ — с сайта и с касс), как
  /// LoadSupplierProductsAsync кассы: поставщик — по id, у старых приходов без id — по названию.
  Future<Set<String>> supplierProductIds(String? supplierId, String supplierName) async {
    final ids = <String>{};
    final name = supplierName.trim().toLowerCase();
    for (var page = 1; page <= 5; page++) {
      final data = await api.get('api/main/suppliers/receipts/', query: {'page': page, 'limit': 100});
      for (final r in asList(data)) {
        final sid = str(r, 'supplier') ?? str(r, 'supplier_id');
        final sname = (str(r, 'supplier_name') ?? '').trim().toLowerCase();
        final ours = (sid != null && sid == supplierId) || (sid == null || supplierId == null) && sname.isNotEmpty && sname == name;
        final items = r['items'];
        if (!ours || items is! List) continue;
        for (final it in items) {
          if (it is Map) {
            final pid = str(it.cast<String, dynamic>(), 'product_id') ?? str(it.cast<String, dynamic>(), 'product');
            if (pid != null) ids.add(pid);
          }
        }
      }
      if (data is! Map || data['next'] == null) break;
    }
    return ids;
  }

  /// Название из общей базы NurCRM по штрихкоду (как «новый товар» на кассе); null — не нашли.
  Future<String?> globalName(String barcode) async {
    try {
      final data = await api.get('api/main/products/global-barcode/${Uri.encodeComponent(barcode)}/');
      if (data is Map) {
        final m = (data['product'] is Map ? data['product'] as Map : data).cast<String, dynamic>();
        return str(m, 'name');
      }
    } on ApiException {
      // нет в общей базе
    }
    return null;
  }

  /// 13.10, макет Н4: из общей базы NurCRM — название, категория, бренд (как карточка товара кассы); null — не нашли.
  Future<({String name, String? category, String? brand})?> globalInfo(String barcode) async {
    try {
      final data = await api.get('api/main/products/global-barcode/${Uri.encodeComponent(barcode)}/');
      if (data is Map) {
        final m = (data['product'] is Map ? data['product'] as Map : data).cast<String, dynamic>();
        final name = str(m, 'name');
        if (name == null) return null;
        String? text(String k) => m[k] is Map ? str((m[k] as Map).cast<String, dynamic>(), 'name') : str(m, k);
        return (name: name, category: text('category'), brand: text('brand'));
      }
    } on ApiException {
      // нет в общей базе
    }
    return null;
  }

  /// kind — product | service (13.10, макет Н8: услуга без остатка); category — название категории.
  ///
  /// 13.10, владелец: «создание нового товара — поставщик, оптовая и розничная цена, доп. штрихкоды, фото».
  /// wholesale — оптовая цена; altBarcodes — доп. штрихкоды как у кассы (BuildAlternateBarcodes): {barcode, name, quantity}
  /// — штрихкод упаковки: скан добавляет quantity штук.
  Future<String> createProduct(
    ReceiveLine l, {
    String kind = 'product',
    String? category,
    String? brand,
    double? wholesale,
    List<Map<String, Object>>? altBarcodes,
    String? description,
  }) async {
    final body = {
      'name': l.name.trim(),
      'unit': l.unit,
      'is_weight': false,
      'quantity': 0,
      'kind': kind,
      if (category != null && category.trim().isNotEmpty) 'category_name': category.trim(),
      if (brand != null && brand.trim().isNotEmpty) 'brand_name': brand.trim(),
      if (wholesale != null && wholesale > 0) 'wholesale_price': wholesale.toStringAsFixed(2),
      if (altBarcodes != null && altBarcodes.isNotEmpty) 'alternate_barcodes': altBarcodes,
      if (description != null && description.trim().isNotEmpty) 'description': description.trim(),
      'barcode': l.barcode.isEmpty ? null : l.barcode,
      'purchase_price': l.cost.toStringAsFixed(2),
      'price': l.price.toStringAsFixed(2),
      'stock': false,
      'promotion_rules_input': <Object>[],
    };
    ApiException? last;
    for (final path in ['api/main/products/create-manual/', 'api/main/products/list/', 'api/main/products/']) {
      try {
        final r = await api.post(path, body: body);
        final m = r is Map ? r.cast<String, dynamic>() : <String, dynamic>{};
        final id = str(m, 'id') ?? (m['product'] is Map ? (m['product'] as Map)['id']?.toString() : null);
        if (id != null && id.isNotEmpty) return id;
      } on ApiException catch (e) {
        if (e.status != 404 && e.status != 405) rethrow;
        last = e;
      }
    }
    throw last ?? ApiException('Товар не создан');
  }

  /// Фото в карточку товара — POST products/{id}/images/ (поле image, is_primary), как UploadProductImageAsync кассы.
  Future<void> uploadImage(String productId, List<int> bytes, {bool primary = false}) async {
    FormData form() => FormData.fromMap({
      'image': MultipartFile.fromBytes(bytes, filename: 'photo.jpg', contentType: DioMediaType('image', 'jpeg')),
      'is_primary': primary ? 'true' : 'false',
    });
    try {
      await api.post('api/main/products/$productId/images/', body: form(), timeout: const Duration(seconds: 60));
    } on ApiException catch (e) {
      // после обновления входа тело запроса нельзя отправить второй раз — собираем заново
      if (e.status == 401) {
        await api.post('api/main/products/$productId/images/', body: form(), timeout: const Duration(seconds: 60));
      } else {
        rethrow;
      }
    }
  }

  /// Провести приход. Возвращает (принято строк, ошибки).
  Future<(int, List<String>)> post(List<ReceiveLine> lines, String? supplierId, bool paidNow) async {
    final errors = <String>[];
    for (final l in lines.where((l) => l.productId.isEmpty)) {
      try {
        l.productId = await createProduct(l, category: l.category);
      } on ApiException catch (e) {
        errors.add('${l.name}: ${e.message}');
      }
    }
    final ready = lines.where((l) => l.productId.isNotEmpty && l.qty > 0).toList();
    if (ready.isEmpty) return (0, errors);
    var posted = 0;
    // 14.10, проверка логики: остаток и цена на телефоне — только у проведённых строк (раньше и у тех, что сервер не принял).
    final done = <ReceiveLine>[];
    if (supplierId != null) {
      await api.post(
        'api/main/suppliers/$supplierId/receipt/',
        body: {
          'items': [
            for (final l in ready) {'product': l.productId, 'qty': l.qty, 'purchase_price': l.cost},
          ],
          'payment_type': paidNow ? 'cash' : 'debt',
          if (paidNow) 'cashbox_role': 'expense_variable',
        },
      );
      posted = ready.length;
      done.addAll(ready);
      for (final l in ready.where((l) => !l.isNew && (l.price - l.oldPrice).abs() > 0.004)) {
        try {
          await api.patch('api/main/products/${l.productId}/', body: {'price': l.price.toStringAsFixed(2)});
        } on ApiException catch (e) {
          errors.add('${l.name}: ${e.message}');
        }
      }
    } else {
      for (final l in ready) {
        try {
          final current = ((await api.get('api/main/products/${l.productId}/')) as Map).cast<String, dynamic>();
          final stock = num0(current, 'quantity');
          await api.patch(
            'api/main/products/${l.productId}/',
            body: {'quantity': ((stock + l.qty) * 1000).round() / 1000, 'purchase_price': l.cost.toStringAsFixed(2), 'price': l.price.toStringAsFixed(2)},
          );
          posted++;
          done.add(l);
        } on ApiException catch (e) {
          errors.add('${l.name}: ${e.message}');
        }
      }
    }
    for (final l in done) {
      await db.adjustStock(l.productId, l.qty);
      await db.customStatement('UPDATE products SET price = ? WHERE id = ?', [l.price, l.productId]);
    }
    return (posted, errors);
  }
}

final adminReceiveRepoProvider = Provider((ref) => AdminReceiveRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));

class AdminReceiveScreen extends ConsumerStatefulWidget {
  /// 13.10, Т10: строки из умной камеры (полка, накладная, этикетки) — приёмка открывается уже заполненной.
  final List<ReceiveLine>? initialLines;
  const AdminReceiveScreen({super.key, this.initialLines});
  @override
  ConsumerState<AdminReceiveScreen> createState() => _AdminReceiveScreenState();
}

class _AdminReceiveScreenState extends ConsumerState<AdminReceiveScreen> {
  late final List<ReceiveLine> _lines = [...?widget.initialLines];
  List<(String, String)> _suppliers = [];
  String? _supplierId;
  bool _paidNow = false;
  bool _scanning = false;
  bool _busy = false;
  final _search = TextEditingController();
  MobileScannerController? _camera;
  String _last = '';
  DateTime _lastAt = DateTime(2000);

  @override
  void initState() {
    super.initState();
    ref
        .read(adminReceiveRepoProvider)
        .suppliers()
        .then((s) {
          if (mounted) setState(() => _suppliers = s);
        })
        .catchError((_) {});
  }

  @override
  void dispose() {
    _camera?.dispose();
    super.dispose();
  }

  double get _total => _lines.fold(0, (s, l) => s + l.qty * l.cost);

  Future<void> _addProduct(Product p) async {
    final existing = _lines.where((l) => l.productId == p.id).firstOrNull;
    if (existing != null) {
      setState(() => existing.qty += 1);
      return;
    }
    var cost = 0.0;
    try {
      cost = num0(((await ref.read(apiProvider).get('api/main/products/${p.id}/')) as Map).cast<String, dynamic>(), 'purchase_price');
    } on ApiException {
      // без связи — закупку впишет администратор
    }
    setState(
      () => _lines.insert(
        0,
        ReceiveLine(
          productId: p.id,
          name: p.name,
          barcode: p.barcode ?? '',
          unit: p.unit,
          before: p.quantity,
          oldCost: cost,
          oldPrice: p.price,
          cost: cost,
          price: p.price,
        ),
      ),
    );
  }

  Future<void> _onCode(String code) async {
    final now = DateTime.now();
    if (code == _last && now.difference(_lastAt).inMilliseconds < 1500) return;
    _last = code;
    _lastAt = now;
    final p = await ref.read(databaseProvider).productByBarcode(code);
    if (p != null) {
      await _addProduct(p);
      return;
    }
    final same = _lines.where((l) => l.isNew && l.barcode == code).firstOrNull;
    if (same != null) {
      setState(() => same.qty += 1);
      return;
    }
    await _newProduct(code);
  }

  /// ПР4: новый товар прямо из приёмки — название из общей базы по штрихкоду, закупка, продажа +30 %.
  Future<void> _newProduct(String code) async {
    final l = context.l;
    final found = await ref.read(adminReceiveRepoProvider).globalName(code);
    if (!mounted) return;
    final name = TextEditingController(text: found ?? '');
    final cost = TextEditingController();
    final price = TextEditingController();
    final qty = TextEditingController(text: '1');
    var unit = 'шт';
    final ok = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => Padding(
          padding: EdgeInsets.fromLTRB(20, 20, 20, MediaQuery.viewInsetsOf(c).bottom + 20),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l.newProduct, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                Text(l.barcodeNotInStock(code), style: TextStyle(color: c.pal.textSoft, fontSize: 12.5)),
                if (found != null)
                  Text(
                    l.foundInBase,
                    style: const TextStyle(color: T.success, fontSize: 12.5, fontWeight: FontWeight.w700),
                  ),
                const SizedBox(height: 10),
                TextField(
                  controller: name,
                  decoration: InputDecoration(labelText: l.clientName),
                ),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: cost,
                        keyboardType: const TextInputType.numberWithOptions(decimal: true),
                        decoration: InputDecoration(labelText: l.purchasePrice),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: TextField(
                        controller: price,
                        keyboardType: const TextInputType.numberWithOptions(decimal: true),
                        decoration: InputDecoration(labelText: l.salePrice),
                      ),
                    ),
                    const SizedBox(width: 8),
                    SizedBox(
                      width: 70,
                      child: TextField(
                        controller: qty,
                        keyboardType: const TextInputType.numberWithOptions(decimal: true),
                        decoration: const InputDecoration(labelText: '×'),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                // 13.10: наценка процентом и суммой.
                MarginFields(cost: cost, price: price),
                const SizedBox(height: 8),
                Wrap(
                  spacing: 6,
                  children: [
                    for (final u in ['шт', 'кг', 'л', 'упак']) ChoiceChip(label: Text(u), selected: unit == u, onSelected: (_) => set(() => unit = u)),
                  ],
                ),
                const SizedBox(height: 12),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: () => Navigator.pop(c, name.text.trim().isNotEmpty && (parseNum(cost.text) ?? 0) > 0),
                  child: Text(l.createAndAdd),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    if (ok != true) return;
    final c0 = parseNum(cost.text) ?? 0;
    setState(
      () => _lines.insert(
        0,
        ReceiveLine(
          productId: '',
          name: name.text.trim(),
          barcode: code,
          unit: unit,
          cost: c0,
          price: parseNum(price.text) ?? ((c0 * 1.3 / 5).ceil() * 5).toDouble(),
          qty: parseNum(qty.text) ?? 1,
          isNew: true,
        ),
      ),
    );
  }

  /// ПР5: цены продажи по правилу — +30 % к закупке, округление до 5 сом (только где закупка изменилась или цены нет).
  /// 13.10, макет ПР5 «Цены продажи»: правило (+N % к закупке), округление, к изменённым или ко всем строкам;
  /// видно, у кого изменилась закупка и какая наценка выйдет. Цены уходят на сервер при проведении прихода.
  int _rulePct = 30;
  int _roundTo = 5;

  bool _costChanged(ReceiveLine x) => x.isNew || (x.cost - x.oldCost).abs() > 0.004 || x.price <= 0;

  double _rounded(double cost) =>
      _roundTo <= 1 ? (cost * (1 + _rulePct / 100) * 100).roundToDouble() / 100 : ((cost * (1 + _rulePct / 100) / _roundTo).ceil() * _roundTo).toDouble();

  Future<void> _applyRule() async {
    final l = context.l;
    var all = false;
    final changed = _lines.where((x) => x.cost > 0 && _costChanged(x)).length;
    final fresh = _lines.where((x) => x.isNew).length;
    final ok = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) {
          final targets = _lines.where((x) => x.cost > 0 && (all || _costChanged(x))).toList();
          return SafeArea(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(l.pricesTitle, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
                  Text(l.pricesSub(changed, fresh), style: TextStyle(color: c.pal.textSoft)),
                  FieldLabel(l.ruleLabel),
                  Wrap(
                    spacing: 6,
                    children: [
                      for (final p in [20, 25, 30, 40, 50])
                        ChoiceChip(label: Text('+$p %'), selected: _rulePct == p, onSelected: (_) => set(() => _rulePct = p)),
                    ],
                  ),
                  FieldLabel(l.roundTo),
                  Wrap(
                    spacing: 6,
                    children: [
                      for (final r in [1, 5, 10])
                        ChoiceChip(label: Text(r == 1 ? '0,01' : '$r'), selected: _roundTo == r, onSelected: (_) => set(() => _roundTo = r)),
                    ],
                  ),
                  CheckboxListTile(contentPadding: EdgeInsets.zero, value: all, onChanged: (v) => set(() => all = v ?? false), title: Text(l.applyToAll)),
                  ConstrainedBox(
                    constraints: const BoxConstraints(maxHeight: 280),
                    child: ListView(
                      shrinkWrap: true,
                      children: [
                        for (final x in targets)
                          ListTile(
                            dense: true,
                            contentPadding: EdgeInsets.zero,
                            title: Text(x.name),
                            subtitle: Text(x.isNew ? l.newItemShort : l.costWas(money(x.oldCost), money(x.cost))),
                            trailing: Text('${money(x.price)} → ${money(_rounded(x.cost))}', style: const TextStyle(fontWeight: FontWeight.w800)),
                          ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 8),
                  FilledButton(
                    style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                    onPressed: targets.isEmpty ? null : () => Navigator.pop(c, true),
                    child: Text(l.applyPrices),
                  ),
                ],
              ),
            ),
          );
        },
      ),
    );
    if (ok != true || !mounted) return;
    setState(() {
      for (final x in _lines) {
        if (x.cost > 0 && (all || _costChanged(x))) x.price = _rounded(x.cost);
      }
    });
  }

  Future<void> _post() async {
    final l = context.l;
    // 13.10, макет ПР6: итог перед проведением — позиции, штуки, новые товары, изменённые цены, сумма закупки.
    final qty = _lines.fold(0.0, (s, x) => s + x.qty);
    final priceChanges = _lines.where((x) => !x.isNew && (x.price - x.oldPrice).abs() > 0.004).length;
    final confirm = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.postReceipt),
        content: Text(l.postSummary(_lines.length, qtyText(qty), _lines.where((x) => x.isNew).length, priceChanges, money(_total))),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.postReceipt)),
        ],
      ),
    );
    if (confirm != true || !mounted) return;
    setState(() => _busy = true);
    try {
      final (n, errors) = await ref.read(adminReceiveRepoProvider).post(_lines, _supplierId, _paidNow);
      Audit.log('receive', '${l.receiptPosted(n)}${errors.isEmpty ? '' : ' · ${errors.length} !'}', amount: _total);
      if (!mounted) return;
      toast(context, errors.isEmpty ? l.receiptPosted(n) : '${l.receiptPosted(n)} ${l.partlyFailed(errors.take(3).join('; '))}', error: errors.isNotEmpty);
      // 13.10, владелец: «после приёмки остаётся на этом экране» — приход проведён (хотя бы частично): закрываем
      if (n > 0) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _searchAdd() async {
    final q = _search.text.trim();
    // 14.10: пустой поиск или несколько совпадений — общий выбор товара с фильтрами (остаток, категория).
    final rows = q.isEmpty ? <Product>[] : await ref.read(databaseProvider).searchProducts(q, limit: 20);
    if (!mounted) return;
    if (rows.length == 1) {
      await _addProduct(rows.first);
      _search.clear();
      return;
    }
    final p = await pickProduct(context, noServices: true, initialQuery: q);
    if (p != null) {
      await _addProduct(p);
      _search.clear();
    }
  }

  // 13.10, владелец: подписи обрезались («Заку…», «Цена…») — поля делят строку поровну, подпись видна целиком.
  Widget _numBox(String label, double value, void Function(double) onChanged) => Expanded(
    child: TextFormField(
      initialValue: value == 0 ? '' : qtyText(value).replaceAll(' ', '').replaceAll(' ', ''),
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      decoration: InputDecoration(labelText: label, isDense: true),
      onChanged: (v) => setState(() => onChanged(parseNum(v) ?? 0)),
    ),
  );

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    // 14.10, владелец: случайное «назад» с непроведённой приёмкой — спросить.
    return LeaveGuard(
      ask: () => _lines.isNotEmpty && !_busy,
      child: Scaffold(
        appBar: ScreenTitle(l.receiveWithPrices, subtitle: l.receiveWithPricesSub),
        body: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 6),
              child: DropdownButtonFormField<String?>(
                initialValue: _supplierId,
                isExpanded: true,
                decoration: InputDecoration(labelText: l.supplierName),
                items: [
                  DropdownMenuItem<String?>(value: null, child: Text(l.noSupplier)),
                  for (final (id, name) in _suppliers)
                    DropdownMenuItem<String?>(
                      value: id,
                      child: Text(name, overflow: TextOverflow.ellipsis),
                    ),
                ],
                onChanged: (v) => setState(() => _supplierId = v),
              ),
            ),
            if (_scanning)
              SizedBox(
                height: 180,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(16),
                    child: MobileScanner(
                      controller: _camera ??= MobileScannerController(detectionSpeed: DetectionSpeed.normal),
                      onDetect: (cap) {
                        final v = cap.barcodes.firstOrNull?.rawValue;
                        if (v != null && v.isNotEmpty) _onCode(v);
                      },
                    ),
                  ),
                ),
              ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 6, 16, 6),
              child: Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _search,
                      decoration: InputDecoration(hintText: l.nameOrBarcode, prefixIcon: const Icon(Icons.search_rounded), isDense: true),
                      onSubmitted: (_) => _searchAdd(),
                    ),
                  ),
                  const SizedBox(width: 8),
                  IconButton.filled(
                    onPressed: () => setState(() => _scanning = !_scanning),
                    icon: Icon(_scanning ? Icons.videocam_off_rounded : Icons.qr_code_scanner_rounded),
                    tooltip: l.scanProduct,
                  ),
                ],
              ),
            ),
            Expanded(
              child: ListView.separated(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
                itemCount: _lines.length,
                separatorBuilder: (_, _) => const SizedBox(height: 8),
                itemBuilder: (c, i) {
                  final line = _lines[i];
                  final costUp = !line.isNew && line.oldCost > 0 && (line.cost - line.oldCost).abs() > 0.004;
                  return Panel(
                    padding: const EdgeInsets.fromLTRB(12, 10, 8, 10),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Row(
                          children: [
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(line.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                                  Text(
                                    line.isNew
                                        ? l.newProduct
                                        : costUp
                                        ? '${l.purchasePrice} ${money(line.oldCost)} → ${money(line.cost)}'
                                        : '${l.onStockLabel}: ${qtyText(line.before)} ${line.unit}',
                                    style: TextStyle(fontSize: 12, color: line.isNew || costUp ? T.warn : c.pal.textSoft),
                                  ),
                                ],
                              ),
                            ),
                            IconButton(
                              onPressed: () => setState(() => line.qty > 1 ? line.qty -= 1 : _lines.removeAt(i)),
                              icon: const Icon(Icons.remove_circle_outline_rounded),
                            ),
                            // 13.10, владелец: «количество не вводится» — нажатие на число: ввод количества
                            InkWell(
                              onTap: () async {
                                final v = await askNumber(context, title: line.name, initial: line.qty);
                                if (v != null && v > 0) setState(() => line.qty = v);
                              },
                              borderRadius: BorderRadius.circular(8),
                              child: Container(
                                constraints: const BoxConstraints(minWidth: 48),
                                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
                                decoration: BoxDecoration(color: context.pal.window, borderRadius: BorderRadius.circular(8)),
                                child: Text(
                                  qtyText(line.qty),
                                  textAlign: TextAlign.center,
                                  style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16),
                                ),
                              ),
                            ),
                            IconButton(
                              onPressed: () => setState(() => line.qty += 1),
                              icon: Icon(Icons.add_circle_rounded, color: context.pal.action),
                            ),
                          ],
                        ),
                        Row(
                          children: [
                            _numBox(l.purchasePrice, line.cost, (v) => line.cost = v),
                            const SizedBox(width: 8),
                            _numBox(l.sellingPrice, line.price, (v) => line.price = v),
                            const SizedBox(width: 8),
                            if (line.cost > 0)
                              Text(
                                '${line.markup >= 0 ? '+' : ''}${line.markup.toStringAsFixed(0)} %',
                                style: TextStyle(fontWeight: FontWeight.w800, color: line.price < line.cost ? T.danger : T.success),
                              ),
                          ],
                        ),
                      ],
                    ),
                  );
                },
              ),
            ),
            SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    TextButton.icon(onPressed: _lines.isEmpty ? null : _applyRule, icon: const Icon(Icons.auto_fix_high_rounded), label: Text(l.pricesTitle)),
                    if (_supplierId != null)
                      SegmentedButton<bool>(
                        segments: [
                          ButtonSegment(value: false, label: Text(l.onCredit)),
                          ButtonSegment(value: true, label: Text(l.payNowCash)),
                        ],
                        selected: {_paidNow},
                        onSelectionChanged: (s) => setState(() => _paidNow = s.first),
                      ),
                    const SizedBox(height: 8),
                    FilledButton(
                      style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(56)),
                      onPressed: _lines.isEmpty || _busy ? null : _post,
                      child: Text('${l.postReceipt} · ${l.purchaseSum(money(_total))}'),
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
