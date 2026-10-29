import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/ai/assistant/ai_handles.dart';
import '../../core/ai/assistant/screen_registry.dart' show foldText;
import '../../core/ai/assistant/tool_registry.dart' show ToolStatus;
import '../../core/ai/vision.dart';
import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/units.dart';
import '../../core/util/barcode_generator.dart';
import '../../core/util/photo_finder.dart';
import '../../core/util/ui.dart';
import '../scan/code_lookup.dart';
import '../scan/scan_screen.dart';
import '../scan/smart_camera.dart';
import '../sale/cart.dart';
import '../stock/barcode_label.dart';
import '../stock/product_edit_repository.dart' show isWeightUnit;
import '../stock/product_packages.dart';
import '../stock/unit_picker.dart';
import 'admin_receive.dart';
import '../../core/util/audit.dart';
import '../../core/ai/product_refs.dart';

/// 13.10, макеты Н3, Н4, Н7, Н8 (владелец: «сверься с макетом и доделывай»): новая позиция с телефона — товар, одежда
/// (размеры × цвета, у каждого варианта свой остаток — api/main/products/{id}/variants/, как карточка кассы), услуга
/// (kind=service, без остатка). «Найти в интернете» — общая база NurCRM по штрихкоду (название, категория, бренд) и поиск
/// в браузере; цену из интернета не берём. 2026-10-27: мастера, длительность и запись у услуги убраны (владелец) — услуга создаётся как обычная позиция без остатка.
class NewItemScreen extends ConsumerStatefulWidget {
  final String kind;

  /// 13.10: штрихкод из сканера («товар не найден → добавить») — подставляется и ищется в общей базе NurCRM.
  final String? barcode;
  const NewItemScreen({super.key, this.kind = 'product', this.barcode});
  @override
  ConsumerState<NewItemScreen> createState() => _NewItemScreenState();
}

class _NewItemScreenState extends ConsumerState<NewItemScreen> {
  late String _kind = widget.kind;
  final _barcode = TextEditingController();
  final _name = TextEditingController();
  final _category = TextEditingController();
  final _brand = TextEditingController();
  final _cost = TextEditingController();
  final _price = TextEditingController();
  final _qty = TextEditingController(text: '0');
  String _unit = 'шт';
  bool _submitted = false; // 2026-10-29: после первого «Сохранить» пустые обязательные поля подсвечиваются

  // 2026-10-27 (М4): «Продавать поштучно из упаковки» — цена упаковки = розничная цена, закупка упаковки = закупка
  late final PackFormController _pack = PackFormController(price: _price, cost: _cost);

  // 13.10, владелец: «добавь поставщиков, оптовую и розничную цену, скидку от поставщиков, доп. штрихкоды, фото».
  final _wholesale = TextEditingController();
  final _description = TextEditingController();
  bool _describing = false;
  final _supplierDiscount = TextEditingController();
  List<(String, String)> _suppliers = [];
  String? _supplierId;
  bool _paidNow = false;
  final List<({TextEditingController code, TextEditingController qty, TextEditingController name})> _altCodes = [];
  final List<Uint8List> _photos = [];
  // 14.10, владелец: 3 снимка для умной камеры (справа, слева, сверху) — эталоны точного поиска при продаже.
  List<Uint8List> _camShots = [];
  bool _scanning = false;
  bool _found = false;
  bool _busy = false;
  MobileScannerController? _camera;

  // одежда (Н7)
  // 14.10, владелец: «размеры не редактируются» — к стандартным можно добавить свои (42, 44, 110 см…).
  final List<String> _allSizes = ['XS', 'S', 'M', 'L', 'XL', 'XXL', '3XL'];
  static const _baseSizes = ['XS', 'S', 'M', 'L', 'XL', 'XXL', '3XL'];
  final List<String> _sizes = ['S', 'M', 'L', 'XL'];
  final List<String> _colors = [];
  final Map<String, TextEditingController> _cells = {};

  // 2026-10-22 (мобилка, админ, П10): что уже сделано на сервере — повтор после частичной ошибки продолжает с того же
  // места. Раньше id созданного товара не запоминался: второе «Сохранить» создавало второй товар.
  String? _createdId;

  /// 2026-10-26 (живой аудит, П7, С-12): код, выданный «Сгенерировать», — после сохранения спросим «Напечатать этикетку?».
  String? _generatedCode;

  /// Создание осталось без ответа (обрыв, 5xx) — перед повтором ищем товар на сервере.
  bool _createUnknown = false;
  final Set<int> _photosDone = {};
  bool _teachDone = false;
  bool _stockDone = false;
  final Set<String> _variantsDone = {};

  /// 2026-10-22 (мобилка, админ, П10): PLU весового товара («кг»); пусто — назначается сам (число весовых + 1).
  final _plu = TextEditingController();

  // 2026-10-26 (голос-управление, В3б): «ручка» для Нур Советника — вписывает название, цену, закупку, штрихкод, единицу и
  // категорию и нажимает ТУ ЖЕ `_save` (проверка «уже есть», создание один раз, поиск после обрыва — прежние). Итог — `_aiSink`.
  final _aiSink = OutcomeSink();
  late final AiScreenHandles _aiHandles = ref.read(aiHandlesProvider);
  late final AiHandle _aiHandle = AiHandle(fill: _aiFill, run: () => _aiSink.run(_save));

  /// Единица из речи → одна из кнопок экрана (каталог units.dart: шт, кг, г, л, мл, м, упак…); null — не поняла.
  static String? _aiUnit(String raw) {
    final u = foldText(raw).trim();
    if (u.isEmpty) return 'шт';
    return unitOf(u)?.code;
  }

