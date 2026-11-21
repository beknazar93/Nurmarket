import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/api/api_client.dart';
import '../../core/catalog/global_products.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/units.dart' show unitOrPcs;
import '../../core/util/ui.dart';
import '../stock/receive_lookup.dart';
import '../stock/stock_repository.dart';
import '../stock/stock_screens.dart' show receiveScanHit;
import 'quick_product.dart';
import 'simple_sheets.dart';

/// 2026-11-20 (Простой режим, М11): «Приход одним сканом» — навёл камеру на штрихкод → «Сколько пришло» (1 / 5 / 10 / 20 / 50 / 100 или своё число)
/// → «Принять». Остаток прибавляется тем же механизмом, что приёмка сканером (`StockRepository.postReceiving`: очередь без связи, ключ
/// идемпотентности). Приёмка без поставщика: этот запрос поставщика не принимает — приход с поставщиком и накладной остаётся в «Ещё».
class SimpleReceiveScreen extends ConsumerStatefulWidget {
  /// Только для проверок без камеры: код, который «отсканировал» кассир.
  @visibleForTesting
  final Future<String?> Function()? debugScan;
  const SimpleReceiveScreen({super.key, this.debugScan});
  @override
  ConsumerState<SimpleReceiveScreen> createState() => _SimpleReceiveScreenState();
}

class _Accepted {
  final String name;
  final double qty;
  final String unit;
  const _Accepted(this.name, this.qty, this.unit);
}

class _SimpleReceiveScreenState extends ConsumerState<SimpleReceiveScreen> {
  final _search = TextEditingController();
  Timer? _debounce;
  List<Product> _found = [];
  final List<_Accepted> _done = [];
  bool _busy = false;

  @override
  void dispose() {
    _debounce?.cancel();
    _search.dispose();
    super.dispose();
  }

  GlobalProducts? get _globals {
    try {
      return ref.read(globalProductsProvider);
    } catch (_) {
      return null;
    }
  }

  Future<void> _scan() async {
    if (_busy) return;
    final code = await (widget.debugScan?.call() ?? Navigator.of(context).push<String>(MaterialPageRoute(builder: (_) => const OneScanPage())));
    if (code == null || code.isEmpty || !mounted) return;
    setState(() => _busy = true);
    try {
      final db = ref.read(databaseProvider);
      final hit = await receiveScanHit(db, code);
      Product? product = hit?.product;
      var per = hit?.qty ?? 1.0;
      if (product == null) {
        final f = await resolveReceiveCode(db, ref.read(apiProvider), _globals, code);
        if (f.kind == ReceiveKind.found) {
          product = f.product;
          per = f.qty;
        }
      }
      if (!mounted) return;
      if (product == null) {
        await _unknown(code);
        return;
      }
      await _askAndAccept(product, initial: per > 1 ? per : null);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Кода нет в каталоге: завести товар (фото, название, цена, количество) с этим штрихкодом.
  Future<void> _unknown(String code) async {
    final yes = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(c.t5('Товар не найден', 'Товар табылган жок', 'Mahsulot topilmadi', 'Product not found', 'Ürün bulunamadı')),
        content: Text(
          c.t5(
            'Штрихкод $code не найден в каталоге. Завести новый товар?',
            '$code штрихкоду каталогдо жок. Жаңы товар түзөлүбү?',
            '$code shtrixkodi katalogda yo\'q. Yangi mahsulot qo\'shamizmi?',
            'Barcode $code is not in the catalog. Create a new product?',
            '$code barkodu katalogda yok. Yeni ürün eklensin mi?',
          ),
        ),
        actions: [
          TextButton(
            style: TextButton.styleFrom(minimumSize: const Size(64, 48)),
            onPressed: () => Navigator.pop(c, false),
            child: Text(c.l.cancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size(64, 48)),
            onPressed: () => Navigator.pop(c, true),
            child: Text(c.t5('Новый товар', 'Жаңы товар', 'Yangi mahsulot', 'New product', 'Yeni ürün')),
          ),
        ],
      ),
    );
    if (yes == true && mounted) await showQuickProduct(context, barcode: code);
  }

