import 'dart:convert';

import 'package:dio/dio.dart' show DioMediaType, FormData, MultipartFile;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../../core/util/audit.dart';
import '../../l10n/app_localizations.dart';
import '../scan/barcode_lookup.dart';
import '../scan/code_lookup.dart';

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

  // 2026-10-22 (мобилка, админ, П8/П12): исход прошлой попытки — как PendingStockPatch и проверка «товар уже создан»
  // кассы (PurchaseReceivingService, BulkImportWindow). Строка живёт до успеха, повтор не задваивает.
  /// Создание товара осталось без ответа (обрыв, 5xx) — перед повтором ищем товар на сервере.
  bool createUnknown = false;

  /// PATCH остатка без ответа: остаток до прибавки и записанный остаток.
  double? pendingPre;
  double? pendingTarget;

  /// Строка проведена (остаток записан сервером).
  bool posted = false;
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

/// Количество — до 3 знаков (граммы).
double roundQty(double v) => (v * 1000).round() / 1000;

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
    bool isWeight = false,
    int? plu,
  }) async {
    final body = {
      'name': l.name.trim(),
      'unit': l.unit,
      // 2026-10-22 (мобилка, админ, П10): «кг» — весовой товар с PLU (как BuildProductBody кассы); раньше всегда штучный
      'is_weight': isWeight,
      if (isWeight && plu != null) 'plu': plu,
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

  /// 2026-10-22 (мобилка, админ, П10): PLU нового весового товара — как ProductEditDialog кассы и сайт: число весовых
  /// товаров + 1 (без связи — по базе телефона), занятые в каталоге номера пропускаются (иначе сервер отвечает 400
  /// «Этот PLU уже используется»).
  /// Вписанный PLU или следующий свободный.
  Future<int> nextPluOr(String typed) async {
    final n = int.tryParse(typed.trim());
    return n != null && n > 0 ? n : nextPlu();
  }

  Future<int> nextPlu() async {
    var count = -1;
    try {
      final data = await api.get('api/main/products/list/', query: {'is_weight': 'true', 'page': 1, 'page_size': 1});
      if (data is Map && data['count'] is num) count = (data['count'] as num).toInt();
    } on ApiException {
      // нет связи — по базе телефона
    }
    if (count < 0) {
      final r = await db.customSelect('SELECT COUNT(*) AS c FROM products WHERE is_weight = 1', readsFrom: {db.products}).getSingle();
      count = r.read<int>('c');
    }
    final used = {
      for (final r in await db.customSelect("SELECT plu FROM products WHERE plu IS NOT NULL AND plu <> ''", readsFrom: {db.products}).get())
        int.tryParse((r.data['plu'] ?? '').toString().trim()) ?? 0,
    };
    var n = count + 1;
    while (used.contains(n)) {
      n++;
    }
    return n;
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

  /// 2026-10-22 (мобилка, админ): ответ потерялся (обрыв, тайм-аут, 5xx, 408, 429) — операция могла пройти. Как
  /// PendingIdempotencyKey.IsOutcomeUnknown кассы.
  static bool outcomeUnknown(Object e) => e is! ApiException || e.status == null || e.status == 408 || e.status == 429 || e.status! >= 500;

  /// 2026-10-22 (мобилка, админ, П8): ключ повтора прихода от поставщика — как _pendingReceiptKey кассы. Пока исход
  /// прошлой попытки с тем же составом неизвестен, повтор идёт ТЕМ ЖЕ ключом (сервер, знающий поле, не проведёт приход
  /// второй раз). Хранится в базе телефона 24 часа — переживает закрытие экрана.
  static const pendingReceiptKeySetting = 'receive.pendingKey';

  static String receiptSignature(String supplierId, bool paidNow, List<ReceiveLine> ready) =>
      '$supplierId|$paidNow|${(ready.map((l) => '${l.productId}:${l.qty.toStringAsFixed(3)}:${l.cost.toStringAsFixed(2)}').toList()..sort()).join(';')}';

  Future<String> receiptKey(String signature) async {
    try {
      final raw = await db.getSetting(pendingReceiptKeySetting);
      if (raw != null && raw.isNotEmpty) {
        final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
        final at = DateTime.tryParse(m['at'] as String? ?? '');
        if (m['sig'] == signature && m['key'] is String && at != null && DateTime.now().difference(at) < const Duration(hours: 24)) {
          return m['key'] as String;
        }
      }
    } catch (_) {
      // испорченная запись — новый ключ
    }
    final key = const Uuid().v4();
    await db.setSetting(pendingReceiptKeySetting, jsonEncode({'key': key, 'sig': signature, 'at': DateTime.now().toIso8601String()}));
    return key;
  }

  /// 2026-10-22 (мобилка, админ, П8/П10/П12): товар, созданный прошлой попыткой без ответа, — ищем на сервере по
  /// штрихкоду, без штрихкода — по точному названию. null — не нашли (или нет связи).
  Future<String?> findCreated(String name, String barcode) async {
    final code = barcode.trim();
    final title = name.trim().toLowerCase();
    try {
      final data = await api.get('api/main/products/list/', query: {'search': code.isNotEmpty ? code : name.trim(), 'page': 1, 'page_size': 20});
      for (final p in asList(data)) {
        final id = str(p, 'id');
        if (id == null || id.isEmpty) continue;
        if (code.isNotEmpty ? (str(p, 'barcode') ?? '').trim() == code : (str(p, 'name') ?? '').trim().toLowerCase() == title) return id;
      }
    } on ApiException {
      // нет связи — не знаем
    }
    return null;
  }

  /// Провести приход. Возвращает (принято строк, ошибки). У проведённых строк — [ReceiveLine.posted].
  /// 2026-10-22 (мобилка, админ, П12): [setStock] — «Задать остаток» (как массовая загрузка кассы: остаток заменяется),
  /// иначе количество прибавляется. Только без поставщика.
  Future<(int, List<String>)> post(List<ReceiveLine> lines, String? supplierId, bool paidNow, {bool setStock = false, AppLocalizations? l10n}) async {
    final errors = <String>[];
    for (final l in lines.where((l) => l.productId.isEmpty)) {
      // 2026-10-22 (мобилка, админ): прошлое создание без ответа — товар мог появиться; второй не создаём
      if (l.createUnknown) {
        final found = await findCreated(l.name, l.barcode);
        if (found != null) {
          l.productId = found;
          l.createUnknown = false;
          continue;
        }
      }
      try {
        l.productId = await createProduct(l, category: l.category);
        l.createUnknown = false;
      } on ApiException catch (e) {
        if (outcomeUnknown(e)) l.createUnknown = true;
        errors.add('${l.name}: ${e.message}');
      }
    }
    final ready = lines.where((l) => l.productId.isNotEmpty && l.qty > 0 && !l.posted).toList();
    if (ready.isEmpty) return (0, errors);
    var posted = 0;
    // 14.10, проверка логики: остаток и цена на телефоне — только у проведённых строк (раньше и у тех, что сервер не принял).
    final done = <ReceiveLine>[];
    if (supplierId != null) {
      final key = await receiptKey(receiptSignature(supplierId, paidNow, ready));
      try {
        await api.post(
          'api/main/suppliers/$supplierId/receipt/',
          body: {
            'items': [
              for (final l in ready) {'product': l.productId, 'qty': l.qty, 'purchase_price': l.cost},
            ],
            'payment_type': paidNow ? 'cash' : 'debt',
            if (paidNow) 'cashbox_role': 'expense_variable',
            'idempotency_key': key,
          },
        );
      } on ApiException catch (e) {
        // отказ по существу — следующая попытка будет новым приходом; без ответа — ключ остаётся для повтора
        if (!outcomeUnknown(e)) await db.setSetting(pendingReceiptKeySetting, '');
        rethrow;
      }
      await db.setSetting(pendingReceiptKeySetting, '');
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
          // 2026-10-22 (мобилка, админ, П8/П12): как PendingStockPatch кассы — прошлый PATCH остатка мог дойти без ответа.
          // Остаток уже равен записанному — повторно не прибавляем; изменился иначе — не понять, прибавилось ли: не трогаем.
          var pre = stock;
          if (!setStock && l.pendingTarget != null && l.pendingPre != null) {
            if ((stock - l.pendingTarget!).abs() < 0.0005) {
              pre = l.pendingPre!;
            } else if ((stock - l.pendingPre!).abs() >= 0.0005) {
              errors.add(l10n?.pAStockUnclear(l.name, qtyText(stock), qtyText(l.pendingPre!)) ?? '${l.name}: ?');
              continue;
            }
          }
          final target = setStock ? roundQty(l.qty) : roundQty(pre + l.qty);
          final body = <String, Object>{'purchase_price': l.cost.toStringAsFixed(2), 'price': l.price.toStringAsFixed(2)};
          if ((stock - target).abs() >= 0.0005) {
            body['quantity'] = target;
            if (!setStock) {
              l.pendingPre = pre;
              l.pendingTarget = target;
            }
          }
          try {
            await api.patch('api/main/products/${l.productId}/', body: body);
          } on ApiException catch (e) {
            // отказ по существу — остаток не записан: помнить нечего (или только прошлую дошедшую прибавку)
            if (!outcomeUnknown(e) && body.containsKey('quantity') && !setStock) {
              if ((stock - pre).abs() < 0.0005) {
                l.pendingPre = l.pendingTarget = null;
              } else {
                l.pendingPre = pre;
                l.pendingTarget = stock;
              }
            }
            rethrow;
          }
          l.pendingPre = l.pendingTarget = null;
          // 2026-10-26 (П0, Б1): остаток записан — отметка сразу, а не в конце: если следующая строка оборвёт проведение
          // неожиданной ошибкой, повтор не прибавит этой строке остаток второй раз
          l.posted = true;
          posted++;
          done.add(l);
        } on ApiException catch (e) {
          errors.add('${l.name}: ${e.message}');
        }
      }
    }
    for (final l in done) {
      l.posted = true;
      if (supplierId == null && setStock) {
        await db.setStock(l.productId, roundQty(l.qty));
      } else {
        await db.adjustStock(l.productId, l.qty);
      }
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

  // 2026-10-22 (мобилка, админ, П8, К-9): поля цен — свои контроллеры у каждой строки. Раньше TextFormField(initialValue)
  // без ключа: новая строка вставлялась наверх, а поле оставалось от прежней — у товара Б показывалась закупка товара А.
  final _costCtl = <ReceiveLine, TextEditingController>{};
  final _priceCtl = <ReceiveLine, TextEditingController>{};

  /// 2026-10-22 (мобилка, админ, П8): скан обрабатывается по одному — пока ищется товар или открыто окно нового товара,
  /// следующие коды не берём (раньше окна открывались одно поверх другого и строки дублировались).
  bool _scanBusy = false;

  /// 2026-10-22 (мобилка, админ, П8): «Провести» нажато — второе нажатие не проводит приход второй раз.
  bool _posting = false;

  static String _numText(double v) => v == 0 ? '' : qtyText(v).replaceAll(RegExp('[ \u00A0\u202F]'), '');

  TextEditingController _ctl(Map<ReceiveLine, TextEditingController> m, ReceiveLine line, double v) =>
      m.putIfAbsent(line, () => TextEditingController(text: _numText(v)));

  void _removeLine(ReceiveLine line) {
    _lines.remove(line);
    _costCtl.remove(line)?.dispose();
    _priceCtl.remove(line)?.dispose();
  }

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
    for (final c in [..._costCtl.values, ..._priceCtl.values]) {
      c.dispose();
    }
    super.dispose();
  }

  double get _total => _lines.fold(0, (s, l) => s + l.qty * l.cost);

  /// [qty] — штрихкод упаковки: сколько штук (2026-10-22, П8: раньше всегда 1).
  Future<void> _addProduct(Product p, {double qty = 1}) async {
    final existing = _lines.where((l) => l.productId == p.id).firstOrNull;
    if (existing != null) {
      setState(() => existing.qty += qty);
      return;
    }
    var cost = 0.0;
    try {
      cost = num0(((await ref.read(apiProvider).get('api/main/products/${p.id}/')) as Map).cast<String, dynamic>(), 'purchase_price');
    } on ApiException {
      // без связи — закупку впишет администратор
    }
    if (!mounted) return;
    // 2026-10-22 (мобилка, админ, П8): пока ждали закупку, строку могли добавить ещё раз — не дублируем
    final again = _lines.where((l) => l.productId == p.id).firstOrNull;
    if (again != null) {
      setState(() => again.qty += qty);
      return;
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
          qty: qty,
        ),
      ),
    );
  }

  Future<void> _onCode(String code) async {
    final now = DateTime.now();
    if (_scanBusy) return;
    if (code == _last && now.difference(_lastAt).inMilliseconds < 1500) return;
    _last = code;
    _lastAt = now;
    _scanBusy = true;
    try {
      await _handleCode(code);
    } finally {
      _scanBusy = false;
      _lastAt = DateTime.now();
    }
  }

  /// 2026-10-22 (мобилка, админ, П8): товар по коду как у кассы — все написания кода, штрихкод упаковки (×N), сервер
  /// (товар, заведённый на ПК минуту назад, не создаётся вторым); этикетка весов в приёмку не берётся (вес одной покупки).
  Future<void> _handleCode(String code) async {
    final l = context.l;
    final db = ref.read(databaseProvider);
    final hit = await CodeLookup.find(db, ref.read(apiProvider), code, allowWeightLabel: false);
    if (!mounted) return;
    if (hit.product != null) {
      await _addProduct(hit.product!, qty: hit.qty ?? 1);
      return;
    }
    final clean = normalizeCode(code);
    final same = _lines.where((x) => x.isNew && x.barcode == clean).firstOrNull;
    if (same != null) {
      setState(() => same.qty += 1);
      return;
    }
    if (parseWeightLabel(clean, clothing: (await db.getSetting('shop.clothing')) == '1') != null) {
      if (mounted) toast(context, l.pAWeightLabel, error: true);
      return;
    }
    if (hit.unavailable) {
      if (mounted) toast(context, l.pANoServerCheck, error: true);
      return;
    }
    // окно нового товара — камера стоит, пока оно открыто
    await _camera?.stop();
    try {
      await _newProduct(clean);
    } finally {
      if (mounted && _scanning) await _camera?.start();
    }
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
        if (x.cost > 0 && (all || _costChanged(x))) {
          x.price = _rounded(x.cost);
          // 2026-10-22 (мобилка, админ, П8): поле показывает новую цену (раньше оставалась прежняя цифра)
          _priceCtl[x]?.text = _numText(x.price);
        }
      }
    });
  }

  Future<void> _post() async {
    if (_posting) return;
    setState(() => _posting = true);
    try {
      await _postOnce();
    } finally {
      _posting = false;
      if (mounted) setState(() {});
    }
  }

  Future<void> _postOnce() async {
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
      final total = _total;
      final (n, errors) = await ref.read(adminReceiveRepoProvider).post(_lines, _supplierId, _paidNow, l10n: l);
      Audit.log('receive', '${l.receiptPosted(n)}${errors.isEmpty ? '' : ' · ${errors.length} !'}', amount: total);
      if (!mounted) return;
      // 2026-10-22 (мобилка, админ, П8): проведённые строки уходят из списка, непринятые остаются — их можно поправить и
      // провести снова (раньше экран закрывался, и строки с ошибкой пропадали).
      setState(() {
        for (final x in _lines.where((x) => x.posted).toList()) {
          _removeLine(x);
        }
      });
      final left = _lines.length;
      toast(
        context,
        errors.isEmpty && left == 0
            ? l.receiptPosted(n)
            : '${l.receiptPosted(n)} ${errors.isEmpty ? '' : l.partlyFailed(errors.take(3).join('; '))}${left > 0 ? ' ${l.pALinesLeft(left)}' : ''}',
        error: errors.isNotEmpty || left > 0,
      );
      // 13.10, владелец: «после приёмки остаётся на этом экране» — приход проведён целиком: закрываем
      if (n > 0 && left == 0) Navigator.of(context).pop(true);
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
  Widget _numBox(String label, TextEditingController controller, void Function(double) onChanged) => Expanded(
    child: TextField(
      controller: controller,
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
                    // 2026-10-22 (мобилка, админ, П8): строка узнаётся по себе, а не по месту в списке
                    key: ObjectKey(line),
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
                              onPressed: () => setState(() => line.qty > 1 ? line.qty -= 1 : _removeLine(line)),
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
                            _numBox(l.purchasePrice, _ctl(_costCtl, line, line.cost), (v) => line.cost = v),
                            const SizedBox(width: 8),
                            _numBox(l.sellingPrice, _ctl(_priceCtl, line, line.price), (v) => line.price = v),
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
                      onPressed: _lines.isEmpty || _busy || _posting ? null : _post,
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