  Future<HandleOutcome?> _aiFill(Map<String, dynamic> a) async {
    final name = '${a['name'] ?? ''}'.trim();
    if (name.isEmpty) return HandleOutcome.clarifyKey('needProduct');
    final price = a['price'] is num ? (a['price'] as num).toDouble() : parseNum('${a['price'] ?? ''}');
    if (price == null || price <= 0) return HandleOutcome.clarifyKey('needPriceValue');
    final unit = _aiUnit('${a['unit'] ?? ''}');
    if (unit == null) return HandleOutcome.clarifyKey('h.unitBad');
    final cost = a['purchase'] is num ? (a['purchase'] as num).toDouble() : parseNum('${a['purchase'] ?? ''}');
    String num2(double v) => v.toStringAsFixed(v.truncateToDouble() == v ? 0 : 2);
    if (!mounted) return HandleOutcome.errorKey('h.screenNotReady');
    setState(() {
      if (_createdId == null) _kind = 'product';
      _name.text = name;
      // закупка раньше цены: MarginFields пересчитывает цену по наценке при смене закупки — названная цена должна остаться
      _cost.text = cost == null ? '' : num2(cost);
      _price.text = num2(price);
      _barcode.text = '${a['barcode'] ?? ''}'.trim();
      _category.text = '${a['category'] ?? ''}'.trim();
      _unit = unit;
    });
    return null;
  }

  /// Что-то введено — случайное «Назад» спрашивает (LeaveGuard).
  bool get _dirty =>
      _createdId == null &&
      (_name.text.trim().isNotEmpty || _barcode.text.trim().isNotEmpty || _cost.text.trim().isNotEmpty || _price.text.trim().isNotEmpty || _photos.isNotEmpty);

  /// Создать товар один раз: id запоминается; создание без ответа — сначала поиск на сервере.
  Future<String> _createOnce(ReceiveLine line, Future<String> Function() create) async {
    if (_createdId != null) return _createdId!;
    if (_createUnknown) {
      final found = await ref.read(adminReceiveRepoProvider).findCreated(line.name, line.barcode);
      if (found != null) {
        _createUnknown = false;
        return _createdId = found;
      }
    }
    try {
      _createdId = await create();
      _createUnknown = false;
      return _createdId!;
    } on ApiException catch (e) {
      if (AdminReceiveRepository.outcomeUnknown(e)) _createUnknown = true;
      rethrow;
    }
  }

