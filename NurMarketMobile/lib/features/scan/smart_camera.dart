import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ai/vision.dart';
import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/admin_receive.dart';
import '../sale/sale_screen.dart';
import 'live_shot.dart';
import '../../core/ai/product_refs.dart';
import '../../core/util/audit.dart';
import 'scan_screen.dart';

/// 13.10, доски Т9–Т10 и владелец: «умная камера при продаже в телефоне где?». Снимок системной камерой (без штрихкода),
/// товары узнаёт ИИ (VisionService), дальше — в чек (продажа) или в приёмку (полка, накладная, этикетки).
/// 14.10, владелец: «умная камера в виде сканера, а не фото» — видоискатель как у сканера (LiveShotPage) вместо
/// системной камеры: кадр с живого изображения, «Авто» — сам через 2 секунды.
Future<Uint8List?> takeProductPhoto(BuildContext context, String mode) {
  final l = context.l;
  final hint = switch (mode) {
    'shelf' => l.liveHintShelf,
    'invoice' => l.liveHintInvoice,
    'labels' => l.liveHintLabels,
    _ => l.liveHintSale,
  };
  return LiveShotPage.open(context, title: l.smartCam, hint: hint);
}

/// Ключа ИИ нет ни на сервере, ни на телефоне — предлагаем вставить (как поле ключа в настройках кассы).
Future<bool> ensureAiKey(BuildContext context, WidgetRef ref) async {
  final vision = ref.read(visionProvider);
  if (await vision.key() != null) return true;
  if (!context.mounted) return false;
  final l = context.l;
  final ctrl = TextEditingController();
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(l.smartCam),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(l.aiKeyNeeded),
          const SizedBox(height: 10),
          TextField(
            controller: ctrl,
            decoration: InputDecoration(labelText: l.aiKeyLabel),
            obscureText: true,
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(MaterialLocalizations.of(c).cancelButtonLabel)),
        FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(MaterialLocalizations.of(c).okButtonLabel)),
      ],
    ),
  );
  if (ok != true || ctrl.text.trim().isEmpty) return false;
  await vision.saveKey(ctrl.text);
  return true;
}

/// Снимок → распознавание → совпадения в каталоге; ожидание — окно «Узнаю товар…».
Future<List<SeenMatch>?> photoAndRecognize(BuildContext context, WidgetRef ref, String mode) async {
  if (!await ensureAiKey(context, ref) || !context.mounted) return null;
  final jpeg = await takeProductPhoto(context, mode);
  if (jpeg == null || !context.mounted) return null;
  final l = context.l;
  showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (c) => AlertDialog(
      content: Row(
        children: [
          const CircularProgressIndicator(),
          const SizedBox(width: 16),
          Expanded(child: Text(l.recognizing)),
        ],
      ),
    ),
  );
  try {
    final vision = ref.read(visionProvider);
    final seen = await vision.recognize(jpeg, mode: mode);
    var matches = await vision.match(seen);
    // 14.10, владелец: обученные товары (3 фото + штрихкод) — точное совпадение с эталонами, первыми в списке.
    if (mode == 'sale') {
      final exact = await exactMatches(ref, jpeg, matches);
      if (exact.isNotEmpty) {
        final ids = {for (final e in exact) e.candidates.first.$1.id};
        matches = [...exact, ...matches.where((m) => m.best == null || !ids.contains(m.best!.id))];
      }
    }
    if (context.mounted) Navigator.of(context, rootNavigator: true).pop();
    return matches;
  } on VisionException catch (e) {
    if (context.mounted) {
      Navigator.of(context, rootNavigator: true).pop();
      if (e.noKey) {
        // ключ не подошёл — забываем его, в следующий раз спросим заново
        toast(context, l.aiKeyNeeded, error: true);
        await ref.read(visionProvider).saveKey('');
      } else {
        toast(context, e.message, error: true);
      }
    }
    return null;
  }
}