  Future<void> _askAndAccept(Product p, {double? initial}) async {
    final qty = await askArrived(context, product: p, initial: initial);
    if (qty == null || qty <= 0 || !mounted) return;
    final l = context.l;
    try {
      final sent = await ref.read(stockRepoProvider).postReceiving([StockLine(productId: p.id, name: p.name, before: p.quantity, qty: qty, unit: unitOrPcs(p.unit))]);
      if (!mounted) return;
      setState(() => _done.insert(0, _Accepted(p.name, qty, p.unit)));
      toast(context, sent ? l.receivedOk : l.receivedQueued);
    } on ApiException catch (e) {
      if (mounted) toast(context, l.receiveNotAccepted(e.message), error: true);
    }
  }

  Future<void> _find() async {
    final q = _search.text.trim();
    if (q.isEmpty) {
      setState(() => _found = []);
      return;
    }
    final r = await ref.read(databaseProvider).searchProducts(q, limit: 30);
    if (mounted) setState(() => _found = r);
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
      children: [
        Text(context.t5('Приход товара', 'Товар кирешеси', 'Tovar kirimi', 'Receive goods', 'Mal girişi'), style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
        const SizedBox(height: 12),
        FilledButton.icon(
          key: const Key('receiveScan'),
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(88),
            textStyle: const TextStyle(fontSize: 19, fontWeight: FontWeight.w800),
          ),
          onPressed: _busy ? null : _scan,
          icon: const Icon(Icons.qr_code_scanner_rounded, size: 34),
          label: Text(
            context.t5('Навести камеру на штрихкод', 'Камераны штрихкодго багыттаңыз', 'Kamerani shtrixkodga qarating', 'Point the camera at a barcode', 'Kamerayı barkoda tutun'),
          ),
        ),
        const SizedBox(height: 16),
        TextField(
          key: const Key('receiveSearch'),
          controller: _search,
          style: const TextStyle(fontSize: 16),
          onChanged: (_) {
            _debounce?.cancel();
            _debounce = Timer(const Duration(milliseconds: 300), _find);
          },
          decoration: InputDecoration(
            hintText: context.t5('Найти по названию', 'Аталышы боюнча издөө', "Nomi bo'yicha qidirish", 'Find by name', 'Ada göre ara'),
            prefixIcon: const Icon(Icons.search_rounded),
          ),
        ),
        for (final p in _found)
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Material(
              color: pal.panel,
              borderRadius: BorderRadius.circular(12),
              child: ListTile(
                key: Key('receivePick_${p.id}'),
                minVerticalPadding: 12,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(12),
                  side: BorderSide(color: pal.border),
                ),
                title: Text(
                  p.name,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontWeight: FontWeight.w700),
                ),
                subtitle: Text('${context.t5('Сейчас', 'Азыр', 'Hozir', 'Now', 'Şimdi')}: ${qtyText(p.quantity)} ${p.unit}'),
                trailing: const Icon(Icons.add_circle_outline_rounded),
                onTap: () => _askAndAccept(p),
              ),
            ),
          ),
        if (_done.isNotEmpty) ...[
          const SizedBox(height: 18),
          Text(
            context.t5('Принято сейчас', 'Азыр кабыл алынды', 'Hozir qabul qilindi', 'Just received', 'Az önce alındı'),
            style: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w700),
          ),
          for (final d in _done.take(8))
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Row(
                children: [
                  Icon(Icons.check_circle_rounded, color: pal.success, size: 22),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(d.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 15)),
                  ),
                  Text(
                    '+${qtyText(d.qty)} ${d.unit}',
                    key: const Key('receiveDone'),
                    style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: pal.success),
                  ),
                ],
              ),
            ),
        ],
      ],
    );
  }
}

/// «Сколько пришло»: кнопки 1 / 5 / 10 / 20 / 50 / 100 и своё число (системная цифровая клавиатура). Возвращает количество.
Future<double?> askArrived(BuildContext context, {required Product product, double? initial}) => showModalBottomSheet<double>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  useSafeArea: true,
  builder: (c) => _ArrivedSheet(product: product, initial: initial),
);

class _ArrivedSheet extends StatefulWidget {
  final Product product;
  final double? initial;
  const _ArrivedSheet({required this.product, this.initial});
  @override
  State<_ArrivedSheet> createState() => _ArrivedSheetState();
}