  @override
  void initState() {
    super.initState();
    _aiHandles.register('new_item', _aiHandle);
    ref
        .read(adminReceiveRepoProvider)
        .suppliers()
        .then((s) {
          if (mounted) setState(() => _suppliers = s);
        })
        .catchError((_) {});
    final code = widget.barcode?.trim() ?? '';
    if (code.isNotEmpty) {
      _barcode.text = code;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _lookup(code);
      });
    }
  }

  @override
  void dispose() {
    _aiHandles.unregister('new_item', _aiHandle);
    _camera?.dispose();
    _pack.dispose();
    _plu.dispose();
    for (final c in _cells.values) {
      c.dispose();
    }
    super.dispose();
  }

  double get _effectiveCost {
    final cost = parseNum(_cost.text) ?? 0;
    final d = (parseNum(_supplierDiscount.text) ?? 0).clamp(0, 100);
    return roundMoney(cost * (1 - d / 100));
  }

  List<Map<String, Object>> get _altBarcodes => [
    for (final a in _altCodes)
      if (a.code.text.trim().isNotEmpty) {'barcode': a.code.text.trim(), 'name': a.name.text.trim(), 'quantity': (parseNum(a.qty.text) ?? 0).round()},
  ];

  Future<void> _scanAltCode(TextEditingController target) async {
    final codes = await Navigator.of(context)
        .push<List<String>>(MaterialPageRoute(builder: (_) => const ScanScreen(initial: ScanMode.receive, returnCodes: true)));
    if (codes != null && codes.isNotEmpty && mounted) setState(() => target.text = codes.last);
  }

  Future<void> _addPhoto(ImageSource source) async {
    final bytes = await pickPhoto(source);
    if (bytes != null && mounted) setState(() => _photos.add(bytes));
  }

  /// Фото из интернета: варианты по штрихкоду (Open Food Facts) и названию (Яндекс.Картинки) — выбрать нажатием.
  Future<void> _photoFromInternet() async {
    final l = context.l;
    final name = _name.text.trim();
    final code = _barcode.text.trim();
    if (name.isEmpty && code.isEmpty) {
      toast(context, l.clientName, error: true);
      return;
    }
    final picked = await showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      builder: (c) => SafeArea(
        child: FutureBuilder<List<String>>(
          future: PhotoFinder.search(name, code),
          builder: (c, snap) {
            if (!snap.hasData) {
              return Padding(
                padding: const EdgeInsets.all(32),
                child: Row(children: [const CircularProgressIndicator(), const SizedBox(width: 16), Text(l.photosSearching)]),
              );
            }
            final urls = snap.data!;
            if (urls.isEmpty) return Padding(padding: const EdgeInsets.all(32), child: Text(l.nothingFound));
            return GridView.count(
              crossAxisCount: 3,
              shrinkWrap: true,
              padding: const EdgeInsets.all(12),
              mainAxisSpacing: 8,
              crossAxisSpacing: 8,
              children: [
                for (final u in urls)
                  InkWell(
                    onTap: () => Navigator.pop(c, u),
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(10),
                      child: Image.network(u, fit: BoxFit.cover, cacheWidth: 300, errorBuilder: (_, _, _) => const Icon(Icons.broken_image_outlined)),
                    ),
                  ),
              ],
            );
          },
        ),
      ),
    );
    if (picked == null) return;
    final bytes = await PhotoFinder.download(picked);
    if (!mounted) return;
    if (bytes == null) {
      toast(context, l.nothingFound, error: true);
      return;
    }
    setState(() => _photos.add(bytes));
  }

  /// Описание из интернета (ИИ с поиском Google); текст можно поправить руками.
  Future<void> _findDescription() async {
    final l = context.l;
    if (_name.text.trim().isEmpty) {
      toast(context, l.clientName, error: true);
      return;
    }
    if (!await ensureAiKey(context, ref)) return;
    setState(() => _describing = true);
    try {
      final text = await ref.read(visionProvider).describe(_name.text.trim(), _barcode.text.trim(), photo: _photos.isEmpty ? null : _photos.first);
      if (mounted) setState(() => _description.text = text);
    } on VisionException catch (e) {
      if (mounted) toast(context, e.noKey ? l.aiKeyNeeded : e.message, error: true);
    } finally {
      if (mounted) setState(() => _describing = false);
    }
  }

  /// После создания: фото в карточку (первое — главное), количество — приходом от поставщика или прямо в остаток.
  Future<List<String>> _afterCreate(String id, double qty, double cost) async {
    Uint8List? teachMain;
    if (_camShots.isNotEmpty && !_teachDone) {
      _teachDone = true;
      await ProductRefs.save(ref.read(databaseProvider), id, _camShots);
      // снимок обучения — фото товара, если другого нет (2026-10-22: этикетка спереди, если её снимали)
      if (_photos.isEmpty) _photos.add(teachMain = teachPhoto(_camShots));
      // 2026-10-22 (аудит У-8): снимки обучения — в NurCRM, чтобы другие телефоны узнавали товар
      unawaited(shareTeachShots(ref, id, _camShots, already: teachMain));
    }
    final repo = ref.read(adminReceiveRepoProvider);
    final errors = <String>[];
    for (var i = 0; i < _photos.length; i++) {
      if (_photosDone.contains(i)) continue;
      try {
        await repo.uploadImage(id, _photos[i], primary: i == 0);
        _photosDone.add(i);
      } on ApiException catch (e) {
        errors.add(e.message);
      }
    }
    // 2026-10-22 (мобилка, админ, П10): остаток — один раз (повтор не проводит второй приход)
    if (qty > 0 && !_stockDone) {
      if (_supplierId != null) {
        final (_, errs) = await repo.post(
          [
            ReceiveLine(
              productId: id,
              name: _name.text.trim(),
              barcode: _barcode.text.trim(),
              unit: _unit,
              qty: qty,
              cost: cost,
              price: parseNum(_price.text) ?? 0,
              isNew: true,
            ),
          ],
          _supplierId,
          _paidNow,
        );
        errors.addAll(errs);
      } else {
        await ref.read(apiProvider).patch('api/main/products/$id/', body: {'quantity': qty});
      }
      _stockDone = true;
    }
    return errors;
  }

  TextEditingController _cell(String color, String size) => _cells.putIfAbsent('$color|$size', () => TextEditingController());

  int get _variantCount => _colors.fold(0, (s, c) => s + _sizes.where((z) => (parseNum(_cell(c, z).text) ?? 0) > 0).length);
  double get _variantQty => _colors.fold(0.0, (s, c) => s + _sizes.fold(0.0, (t, z) => t + (parseNum(_cell(c, z).text) ?? 0)));

  /// 2026-10-26 (требование владельца): «Сгенерировать» — свой EAN-13 (не с 0, не весовой), свободный в базе телефона
  /// (до 10 попыток; нет связи — код без проверки, «уже есть» поймает _save). Введённый код меняется после подтверждения.
  /// В общую базу по такому коду не ищем — это свой код магазина.
  Future<void> _generateCode() async {
    final l = context.l;
    if (_barcode.text.trim().isNotEmpty) {
      final ok = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          content: Text(l.bcGenReplace),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.bcGenDo)),
          ],
        ),
      );
      if (ok != true || !mounted) return;
    }
    final db = ref.read(databaseProvider);
    final code = await BarcodeGenerator.createFree((c) async => await db.productByBarcode(c) != null);
    if (!mounted) return;
    setState(() {
      _barcode.text = code;
      _found = false;
      _generatedCode = code;
    });
    unawaited(_verifyOnServer(code)); // фоном, без блокировки ввода
  }

  /// 2026-10-26 (живой аудит, П7): код проверяем и по серверу (products/list/?barcode=, 2 с) — товар, заведённый на ПК,
  /// мог ещё не прийти в базу телефона. Занят — выдаём новый (до 3 раз), нет связи — оставляем как есть.
  Future<void> _verifyOnServer(String code, {int left = 3}) async {
    final owner = await serverBarcodeOwner(ref.read(apiProvider), code);
    if (owner == null || !mounted || _barcode.text.trim() != code) return;
    final db = ref.read(databaseProvider);
    final fresh = await BarcodeGenerator.createFree((c) async => await db.productByBarcode(c) != null);
    if (!mounted || _barcode.text.trim() != code) return;
    setState(() {
      _barcode.text = fresh;
      _generatedCode = fresh;
    });
    toast(context, context.l.bcGenTaken);
    if (left > 1) unawaited(_verifyOnServer(fresh, left: left - 1));
  }

  Future<void> _lookup(String code) async {
    final l = context.l;
    if (code.trim().isEmpty) return;
    _barcode.text = code.trim();
    // 2026-10-22 (мобилка, админ, П10): как скан кассы — все написания кода, упаковка, сервер
    final existing = (await CodeLookup.find(ref.read(databaseProvider), ref.read(apiProvider), code.trim(), allowWeightLabel: false)).product;
    if (!mounted) return;
    if (existing != null) {
      toast(context, l.alreadyInStock(existing.name), error: true);
      return;
    }
    final info = await ref.read(adminReceiveRepoProvider).globalInfo(code.trim());
    if (!mounted) return;
    setState(() {
      _scanning = false;
      _found = info != null;
      if (info != null) {
        if (_name.text.trim().isEmpty) _name.text = info.name;
        if (_category.text.trim().isEmpty && info.category != null) _category.text = info.category!;
        if (_brand.text.trim().isEmpty && info.brand != null) _brand.text = info.brand!;
      }
    });
  }

  /// Н4: найденное в базе NurCRM — что взять (название, категория, бренд); поиск в браузере по штрихкоду или названию.
  Future<void> _findInternet() async {
    final l = context.l;
    final code = _barcode.text.trim();
    final query = code.isNotEmpty ? code : _name.text.trim();
    if (query.isEmpty) {
      toast(context, l.barcodeLabel, error: true);
      return;
    }
    final info = code.isEmpty ? null : await ref.read(adminReceiveRepoProvider).globalInfo(code);
    if (!mounted) return;
    var takeName = true, takeCategory = true, takeBrand = true;
    final apply = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l.findInternet, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
                const SizedBox(height: 4),
                Text(query, style: TextStyle(color: c.pal.textSoft)),
                const SizedBox(height: 12),
                if (info == null)
                  Text(l.nothingFound, style: TextStyle(color: c.pal.textSoft))
                else ...[
                  Panel(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(info.name, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
                        Text(
                          '${l.internetFound} · ${l.barcodeMatched}',
                          style: const TextStyle(color: T.success, fontSize: 12, fontWeight: FontWeight.w700),
                        ),
                        if (info.category != null || info.brand != null)
                          Text([info.category, info.brand].whereType<String>().join(' · '), style: TextStyle(color: c.pal.textSoft, fontSize: 12.5)),
                      ],
                    ),
                  ),
                  const SizedBox(height: 8),
                  FieldLabel(l.takeLabel),
                  Wrap(
                    spacing: 6,
                    children: [
                      FilterChip(label: Text(l.clientName), selected: takeName, onSelected: (v) => set(() => takeName = v)),
                      if (info.category != null)
                        FilterChip(label: Text(l.categoryLabel), selected: takeCategory, onSelected: (v) => set(() => takeCategory = v)),
                      if (info.brand != null) FilterChip(label: Text(l.brandLabel), selected: takeBrand, onSelected: (v) => set(() => takeBrand = v)),
                    ],
                  ),
                ],
                const SizedBox(height: 6),
                Text(l.priceNotTaken, style: TextStyle(color: c.pal.textSoft, fontSize: 12)),
                const SizedBox(height: 12),
                OutlinedButton.icon(
                  onPressed: () => launchUrl(Uri.parse('https://www.google.com/search?q=${Uri.encodeComponent(query)}'), mode: LaunchMode.externalApplication),
                  icon: const Icon(Icons.travel_explore_rounded),
                  label: Text(l.searchInBrowser),
                ),
                if (info != null) ...[
                  const SizedBox(height: 8),
                  FilledButton(
                    style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                    onPressed: () => Navigator.pop(c, true),
                    child: Text(l.fillProduct),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
    if (apply != true || info == null || !mounted) return;
    setState(() {
      _found = true;
      if (takeName) _name.text = info.name;
      if (takeCategory && info.category != null) _category.text = info.category!;
      if (takeBrand && info.brand != null) _brand.text = info.brand!;
    });
  }

  Future<void> _addSize() async {
    final l = context.l;
    final ctrl = TextEditingController();
    final v = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.customSize),
        content: TextField(controller: ctrl, autofocus: true, textCapitalization: TextCapitalization.characters, onSubmitted: (v) => Navigator.pop(c, v)),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: Text(MaterialLocalizations.of(c).cancelButtonLabel)),
          FilledButton(onPressed: () => Navigator.pop(c, ctrl.text), child: Text(MaterialLocalizations.of(c).okButtonLabel)),
        ],
      ),
    );
    final z = v?.trim().toUpperCase() ?? '';
    if (z.isEmpty || !mounted) return;
    setState(() {
      if (!_allSizes.contains(z)) _allSizes.add(z);
      if (!_sizes.contains(z)) _sizes.add(z);
      _sizes.sort((a, b) => _allSizes.indexOf(a).compareTo(_allSizes.indexOf(b)));
    });
  }

  Future<void> _addColor() async {
    final l = context.l;
    final ctrl = TextEditingController();
    final name = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.addColor),
        content: TextField(
          controller: ctrl,
          autofocus: true,
          decoration: InputDecoration(hintText: l.colorName),
          onSubmitted: (v) => Navigator.pop(c, v),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: Text(MaterialLocalizations.of(c).cancelButtonLabel)),
          FilledButton(onPressed: () => Navigator.pop(c, ctrl.text), child: Text(MaterialLocalizations.of(c).okButtonLabel)),
        ],
      ),
    );
    final v = name?.trim() ?? '';
    if (v.isNotEmpty && !_colors.contains(v)) setState(() => _colors.add(v));
  }

  Future<void> _save() async {
    final l = context.l;
    final name = _name.text.trim();
    final cost = parseNum(_cost.text) ?? 0;
    final price = parseNum(_price.text) ?? (_kind == 'service' ? 0 : ((cost * 1.3 / 5).ceil() * 5).toDouble());
    if (name.isEmpty || price <= 0) {
      setState(() => _submitted = true);
      toast(context, name.isEmpty ? l.peNameNeed : l.nuPriceNeed, error: true);
      _aiSink.report(HandleOutcome.clarifyKey(name.isEmpty ? 'needProduct' : 'needPriceValue'));
      return;
    }
    if (_busy) return;
    // 2026-10-29: штрихкод и доп. штрихкоды — 4–14 цифр
    if (_kind != 'service' && (!barcodeFieldOk(_barcode.text) || _altCodes.any((a) => !barcodeFieldOk(a.code.text)))) {
      toast(context, l.barcodeBadLen, error: true);
      return;
    }
    // 2026-10-29: количество по единице — «шт» целое, «кг/л/м» можно с дробью (12,5); товар с упаковкой считается в упаковках
    if (_kind == 'product' && _createdId == null) {
      final qtyErr = qtyErrorText(l, _unit, parseNum(_qty.text) ?? 0, packed: _pack.enabled);
      if (qtyErr != null) {
        toast(context, qtyErr, error: true);
        return;
      }
    }
    // 2026-10-27 (М4): поштучная продажа из упаковки — проверки до создания товара (штук целое ≥ 2, цена штуки, расхождение > 5 %)
    if (_kind == 'product' && _pack.enabled && _createdId == null) {
      if (isWeightUnit(_unit)) {
        toast(context, l.pkWeightBlocked, error: true);
        return;
      }
      final pr = _pack.problem;
      if (pr == PackProblem.qtyBad || pr == PackProblem.pieceBad) {
        toast(context, packErrorText(context, pr)!, error: true);
        return;
      }
      if (pr == PackProblem.mismatch) {
        final keep = await confirmPackMismatch(context, _pack);
        if (!mounted || !keep) return;
      }
    }
    setState(() => _busy = true);
    final repo = ref.read(adminReceiveRepoProvider);
    final api = ref.read(apiProvider);
    try {
      // 2026-10-22 (мобилка, админ, П10): «уже есть» — как скан кассы: все написания кода, упаковка, сервер (товар,
      // заведённый на ПК минуту назад). Раньше проверялся только точный штрихкод в базе телефона и только по Enter.
      final code = _barcode.text.trim();
      if (_createdId == null && _kind != 'service' && code.isNotEmpty) {
        final hit = await CodeLookup.find(ref.read(databaseProvider), api, code, allowWeightLabel: false);
        if (hit.product != null) {
          if (mounted) toast(context, l.alreadyInStock(hit.product!.name), error: true);
          _aiSink.report(HandleOutcome.error(l.alreadyInStock(hit.product!.name)));
          return;
        }
      }
      if (_kind == 'service') {
        final line = ReceiveLine(productId: '', name: name, unit: 'шт', cost: 0, price: price, qty: 0, isNew: true);
        final id = await _createOnce(line, () => repo.createProduct(line, kind: 'service', category: _category.text, description: _description.text));
        await _afterCreate(id, 0, 0);
      } else if (_kind == 'clothes') {
        final line = ReceiveLine(productId: '', name: name, barcode: _barcode.text.trim(), unit: 'шт', cost: _effectiveCost, price: price, qty: 0, isNew: true);
        final id = await _createOnce(
          line,
          () => repo.createProduct(
            line,
            category: _category.text,
            brand: _brand.text,
            wholesale: parseNum(_wholesale.text),
            altBarcodes: _altBarcodes,
            description: _description.text,
          ),
        );
        var saved = 0;
        final errors = <String>[];
        for (final color in _colors) {
          for (final size in _sizes) {
            final q = parseNum(_cell(color, size).text) ?? 0;
            if (q <= 0 || _variantsDone.contains('$color|$size')) continue;
            try {
              await api.post(
                'api/main/products/$id/variants/',
                body: {'size': size, 'color': color, 'barcode': null, 'quantity': q.toStringAsFixed(3), 'price': null, 'is_active': true},
              );
              _variantsDone.add('$color|$size');
              saved++;
            } on ApiException catch (e) {
              errors.add('$color $size: ${e.message}');
            }
          }
        }
        // остаток вариантов — на складе сразу; от поставщика — приходом на всё количество
        errors.addAll(await _afterCreate(id, _variantQty, _effectiveCost));
        if (mounted) toast(context, errors.isEmpty ? l.variantsSaved(saved) : errors.first, error: errors.isNotEmpty);
      } else {
        final qty = parseNum(_qty.text) ?? 0;
        final line = ReceiveLine(
          productId: '',
          name: name,
          barcode: _barcode.text.trim(),
          unit: _unit,
          cost: _effectiveCost,
          price: price,
          qty: qty,
          isNew: true,
        );
        // 2026-10-22 (мобилка, админ, П10): «кг» — весовой товар с PLU (весы печатают этикетку с этим кодом)
        final weight = isWeightUnit(_unit); // 2026-10-29: кг и г — весовые (как карточка товара)
        final plu = weight && _createdId == null ? await repo.nextPluOr(_plu.text) : null;
        final id = await _createOnce(
          line,
          () => repo.createProduct(
            line,
            category: _category.text,
            brand: _brand.text,
            wholesale: parseNum(_wholesale.text),
            altBarcodes: _altBarcodes,
            description: _description.text,
            isWeight: weight,
            plu: plu,
            packages: weight || _pack.toPack() == null ? null : [_pack.toPack()!.toInput(_unit)],
          ),
        );
        final errors = await _afterCreate(id, qty, _effectiveCost);
        if (errors.isNotEmpty && mounted) toast(context, errors.first, error: true);
      }
      // 2026-10-22 (мобилка, админ): товар уже создан — сбой обновления каталога не ошибка сохранения
      try {
        await ref.read(catalogRepoProvider).sync();
      } catch (_) {}
      Audit.log('product', '${l.productCreated}: ${_name.text.trim()}');
      _aiSink.report(HandleOutcome.ok({'id': _createdId ?? ''}));
      if (!mounted) return;
      toast(context, l.productCreated);
      // 2026-10-26 (живой аудит, П7): сохранён сгенерированный код — предложить этикетку (иначе товар нечем сканировать)
      final labelCode = _kind != 'service' && _generatedCode != null && _barcode.text.trim() == _generatedCode ? _generatedCode : null;
      if (labelCode != null) {
        _generatedCode = null;
        await askPrintLabel(context, name: name, code: labelCode, price: price);
        if (!mounted) return;
      }
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
      // создание без ответа: экран запомнит это (_createUnknown) и при повторе найдёт товар на сервере, а не создаст второй
      _aiSink.report(AdminReceiveRepository.outcomeUnknown(e) ? const HandleOutcome(ToolStatus.error, unknown: true) : HandleOutcome.error(e.message));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final service = _kind == 'service';
    final clothes = _kind == 'clothes';
    // 2026-10-22 (мобилка, админ, П10): случайное «Назад» с заполненной карточкой — спросить
    return LeaveGuard(
      ask: () => _dirty && !_busy,
      child: Scaffold(
      appBar: ScreenTitle(l.newProduct, subtitle: l.newProductSub),
      // 14.10, владелец (снимок): «Сохранить услугу» пряталась под полосой жестов — отступ снизу на её высоту.
      body: ListView(
        padding: EdgeInsets.fromLTRB(16, 4, 16, 24 + MediaQuery.paddingOf(context).bottom),
        children: [
          SegmentedButton<String>(
            segments: [
              ButtonSegment(value: 'product', label: Text(l.kindProduct)),
              ButtonSegment(value: 'clothes', label: Text(l.kindClothes)),
              ButtonSegment(value: 'service', label: Text(l.kindService)),
            ],
            selected: {_kind},
            // 2026-10-22 (мобилка, админ, П10): товар уже создан — вид не меняется (повтор доделывает тот же товар)
            onSelectionChanged: _createdId != null ? null : (s) => setState(() => _kind = s.first),
          ),
          const SizedBox(height: 10),
          if (!service) ...[
            if (_scanning)
              SizedBox(
                height: 200,
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(16),
                  child: MobileScanner(
                    controller: _camera ??= MobileScannerController(),
                    onDetect: (cap) {
                      final v = cap.barcodes.firstOrNull?.rawValue;
                      if (v != null && v.isNotEmpty && v != _barcode.text) _lookup(v);
                    },
                  ),
                ),
              ),
            // 2026-10-26 (живой аудит, П7, Н-7): подпись над полем, как у остальных (было плавающее labelText)
            FieldLabel(l.barcodeLabel),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    key: const Key('newItemBarcode'),
                    controller: _barcode,
                    keyboardType: TextInputType.number,
                    inputFormatters: barcodeFormatters,
                    onSubmitted: _lookup,
                    onChanged: (_) => setState(() {}),
                    decoration: InputDecoration(helperText: l.barcodeDigitsHint, errorText: barcodeFieldOk(_barcode.text) ? null : l.barcodeBadLen),
                  ),
                ),
                const SizedBox(width: 8),
                IconButton.outlined(tooltip: l.bcGenTooltip, onPressed: _generateCode, icon: const Icon(Icons.casino_outlined)),
                const SizedBox(width: 8),
                IconButton.filled(
                  tooltip: l.a11yScanner,
                  onPressed: () => setState(() => _scanning = !_scanning),
                  icon: Icon(_scanning ? Icons.videocam_off_rounded : Icons.qr_code_scanner_rounded),
                ),
              ],
            ),
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(onPressed: _findInternet, icon: const Icon(Icons.travel_explore_rounded, size: 18), label: Text(l.findInternet)),
            ),
            if (_found) ...[
              Text(
                l.foundInBase,
                style: const TextStyle(color: T.success, fontWeight: FontWeight.w700),
              ),
              // 2026-10-27: откуда подставлено — встроенный справочник NurCRM (работает и без связи)
              Text(l.globalSource, style: TextStyle(color: context.pal.textSoft, fontSize: 12)),
            ],
          ],
          FieldLabel(l.peName), // 2026-10-26 (живой аудит, П7, Н-7): было «Имя» (clientName)
          TextField(controller: _name),
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    FieldLabel(l.categoryLabel),
                    TextField(controller: _category),
                  ],
                ),
              ),
              if (!service) ...[
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      FieldLabel(l.brandLabel),
                      TextField(controller: _brand),
                    ],
                  ),
                ),
              ],
            ],
          ),
          if (service) ...[
            FieldLabel(l.price),
            TextField(controller: _price, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
          ] else ...[
            Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      FieldLabel(l.purchasePrice),
                      TextField(controller: _cost, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
                    ],
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      FieldLabel(l.retailPrice),
                      TextField(
                        key: const Key('newItemPrice'),
                        controller: _price,
                        keyboardType: const TextInputType.numberWithOptions(decimal: true),
                        onChanged: (_) => setState(() {}),
                        decoration: InputDecoration(errorText: _submitted && (parseNum(_price.text) ?? 0) <= 0 && _kind != 'service' && (parseNum(_cost.text) ?? 0) <= 0 ? l.nuPriceNeed : null),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            MarginFields(cost: _cost, price: _price),
          // 2026-10-29: количество и единица — сразу под ценой (раньше были внизу, после поставщика и доп. штрихкодов,
          // и владелец их не находил). Единицы — общий каталог units.dart; «кг/л/м» — дробное количество, «шт» — целое.
          if (_kind == 'product') ...[
            const SizedBox(height: 8),
            FieldLabel(l.quantityLabel),
            ListenableBuilder(
              listenable: Listenable.merge([_qty, _pack]),
              builder: (c, _) => TextField(
                key: const Key('newItemQty'),
                controller: _qty,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: InputDecoration(suffixText: _unit, errorText: qtyErrorText(l, _unit, parseNum(_qty.text), packed: _pack.enabled)),
              ),
            ),
            FieldLabel(l.unitLabel),
            UnitPicker(
              value: _unit,
              onChanged: (u) => setState(() {
                _unit = u;
                if (isWeightUnit(u)) _pack.setEnabled(false); // весовой товар нельзя продавать поштучно из упаковки
              }),
            ),
            // 2026-10-29: подсказка про упаковки — рядом с переключателем (под выбором единицы), а не выше
            ListenableBuilder(
              listenable: _pack,
              builder: (c, _) => _pack.enabled && !isWeightUnit(_unit)
                  ? Padding(padding: const EdgeInsets.only(top: 4), child: Text(l.pkQtyInPacks, style: TextStyle(color: pal.textSoft, fontSize: 12)))
                  : const SizedBox.shrink(),
            ),
            // 2026-10-27 (М4): упаковки — для любого штучного товара; остаток выше вводится в упаковках
            PackSection(ctrl: _pack, blocked: isWeightUnit(_unit)),
            if (isWeightUnit(_unit)) ...[
              FieldLabel(l.pAPluLabel),
              TextField(
                controller: _plu,
                keyboardType: TextInputType.number,
                decoration: InputDecoration(hintText: l.pAPluHint),
              ),
            ],
          ],
            FieldLabel(l.wholesalePrice),
            TextField(controller: _wholesale, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
            ..._supplierSection(context),
            ..._altSection(context),
          ],
          if (clothes) ..._clothesSection(context),
          ..._photoSection(context),
          FieldLabel(l.descriptionLabel),
          TextField(controller: _description, minLines: 3, maxLines: 8, keyboardType: TextInputType.multiline),
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton.icon(
              onPressed: _describing ? null : _findDescription,
              icon: _describing
                  ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.auto_awesome_rounded, size: 18),
              label: Text(_describing ? l.descriptionWriting : l.descriptionFind),
            ),
          ),
          const SizedBox(height: 18),
          if (clothes)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Text(
                l.variantsSummary(_variantCount, qtyText(_variantQty)),
                textAlign: TextAlign.center,
                style: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w700),
              ),
            ),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
            onPressed: _busy ? null : _save,
            child: Text(service ? l.saveService : l.save),
          ),
        ],
      ),
    ),
    );
  }

  List<Widget> _supplierSection(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final discount = parseNum(_supplierDiscount.text) ?? 0;
    return [
      FieldLabel(l.supplierLabel),
      DropdownButtonFormField<String?>(
        initialValue: _supplierId,
        isExpanded: true,
        items: [
          DropdownMenuItem(value: null, child: Text(l.noSupplier)),
          for (final (id, name) in _suppliers)
            DropdownMenuItem(
              value: id,
              child: Text(name, overflow: TextOverflow.ellipsis),
            ),
        ],
        onChanged: (v) => setState(() => _supplierId = v),
      ),
      if (_supplierId != null) ...[
        FieldLabel(l.supplierDiscount),
        TextField(controller: _supplierDiscount, keyboardType: const TextInputType.numberWithOptions(decimal: true), onChanged: (_) => setState(() {})),
        if (discount > 0 && (parseNum(_cost.text) ?? 0) > 0)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(
              l.costWithDiscount(money(_effectiveCost)),
              style: const TextStyle(fontWeight: FontWeight.w800, color: T.success),
            ),
          ),
        const SizedBox(height: 6),
        SegmentedButton<bool>(
          segments: [
            ButtonSegment(value: false, label: Text(l.onCredit)),
            ButtonSegment(value: true, label: Text(l.payNowCash)),
          ],
          selected: {_paidNow},
          onSelectionChanged: (v) => setState(() => _paidNow = v.first),
        ),
        Text(l.supplierReceiptNote, style: TextStyle(color: pal.textSoft, fontSize: 12)),
      ],
    ];
  }

  List<Widget> _altSection(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return [
      FieldLabel(l.altBarcodesLabel),
      Text(l.altBarcodesHint, style: TextStyle(color: pal.textSoft, fontSize: 12)),
      for (final a in _altCodes)
        Padding(
          padding: const EdgeInsets.only(top: 6),
          child: Row(
            children: [
              Expanded(
                flex: 4,
                child: TextField(
                  controller: a.code,
                  keyboardType: TextInputType.number,
                  inputFormatters: barcodeFormatters,
                  decoration: InputDecoration(hintText: l.barcodeLabel, isDense: true),
                ),
              ),
              IconButton(icon: const Icon(Icons.qr_code_scanner_rounded), onPressed: () => _scanAltCode(a.code)),
              Expanded(
                flex: 2,
                child: TextField(
                  controller: a.qty,
                  keyboardType: TextInputType.number,
                  decoration: InputDecoration(hintText: l.packQty, isDense: true),
                ),
              ),
              const SizedBox(width: 6),
              Expanded(
                flex: 3,
                child: TextField(
                  controller: a.name,
                  decoration: InputDecoration(hintText: l.packName, isDense: true),
                ),
              ),
              IconButton(icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _altCodes.remove(a))),
            ],
          ),
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(
          onPressed: () => setState(() => _altCodes.add((code: TextEditingController(), qty: TextEditingController(), name: TextEditingController()))),
          icon: const Icon(Icons.add_rounded, size: 18),
          label: Text(l.altBarcodeAdd),
        ),
      ),
    ];
  }

  List<Widget> _photoSection(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return [
      FieldLabel(l.photosLabel),
      if (_photos.isNotEmpty) ...[
        SizedBox(
          height: 86,
          child: ListView(
            scrollDirection: Axis.horizontal,
            children: [
              for (var i = 0; i < _photos.length; i++)
                Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: Stack(
                    children: [
                      ClipRRect(
                        borderRadius: BorderRadius.circular(10),
                        child: Image.memory(_photos[i], width: 86, height: 86, fit: BoxFit.cover, cacheWidth: 260),
                      ),
                      Positioned(
                        right: 0,
                        top: 0,
                        child: InkWell(
                          onTap: () => setState(() => _photos.removeAt(i)),
                          child: const CircleAvatar(radius: 12, child: Icon(Icons.close, size: 14)),
                        ),
                      ),
                    ],
                  ),
                ),
            ],
          ),
        ),
        Text(l.photoFirstMain, style: TextStyle(color: pal.textSoft, fontSize: 11.5)),
      ],
      Wrap(
        spacing: 6,
        children: [
          ActionChip(avatar: const Icon(Icons.photo_camera_rounded, size: 18), label: Text(l.photoCamera), onPressed: () => _addPhoto(ImageSource.camera)),
          ActionChip(avatar: const Icon(Icons.photo_library_outlined, size: 18), label: Text(l.photoGallery), onPressed: () => _addPhoto(ImageSource.gallery)),
          ActionChip(avatar: const Icon(Icons.travel_explore_rounded, size: 18), label: Text(l.photoInternet), onPressed: _photoFromInternet),
        ],
      ),
      const SizedBox(height: 8),
      OutlinedButton.icon(
        onPressed: () async {
          // 2026-10-22: сканер штрихкода на этом экране держит камеру — выключаем, иначе умная камера откроется чёрной
          if (_scanning) {
            setState(() => _scanning = false);
            try {
              await _camera?.stop();
            } catch (_) {}
            if (!context.mounted) return;
          }
          // 2026-10-22: название — для надписи «Штрихкод … будет привязан к «Товар»» в шаге «Штрихкод»
          final r = await teachCamera(context, askBarcode: _barcode.text.trim().isEmpty, productName: _name.text.trim());
          if (r == null || !mounted) return;
          setState(() {
            _camShots = r.shots;
            if (r.barcode != null && _barcode.text.trim().isEmpty) _barcode.text = r.barcode!;
          });
        },
        icon: Icon(_camShots.isEmpty ? Icons.center_focus_strong_rounded : Icons.check_circle_rounded, color: _camShots.isEmpty ? null : T.success),
        label: Text(_camShots.isEmpty ? l.teachCamera : l.teachShots(_camShots.length)),
      ),
    ];
  }

  List<Widget> _clothesSection(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return [
      FieldLabel(l.sizesLabel),
      Wrap(
        spacing: 6,
        children: [
          for (final z in _allSizes)
            FilterChip(
              label: Text(z),
              selected: _sizes.contains(z),
              onSelected: (on) => setState(() {
                if (on) {
                  _sizes.add(z);
                  _sizes.sort((a, b) => _allSizes.indexOf(a).compareTo(_allSizes.indexOf(b)));
                } else {
                  _sizes.remove(z);
                }
              }),
              onDeleted: _baseSizes.contains(z)
                  ? null
                  : () => setState(() {
                      _allSizes.remove(z);
                      _sizes.remove(z);
                    }),
            ),
          ActionChip(avatar: const Icon(Icons.add, size: 18), label: Text(l.customSize), onPressed: _addSize),
        ],
      ),
      FieldLabel(l.colorsLabel),
      Wrap(
        spacing: 6,
        children: [
          for (final c in _colors) InputChip(label: Text(c), onDeleted: () => setState(() => _colors.remove(c))),
          ActionChip(avatar: const Icon(Icons.add, size: 18), label: Text(l.addColor), onPressed: _addColor),
        ],
      ),
      if (_colors.isNotEmpty && _sizes.isNotEmpty) ...[
        FieldLabel(l.colorSizeGrid),
        SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Table(
            defaultColumnWidth: const FixedColumnWidth(58),
            columnWidths: const {0: FixedColumnWidth(96)},
            defaultVerticalAlignment: TableCellVerticalAlignment.middle,
            children: [
              TableRow(
                children: [
                  const SizedBox(),
                  for (final z in _sizes)
                    Center(
                      child: Text(z, style: const TextStyle(fontWeight: FontWeight.w800)),
                    ),
                ],
              ),
              for (final c in _colors)
                TableRow(
                  children: [
                    Padding(
                      padding: const EdgeInsets.only(right: 6),
                      child: Text(
                        c,
                        style: const TextStyle(fontWeight: FontWeight.w700),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    for (final z in _sizes)
                      Padding(
                        padding: const EdgeInsets.all(3),
                        child: TextField(
                          controller: _cell(c, z),
                          textAlign: TextAlign.center,
                          keyboardType: TextInputType.number,
                          onChanged: (_) => setState(() {}),
                          decoration: InputDecoration(
                            hintText: '0',
                            isDense: true,
                            hintStyle: TextStyle(color: pal.textSoft),
                          ),
                        ),
                      ),
                  ],
                ),
            ],
          ),
        ),
      ],
    ];
  }
}