/// Кадр против эталонов: обученные на этом телефоне товары (3 снимка) и, для найденных первым шагом, фото из NurCRM.
/// Не больше 8 товаров — запрос к ИИ остаётся быстрым.
Future<List<SeenMatch>> exactMatches(WidgetRef ref, Uint8List shot, List<SeenMatch> first) async {
  try {
    final db = ref.read(databaseProvider);
    final trained = await ProductRefs.trainedIds(db);
    final ordered = <String>[
      for (final m in first)
        for (final (p, _) in m.candidates) p.id,
    ];
    final ids = <String>{...ordered.where(trained.contains), ...trained.where((id) => !ordered.contains(id))}.take(8).toList();
    final refs = <(Product, List<Uint8List>)>[];
    for (final id in ids) {
      final p = await db.productById(id);
      final pngs = await ProductRefs.load(db, id);
      if (p != null && pngs.isNotEmpty) refs.add((p, pngs));
    }
    // товары, обученные на другом телефоне: их фото в NurCRM
    for (final m in first) {
      for (final (p, _) in m.candidates.take(2)) {
        if (refs.length >= 8 || trained.contains(p.id) || refs.any((r) => r.$1.id == p.id) || p.imageUrl == null) continue;
        final png = await ProductRefs.fromUrl(p.imageUrl!);
        if (png != null) refs.add((p, [png]));
      }
    }
    if (refs.isEmpty) return const [];
    final found = await ref.read(visionProvider).identify(shot, [for (final r in refs) (r.$1.name, r.$2)]);
    return [
      for (final (i, count) in found) SeenMatch(SeenItem(name: '✓ ${refs[i].$1.name}', count: count), [(refs[i].$1, 1.0)]),
    ];
  } catch (_) {
    return const [];
  }
}

/// 14.10, владелец: обучение умной камеры — 3 снимка видоискателем (справа, слева, сверху) и штрихкод.
Future<({List<Uint8List> shots, String? barcode})?> teachCamera(BuildContext context, {bool askBarcode = true}) async {
  final l = context.l;
  final shots = <Uint8List>[];
  for (final title in [l.teachRight, l.teachLeft, l.teachTop]) {
    if (!context.mounted) return null;
    final shot = await LiveShotPage.open(context, title: title, hint: l.teachHint);
    if (shot == null) return null;
    shots.add(shot);
  }
  String? barcode;
  if (askBarcode && context.mounted) {
    final codes = await Navigator.of(context)
        .push<List<String>>(MaterialPageRoute(builder: (_) => const ScanScreen(initial: ScanMode.receive, returnCodes: true)));
    if (codes != null && codes.isNotEmpty) barcode = codes.last;
  }
  return (shots: shots, barcode: barcode);
}

/// Обучить камеру на уже существующем товаре (склад → действия товара).
Future<void> teachExistingProduct(BuildContext context, WidgetRef ref, Product p) async {
  final r = await teachCamera(context, askBarcode: p.barcode == null || p.barcode!.isEmpty);
  if (r == null || !context.mounted) return;
  final l = context.l;
  await ProductRefs.save(ref.read(databaseProvider), p.id, r.shots);
  try {
    if (r.barcode != null) await ref.read(apiProvider).patch('api/main/products/${p.id}/', body: {'barcode': r.barcode});
    if (p.imageUrl == null) await ref.read(adminReceiveRepoProvider).uploadImage(p.id, r.shots.last, primary: true);
  } on ApiException catch (e) {
    if (context.mounted) toast(context, e.message, error: true);
  }
  Audit.log('product', '${l.teachCamera}: ${p.name}');
  if (context.mounted) toast(context, l.teachDone);
}

