import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/ai/vision.dart';
import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/photo_finder.dart';
import '../../core/util/ui.dart';
import '../scan/scan_screen.dart';
import '../scan/smart_camera.dart';
import '../sale/cart.dart';
import 'admin_receive.dart';
import '../../core/util/audit.dart';
import '../../core/ai/product_refs.dart';

/// 13.10, макеты Н3, Н4, Н7, Н8 (владелец: «сверься с макетом и доделывай»): новая позиция с телефона — товар, одежда
/// (размеры × цвета, у каждого варианта свой остаток — api/main/products/{id}/variants/, как карточка кассы), услуга
/// (kind=service, без остатка). «Найти в интернете» — общая база NurCRM по штрихкоду (название, категория, бренд) и поиск
/// в браузере; цену из интернета не берём. Мастера, длительность и расходники услуги — на этом телефоне, пока нет API услуг.
class NewItemScreen extends ConsumerStatefulWidget {
  final String kind;

  /// 13.10: штрихкод из сканера («товар не найден → добавить») — подставляется и ищется в общей базе NurCRM.
  final String? barcode;
  const NewItemScreen({super.key, this.kind = 'product', this.barcode});
  @override
  ConsumerState<NewItemScreen> createState() => _NewItemScreenState();
}

class _MasterRow {
  final String name;
  bool on;
  final TextEditingController price;
  final TextEditingController percent;
  _MasterRow(this.name, this.on, String price, String percent) : price = TextEditingController(text: price), percent = TextEditingController(text: percent);
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

  // услуга (Н8)
  int _minutes = 30;
  static const _presetMinutes = [15, 30, 45, 60, 90, 120];
  final List<_MasterRow> _masters = [];
  final List<({String id, String name, double qty})> _consumables = [];
  bool _online = false;