class _ArrivedSheetState extends State<_ArrivedSheet> {
  late final _ctl = TextEditingController(text: widget.initial == null ? '' : qtyText(widget.initial!).replaceAll(' ', ''));
  @override
  void dispose() {
    _ctl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final p = widget.product;
    final qty = parseNum(_ctl.text);
    final ok = qty != null && qty > 0;
    return Padding(
      padding: EdgeInsets.fromLTRB(16, 0, 16, 16 + MediaQuery.viewInsetsOf(context).bottom),
      child: PinnedSheet(
        footer: FilledButton(
          key: const Key('arrivedOk'),
          style: bigButton(min: const Size.fromHeight(60)),
          onPressed: ok ? () => Navigator.pop(context, qty) : null,
          child: Text(context.t5('Принять', 'Кабыл алуу', 'Qabul qilish', 'Accept', 'Kabul et')),
        ),
        children: [
          Text(
            context.t5('Сколько пришло?', 'Канча келди?', 'Qancha keldi?', 'How many arrived?', 'Kaç adet geldi?'),
            style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800),
          ),
          const SizedBox(height: 4),
          Text(
            p.name,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700),
          ),
          Text(
            '${context.t5('Сейчас на складе', 'Азыр кампада', 'Hozir omborda', 'In stock now', 'Şu an stokta')}: ${qtyText(p.quantity)} ${p.unit}',
            style: TextStyle(color: pal.textSoft),
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final n in const [1, 5, 10, 20, 50, 100])
                SizedBox(
                  height: 56,
                  child: OutlinedButton(
                    key: Key('arrived_$n'),
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size(88, 56),
                      textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
                    ),
                    onPressed: () => setState(() => _ctl.text = '$n'),
                    child: Text('$n'),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 12),
          TextField(
            key: const Key('arrivedField'),
            controller: _ctl,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: amountFormatters,
            onChanged: (_) => setState(() {}),
            style: const TextStyle(fontSize: 26, fontWeight: FontWeight.w800),
            decoration: InputDecoration(labelText: context.t5('Своё число', 'Өз санын', 'O\'z soningiz', 'Your number', 'Kendi sayınız'), suffixText: p.unit),
          ),
        ],
      ),
    );
  }
}

/// Камера для ОДНОГО кода: первый распознанный штрихкод закрывает экран и возвращается.
class OneScanPage extends StatefulWidget {
  const OneScanPage({super.key});
  @override
  State<OneScanPage> createState() => _OneScanPageState();
}

class _OneScanPageState extends State<OneScanPage> {
  final _camera = MobileScannerController(
    formats: const [BarcodeFormat.ean13, BarcodeFormat.ean8, BarcodeFormat.code128, BarcodeFormat.upcA, BarcodeFormat.upcE, BarcodeFormat.itf14, BarcodeFormat.qrCode],
    detectionSpeed: DetectionSpeed.normal,
    detectionTimeoutMs: 100,
    returnImage: false,
    autoZoom: true,
  );
  bool _got = false;

  @override
  void dispose() {
    _camera.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture cap) {
    if (_got) return;
    final v = cap.barcodes.map((b) => b.rawValue).whereType<String>().firstWhere((s) => s.isNotEmpty, orElse: () => '');
    if (v.isEmpty) return;
    _got = true;
    Navigator.of(context).pop(v);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      body: Stack(
        children: [
          Positioned.fill(
            child: MobileScanner(
              controller: _camera,
              onDetect: _onDetect,
              errorBuilder: (c, e) => Center(
                child: Padding(
                  padding: const EdgeInsets.all(24),
                  child: Text(
                    '${c.l.cameraUnavailable}\n$e',
                    textAlign: TextAlign.center,
                    style: const TextStyle(color: Colors.white),
                  ),
                ),
              ),
            ),
          ),
          SafeArea(
            child: Align(
              alignment: Alignment.topCenter,
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Text(
                  context.t5('Наведите на штрихкод', 'Штрихкодго багыттаңыз', 'Shtrixkodga qarating', 'Point at a barcode', 'Barkoda tutun'),
                  style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w800),
                ),
              ),
            ),
          ),
          SafeArea(
            child: Align(
              alignment: Alignment.bottomCenter,
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: FilledButton(
                  key: const Key('oneScanClose'),
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(60)),
                  onPressed: () => Navigator.of(context).pop(),
                  child: Text(context.l.cancel),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