/// Продажа «По виду»: увиденное и подходящие товары каталога — нажатие кладёт товар в чек (сколько штук видно на снимке).
Future<void> recognizeForSale(BuildContext context, WidgetRef ref) async {
  final matches = await photoAndRecognize(context, ref, 'sale');
  if (matches == null || !context.mounted) return;
  final l = context.l;
  if (matches.isEmpty) {
    toast(context, l.recognizedNone, error: true);
    return;
  }
  await showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    builder: (c) => SafeArea(
      child: ConstrainedBox(
        constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(c).height * .75),
        child: ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
          children: [
            Text(l.seenOnPhoto, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
            for (final m in matches) ...[
              const SizedBox(height: 10),
              Text(
                '${m.seen.title} · ×${m.seen.count}${m.seen.name.startsWith('✓') ? ' · ${l.exactMatch}' : ''}',
                style: TextStyle(color: m.seen.name.startsWith('✓') ? T.success : c.pal.textSoft, fontWeight: FontWeight.w700),
              ),
              if (m.candidates.isEmpty) Text(l.notInCatalog, style: const TextStyle(color: T.warn)),
              for (final (p, score) in m.candidates)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: ProductThumb(name: p.name, url: p.imageUrl, size: 44),
                  title: Text(p.name, style: TextStyle(fontWeight: score >= .5 ? FontWeight.w800 : FontWeight.w500)),
                  subtitle: Text('${money(p.price)} ${l.som} · ${l.inStock(qtyText(p.quantity), p.unit)}'),
                  trailing: const Icon(Icons.add_shopping_cart_rounded),
                  onTap: () async {
                    await addProduct(c, ref, p, qty: p.isWeight ? null : m.seen.count.toDouble());
                    if (c.mounted) toast(c, l.addedToCheckName(p.name));
                  },
                ),
            ],
          ],
        ),
      ),
    ),
  );
}

/// Т9–Т10: снимки полки, накладной или этикеток → проверка: «есть в базе» (+ к остатку) и «новые» (название, закупка, цена)
/// → «Загрузить на склад» сразу или «В приёмку с поставщиком» (ПР2–ПР6). 13.10, владелец: «дизайн умной камеры продумай»,
/// «количество не вводится» — у каждой строки −/+ и ввод числа, у новых — поля названия, закупки и цены.
class _CamItem {
  final SeenMatch seen;
  Product? product;
  double qty;
  final TextEditingController name;
  final TextEditingController cost;
  final TextEditingController price;
  _CamItem(this.seen)
    : product = seen.best,
      qty = seen.seen.count.toDouble(),
      name = TextEditingController(text: seen.seen.title),
      cost = TextEditingController(text: seen.seen.labelPrice > 0 ? seen.seen.labelPrice.toStringAsFixed(0) : ''),
      price = TextEditingController();
}

class SmartCameraScreen extends ConsumerStatefulWidget {
  const SmartCameraScreen({super.key});
  @override
  ConsumerState<SmartCameraScreen> createState() => _SmartCameraScreenState();
}

class _SmartCameraScreenState extends ConsumerState<SmartCameraScreen> {
  String _mode = 'shelf';
  final List<_CamItem> _items = [];
  int _shots = 0;
  bool _busy = false;

  Future<void> _shoot() async {
    final found = await photoAndRecognize(context, ref, _mode);
    if (found == null || !mounted) return;
    if (found.isEmpty) toast(context, context.l.recognizedNone, error: true);
    setState(() {
      _shots++;
      for (final m in found) {
        final same = m.best == null ? null : _items.where((x) => x.product?.id == m.best!.id).firstOrNull;
        if (same != null) {
          same.qty += m.seen.count;
        } else {
          _items.add(_CamItem(m));
        }
      }
    });
  }

  static double _autoPrice(double cost) => cost <= 0 ? 0 : ((cost * 1.3 / 5).ceil() * 5).toDouble();

  List<ReceiveLine> _lines() => [
    for (final x in _items.where((x) => x.qty > 0))
      if (x.product case final p?)
        ReceiveLine(
          productId: p.id,
          name: p.name,
          barcode: p.barcode ?? '',
          unit: p.unit,
          before: p.quantity,
          oldPrice: p.price,
          price: p.price,
          qty: x.qty,
          cost: parseNum(x.cost.text) ?? 0,
        )
      else
        ReceiveLine(
          productId: '',
          name: x.name.text.trim(),
          barcode: x.seen.seen.barcode,
          qty: x.qty,
          cost: parseNum(x.cost.text) ?? 0,
          price: parseNum(x.price.text) ?? _autoPrice(parseNum(x.cost.text) ?? 0),
          isNew: true,
        ),
  ];