/// 14.10: фото с камеры или из галереи (общий выбор для «Нового товара» и окна «Товар — цена и остаток»).
Future<Uint8List?> pickPhoto(ImageSource source) async {
  final shot = await ImagePicker().pickImage(source: source, maxWidth: 1600, maxHeight: 1600, imageQuality: 80);
  return shot?.readAsBytes();
}

/// 14.10: фото из интернета по названию и штрихкоду — выбрать нажатием (как в «Новом товаре»).
Future<Uint8List?> pickInternetPhoto(BuildContext context, String name, String code) async {
  final l = context.l;
  final picked = await showModalBottomSheet<String>(
    context: context,
    isScrollControlled: true,
    builder: (c) => SafeArea(
      child: FutureBuilder<List<String>>(
        future: PhotoFinder.search(name, code),
        builder: (c, snap) {
          if (!snap.hasData) {
            return Padding(
              padding: const EdgeInsets.all(32),
              child: Row(children: [const CircularProgressIndicator(), const SizedBox(width: 16), Text(l.photosSearching)]),
            );
          }
          final urls = snap.data!;
          if (urls.isEmpty) return Padding(padding: const EdgeInsets.all(32), child: Text(l.nothingFound));
          return GridView.count(
            crossAxisCount: 3,
            shrinkWrap: true,
            padding: const EdgeInsets.all(12),
            mainAxisSpacing: 8,
            crossAxisSpacing: 8,
            children: [
              for (final u in urls)
                InkWell(
                  onTap: () => Navigator.pop(c, u),
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(10),
                    child: Image.network(u, fit: BoxFit.cover, cacheWidth: 300, errorBuilder: (_, _, _) => const Icon(Icons.broken_image_outlined)),
                  ),
                ),
            ],
          );
        },
      ),
    ),
  );
  if (picked == null) return null;
  final bytes = await PhotoFinder.download(picked);
  if (bytes == null && context.mounted) toast(context, l.nothingFound, error: true);
  return bytes;
}