  @override
  void initState() {
    super.initState();
    _loadMasters();
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

  Future<void> _loadMasters() async {
    final raw = await ref.read(databaseProvider).getSetting('services.masters');
    final names = raw == null ? <String>[] : (jsonDecode(raw) as List).map((e) => e.toString()).toList();
    if (!mounted) return;
    setState(() {
      for (final n in names) {
        _masters.add(_MasterRow(n, false, '', '40'));
      }
    });
  }

  @override
  void dispose() {
    _camera?.dispose();
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
    if (_camShots.isNotEmpty) {
      await ProductRefs.save(ref.read(databaseProvider), id, _camShots);
      if (_photos.isEmpty) _photos.add(_camShots.last); // снимок сверху — фото товара, если другого нет
    }
    final repo = ref.read(adminReceiveRepoProvider);
    final errors = <String>[];
    for (var i = 0; i < _photos.length; i++) {
      try {
        await repo.uploadImage(id, _photos[i], primary: i == 0);
      } on ApiException catch (e) {
        errors.add(e.message);
      }
    }
    if (qty > 0) {
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
    }
    return errors;
  }

  TextEditingController _cell(String color, String size) => _cells.putIfAbsent('$color|$size', () => TextEditingController());

  int get _variantCount => _colors.fold(0, (s, c) => s + _sizes.where((z) => (parseNum(_cell(c, z).text) ?? 0) > 0).length);
  double get _variantQty => _colors.fold(0.0, (s, c) => s + _sizes.fold(0.0, (t, z) => t + (parseNum(_cell(c, z).text) ?? 0)));

  Future<void> _lookup(String code) async {
    final l = context.l;
    if (code.trim().isEmpty) return;
    _barcode.text = code.trim();
    final existing = await ref.read(databaseProvider).productByBarcode(code.trim());
    if (existing != null) {
      if (mounted) toast(context, l.alreadyInStock(existing.name), error: true);
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

  Future<void> _addMaster() async {
    final l = context.l;
    final ctrl = TextEditingController();
    final name = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.masterAdd),
        content: TextField(
          controller: ctrl,
          autofocus: true,
          decoration: InputDecoration(hintText: l.masterName),
          onSubmitted: (v) => Navigator.pop(c, v),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: Text(MaterialLocalizations.of(c).cancelButtonLabel)),
          FilledButton(onPressed: () => Navigator.pop(c, ctrl.text), child: Text(MaterialLocalizations.of(c).okButtonLabel)),
        ],
      ),
    );
    final v = name?.trim() ?? '';
    if (v.isEmpty || _masters.any((m) => m.name == v)) return;
    setState(() => _masters.add(_MasterRow(v, true, _price.text, '40')));
    await ref.read(databaseProvider).setSetting('services.masters', jsonEncode(_masters.map((m) => m.name).toList()));
  }

  Future<void> _addConsumable() async {
    final l = context.l;
    final db = ref.read(databaseProvider);
    final query = TextEditingController();
    var results = <Product>[];
    final picked = await showModalBottomSheet<Product>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => Padding(
          padding: EdgeInsets.fromLTRB(16, 12, 16, MediaQuery.viewInsetsOf(c).bottom + 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: query,
                autofocus: true,
                decoration: InputDecoration(hintText: l.findProduct, prefixIcon: const Icon(Icons.search_rounded)),
                onChanged: (q) async {
                  final r = q.trim().length < 2 ? <Product>[] : await db.searchProducts(q.trim(), limit: 20);
                  set(() => results = r.where((p) => !p.isService).toList());
                },
              ),
              SizedBox(
                height: 300,
                child: ListView(
                  children: [
                    for (final p in results)
                      ListTile(title: Text(p.name), subtitle: Text('${qtyText(p.quantity)} ${p.unit}'), onTap: () => Navigator.pop(c, p)),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
    if (picked == null || !mounted) return;
    final qty = await askNumber(context, title: picked.name, hint: picked.unit, initial: 1);
    if (qty == null || qty <= 0) return;
    setState(() => _consumables.add((id: picked.id, name: picked.name, qty: qty)));
  }

  Future<void> _save() async {
    final l = context.l;
    final name = _name.text.trim();
    final cost = parseNum(_cost.text) ?? 0;
    final price = parseNum(_price.text) ?? (_kind == 'service' ? 0 : ((cost * 1.3 / 5).ceil() * 5).toDouble());
    if (name.isEmpty || price <= 0) {
      toast(context, l.salePrice, error: true);
      return;
    }
    setState(() => _busy = true);
    final repo = ref.read(adminReceiveRepoProvider);
    final api = ref.read(apiProvider);
    try {
      if (_kind == 'service') {
        final line = ReceiveLine(productId: '', name: name, unit: 'шт', cost: 0, price: price, qty: 0, isNew: true);
        final id = await repo.createProduct(line, kind: 'service', category: _category.text, description: _description.text);
        await _afterCreate(id, 0, 0);
        await ref
            .read(databaseProvider)
            .setSetting(
              'service.$id',
              jsonEncode({
                'minutes': _minutes,
                'masters': [
                  for (final m in _masters.where((m) => m.on))
                    {'master': m.name, 'price': parseNum(m.price.text) ?? price, 'percent': (parseNum(m.percent.text) ?? 0).clamp(0, 100)},
                ],
                'consumables': [
                  for (final c in _consumables) {'productId': c.id, 'name': c.name, 'quantity': c.qty},
                ],
                'online': _online,
              }),
            );
      } else if (_kind == 'clothes') {
        final line = ReceiveLine(productId: '', name: name, barcode: _barcode.text.trim(), unit: 'шт', cost: _effectiveCost, price: price, qty: 0, isNew: true);
        final id = await repo.createProduct(
          line,
          category: _category.text,
          brand: _brand.text,
          wholesale: parseNum(_wholesale.text),
          altBarcodes: _altBarcodes,
          description: _description.text,
        );
        var saved = 0;
        final errors = <String>[];
        for (final color in _colors) {
          for (final size in _sizes) {
            final q = parseNum(_cell(color, size).text) ?? 0;
            if (q <= 0) continue;
            try {
              await api.post(
                'api/main/products/$id/variants/',
                body: {'size': size, 'color': color, 'barcode': null, 'quantity': q.toStringAsFixed(3), 'price': null, 'is_active': true},
              );
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
        final id = await repo.createProduct(
          line,
          category: _category.text,
          brand: _brand.text,
          wholesale: parseNum(_wholesale.text),
          altBarcodes: _altBarcodes,
          description: _description.text,
        );
        final errors = await _afterCreate(id, qty, _effectiveCost);
        if (errors.isNotEmpty && mounted) toast(context, errors.first, error: true);
      }
      await ref.read(catalogRepoProvider).sync();
      Audit.log('product', '${l.productCreated}: ${_name.text.trim()}');
      if (!mounted) return;
      toast(context, l.productCreated);
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
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
    return Scaffold(
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
            onSelectionChanged: (s) => setState(() => _kind = s.first),
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
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _barcode,
                    keyboardType: TextInputType.number,
                    decoration: InputDecoration(labelText: l.barcodeLabel),
                    onSubmitted: _lookup,
                  ),
                ),
                const SizedBox(width: 8),
                IconButton.filled(
                  onPressed: () => setState(() => _scanning = !_scanning),
                  icon: Icon(_scanning ? Icons.videocam_off_rounded : Icons.qr_code_scanner_rounded),
                ),
              ],
            ),
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(onPressed: _findInternet, icon: const Icon(Icons.travel_explore_rounded, size: 18), label: Text(l.findInternet)),
            ),
            if (_found)
              Text(
                l.foundInBase,
                style: const TextStyle(color: T.success, fontWeight: FontWeight.w700),
              ),
          ],
          FieldLabel(l.clientName),
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
                      TextField(controller: _price, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            MarginFields(cost: _cost, price: _price),
            FieldLabel(l.wholesalePrice),
            TextField(controller: _wholesale, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
            ..._supplierSection(context),
            ..._altSection(context),
          ],
          if (_kind == 'product') ...[
            FieldLabel(l.quantityLabel),
            TextField(controller: _qty, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
            FieldLabel(l.unitLabel),
            Wrap(
              spacing: 6,
              children: [
                for (final u in ['шт', 'кг', 'л', 'упак']) ChoiceChip(label: Text(u), selected: _unit == u, onSelected: (_) => setState(() => _unit = u)),
              ],
            ),
          ],
          if (clothes) ..._clothesSection(context),
          if (service) ..._serviceSection(context),
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
          final r = await teachCamera(context, askBarcode: _barcode.text.trim().isEmpty);
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

  List<Widget> _serviceSection(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return [
      FieldLabel(l.durationLabel),
      Wrap(
        spacing: 6,
        children: [
          for (final m in _presetMinutes)
            ChoiceChip(
              label: Text(m < 60 ? '$m мин' : '${(m / 60).toStringAsFixed(m % 60 == 0 ? 0 : 1)} ч'),
              selected: _minutes == m,
              onSelected: (_) => setState(() => _minutes = m),
            ),
          // 14.10, владелец: «добавить кнопку (свой) для длительности» — любое число минут.
          ChoiceChip(
            label: Text(_presetMinutes.contains(_minutes) ? l.durationCustom : '$_minutes мин'),
            selected: !_presetMinutes.contains(_minutes),
            onSelected: (_) async {
              final v = await askNumber(context, title: l.durationLabel, hint: l.minutesHint, initial: _minutes.toDouble());
              if (v != null && v >= 1 && mounted) setState(() => _minutes = v.round());
            },
          ),
        ],
      ),
      FieldLabel(l.mastersLabel),
      for (final m in _masters)
        Padding(
          padding: const EdgeInsets.only(bottom: 6),
          child: Row(
            children: [
              Checkbox(value: m.on, onChanged: (v) => setState(() => m.on = v ?? false)),
              Expanded(
                child: Text(m.name, style: const TextStyle(fontWeight: FontWeight.w700)),
              ),
              SizedBox(
                width: 80,
                child: TextField(
                  controller: m.price,
                  keyboardType: TextInputType.number,
                  decoration: InputDecoration(hintText: l.price, isDense: true),
                ),
              ),
              const SizedBox(width: 6),
              SizedBox(
                width: 60,
                child: TextField(
                  controller: m.percent,
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(suffixText: '%', isDense: true),
                ),
              ),
            ],
          ),
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(onPressed: _addMaster, icon: const Icon(Icons.person_add_alt_1_rounded, size: 18), label: Text(l.masterAdd)),
      ),
      FieldLabel(l.consumablesLabel),
      for (final c in _consumables)
        ListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: Text(c.name),
          subtitle: Text(qtyText(c.qty)),
          trailing: IconButton(icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _consumables.remove(c))),
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(onPressed: _addConsumable, icon: const Icon(Icons.add_rounded, size: 18), label: Text(l.consumableAdd)),
      ),
      SwitchListTile(contentPadding: EdgeInsets.zero, value: _online, onChanged: (v) => setState(() => _online = v), title: Text(l.onlineBooking)),
      Text(l.serviceLocalNote, style: TextStyle(color: pal.textSoft, fontSize: 12)),
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