  Future<void> _upload() async {
    final l = context.l;
    final lines = _lines().where((x) => x.name.isNotEmpty && (!x.isNew || x.price > 0)).toList();
    if (lines.isEmpty) return;
    setState(() => _busy = true);
    try {
      final (n, errors) = await ref.read(adminReceiveRepoProvider).post(lines, null, false);
      await ref.read(catalogRepoProvider).sync();
      if (!mounted) return;
      toast(context, errors.isEmpty ? l.smartUploaded(n) : '${l.smartUploaded(n)} · ${errors.take(2).join('; ')}', error: errors.isNotEmpty);
      if (errors.isEmpty) Navigator.of(context).maybePop();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _editQty(_CamItem x) async {
    final v = await askNumber(context, title: x.product?.name ?? x.name.text, initial: x.qty);
    if (v != null) setState(() => x.qty = v.clamp(0, 99999).toDouble());
  }

  Widget _qtyRow(_CamItem x) {
    final pal = context.pal;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        IconButton.outlined(onPressed: () => setState(() => x.qty = (x.qty - 1).clamp(0, 99999).toDouble()), icon: const Icon(Icons.remove)),
        InkWell(
          onTap: () => _editQty(x),
          borderRadius: BorderRadius.circular(10),
          child: Container(
            constraints: const BoxConstraints(minWidth: 56),
            height: 40,
            alignment: Alignment.center,
            margin: const EdgeInsets.symmetric(horizontal: 4),
            decoration: BoxDecoration(color: pal.window, borderRadius: BorderRadius.circular(10)),
            child: Text(qtyText(x.qty), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
          ),
        ),
        IconButton.filled(
          style: IconButton.styleFrom(backgroundColor: pal.action),
          onPressed: () => setState(() => x.qty += 1),
          icon: Icon(Icons.add, color: pal.onAction),
        ),
      ],
    );
  }

  Widget _knownCard(_CamItem x) {
    final pal = context.pal;
    final p = x.product!;
    return Panel(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              ProductThumb(name: p.name, url: p.imageUrl, size: 44),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      p.name,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontWeight: FontWeight.w700),
                    ),
                    Text(
                      '${context.l.onStockLabel}: ${qtyText(p.quantity)} ${p.unit} · ${money(p.price)}',
                      style: TextStyle(color: pal.textSoft, fontSize: 12),
                    ),
                  ],
                ),
              ),
              IconButton(icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _items.remove(x))),
            ],
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Text(
                '+ ',
                style: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w800),
              ),
              _qtyRow(x),
            ],
          ),
        ],
      ),
    );
  }

  Widget _newCard(_CamItem x) {
    final l = context.l;
    final pal = context.pal;
    final cost = parseNum(x.cost.text) ?? 0;
    return Panel(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                decoration: BoxDecoration(color: T.accent, borderRadius: BorderRadius.circular(8)),
                child: Text(
                  l.newItemShort,
                  style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 11.5),
                ),
              ),
              if (x.seen.seen.barcode.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(left: 8),
                  child: Text(x.seen.seen.barcode, style: TextStyle(color: pal.textSoft, fontSize: 12)),
                ),
              const Spacer(),
              IconButton(icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _items.remove(x))),
            ],
          ),
          TextField(
            controller: x.name,
            decoration: InputDecoration(labelText: l.clientName, isDense: true),
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: x.cost,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  onChanged: (_) => setState(() {}),
                  decoration: InputDecoration(labelText: l.purchasePrice, isDense: true),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: TextField(
                  controller: x.price,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(labelText: l.salePrice, hintText: cost > 0 ? money(_autoPrice(cost)) : null, isDense: true),
                ),
              ),
            ],
          ),
          const SizedBox(height: 6),
          _qtyRow(x),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final known = _items.where((x) => x.product != null).toList();
    final fresh = _items.where((x) => x.product == null).toList();
    final count = _items.where((x) => x.qty > 0).length;
    return Scaffold(
      appBar: ScreenTitle(l.smartCam, subtitle: _shots > 0 ? l.smartShots(_shots) : null),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 140),
        children: [
          SegmentedButton<String>(
            segments: [
              ButtonSegment(value: 'shelf', icon: const Icon(Icons.shelves, size: 18), label: Text(l.smartShelf)),
              ButtonSegment(value: 'invoice', icon: const Icon(Icons.receipt_long_rounded, size: 18), label: Text(l.smartInvoice)),
              ButtonSegment(value: 'labels', icon: const Icon(Icons.sell_outlined, size: 18), label: Text(l.smartLabels)),
            ],
            selected: {_mode},
            onSelectionChanged: (s) => setState(() => _mode = s.first),
          ),
          const SizedBox(height: 12),
          // большая «кнопка-видоискатель»: снимок системной камерой
          InkWell(
            onTap: _busy ? null : _shoot,
            borderRadius: BorderRadius.circular(22),
            child: Container(
              height: _items.isEmpty ? 220 : 120,
              decoration: BoxDecoration(
                color: T.navy,
                borderRadius: BorderRadius.circular(22),
                border: Border.all(color: T.accent.withValues(alpha: .6), width: 2),
              ),
              child: Stack(
                children: [
                  for (final (a, b) in [(Alignment.topLeft, 0.0), (Alignment.topRight, 1.0), (Alignment.bottomLeft, 2.0), (Alignment.bottomRight, 3.0)])
                    Align(
                      alignment: a,
                      child: Padding(
                        padding: const EdgeInsets.all(14),
                        child: Transform.rotate(
                          angle: b * 1.5708,
                          child: const Icon(Icons.crop_free_rounded, color: T.accent, size: 22),
                        ),
                      ),
                    ),
                  Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Container(
                          width: 64,
                          height: 64,
                          decoration: const BoxDecoration(color: T.accent, shape: BoxShape.circle),
                          child: const Icon(Icons.photo_camera_rounded, color: T.navy, size: 32),
                        ),
                        const SizedBox(height: 8),
                        Text(
                          _items.isEmpty ? l.takePhoto : l.morePhoto,
                          style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800),
                        ),
                        if (_items.isEmpty)
                          Padding(
                            padding: const EdgeInsets.fromLTRB(24, 4, 24, 0),
                            child: Text(
                              l.smartHint,
                              textAlign: TextAlign.center,
                              style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12),
                            ),
                          ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
          if (known.isNotEmpty) ...[
            FieldLabel(l.seenInBase(known.length)),
            for (final x in known) Padding(padding: const EdgeInsets.only(bottom: 6), child: _knownCard(x)),
          ],
          if (fresh.isNotEmpty) ...[
            FieldLabel(l.seenNew(fresh.length)),
            Text(l.smartPriceRule, style: TextStyle(color: pal.textSoft, fontSize: 12)),
            const SizedBox(height: 4),
            for (final x in fresh) Padding(padding: const EdgeInsets.only(bottom: 6), child: _newCard(x)),
          ],
        ],
      ),
      bottomNavigationBar: _items.isEmpty
          ? null
          : SafeArea(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    FilledButton(
                      style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
                      onPressed: _busy || count == 0 ? null : _upload,
                      child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.smartUpload(count)),
                    ),
                    const SizedBox(height: 6),
                    OutlinedButton(
                      onPressed: _busy
                          ? null
                          : () async {
                              final done = await context.push<bool>('/admin/receive', extra: _lines());
                              if (done == true && context.mounted) Navigator.of(context).maybePop();
                            },
                      child: Text(l.smartToReceive),
                    ),
                  ],
                ),
              ),
            ),
    );
  }
}
