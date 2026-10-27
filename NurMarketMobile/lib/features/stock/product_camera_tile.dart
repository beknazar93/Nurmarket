import 'dart:typed_data';

import 'package:camera/camera.dart' show ResolutionPreset;
import 'package:drift/drift.dart' show Value;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/product_refs.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/audit.dart';
import '../../core/util/ui.dart';
import '../scan/live_shot.dart';
import '../scan/smart_camera.dart';

// 2026-10-27 (мобилка, М3): владелец — «в товарах в редактировании нет возможности обучения умной камеры!». «Обучить камеру»
// пряталась в «⋮». Теперь в карточке товара видна плитка «Умная камера»: счётчик эталонов, три места под снимки
// (спереди / сзади / сбоку) с превью и «Убрать», штрихкод и кнопка полного курса. Хранение то же — [ProductRefs]
// (телефон, refs.index); при продаже эталоны сверяются как раньше.

/// Подсказки к трём местам: что снять первым, вторым, третьим.
List<String> _angles(BuildContext context) => [context.l.ctAngleFront, context.l.ctAngleBack, context.l.ctAngleSide];

class ProductCameraTile extends ConsumerStatefulWidget {
  final Product product;

  /// Штрихкод, который сейчас в поле карточки (может быть ещё не сохранён).
  final String barcode;

  /// «Сканировать» штрихкод — карточка вставляет код в своё поле.
  final VoidCallback onScanBarcode;

  /// Полный курс обучения закончен (могла измениться карточка — штрихкод, фото).
  final VoidCallback? onTrained;

  /// Снять один кадр (для тестов подменяется; по умолчанию — камера приложения).
  final Future<Uint8List?> Function(BuildContext context, String title, String hint)? shoot;

  /// Полный курс «Обучить камеру» (для тестов подменяется; по умолчанию — [teachExistingProduct]).
  final Future<void> Function(BuildContext context, WidgetRef ref, Product product)? train;

  const ProductCameraTile({super.key, required this.product, required this.barcode, required this.onScanBarcode, this.onTrained, this.shoot, this.train});

  @override
  ConsumerState<ProductCameraTile> createState() => _ProductCameraTileState();
}

class _ProductCameraTileState extends ConsumerState<ProductCameraTile> {
  List<Uint8List?> _slots = List.filled(ProductRefs.slotCount, null);
  bool _busy = false;

  Product get p => widget.product;
  int get _count => _slots.where((s) => s != null).length;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  Future<void> _reload() async {
    final slots = await ProductRefs.slots(ref.read(databaseProvider), p.id);
    if (mounted) setState(() => _slots = slots);
  }

  Future<Uint8List?> _shoot(String title, String hint) {
    final custom = widget.shoot;
    if (custom != null) return custom(context, title, hint);
    return LiveShotPage.open(context, title: title, hint: hint, preset: ResolutionPreset.medium);
  }

  Future<void> _capture(int slot) async {
    if (_busy) return;
    final l = context.l;
    final bytes = await _shoot(l.ctTitle, l.ctShotHint(_angles(context)[slot]));
    if (bytes == null || !mounted) return;
    setState(() => _busy = true);
    try {
      await ProductRefs.setSlot(ref.read(databaseProvider), p.id, slot, bytes);
      RefCache.forget(p.id);
      Audit.log('product', '${l.ctTitle}: ${p.name} · +${slot + 1}');
      await _reload();
      if (mounted) toast(context, l.ctSaved);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _remove(int slot) async {
    if (_busy) return;
    final l = context.l;
    setState(() => _busy = true);
    try {
      await ProductRefs.clearSlot(ref.read(databaseProvider), p.id, slot);
      RefCache.forget(p.id);
      Audit.log('product', '${l.ctTitle}: ${p.name} · −${slot + 1}');
      await _reload();
      if (mounted) toast(context, l.ctRemoved);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _resetAll() async {
    final l = context.l;
    const btn = Size(64, 48);
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        key: const Key('camResetDialog'),
        title: Text(l.ctReset),
        content: Text(l.ctResetAsk),
        actions: [
          TextButton(style: TextButton.styleFrom(minimumSize: btn), onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(
            key: const Key('camResetYes'),
            style: FilledButton.styleFrom(backgroundColor: T.danger, foregroundColor: Colors.white, minimumSize: btn),
            onPressed: () => Navigator.pop(c, true),
            child: Text(l.ctReset),
          ),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    await ProductRefs.clearAll(ref.read(databaseProvider), p.id);
    RefCache.forget(p.id);
    Audit.log('product', '${l.ctReset}: ${p.name}');
    await _reload();
    if (mounted) toast(context, l.ctRemoved);
  }

  Future<void> _train() async {
    if (_busy) return;
    // штрихкод из поля карточки: курс спрашивает код, только если его нет и там
    final code = widget.barcode.trim();
    final target = p.copyWith(barcode: Value(code.isEmpty ? null : code));
    final run = widget.train ?? teachExistingProduct;
    await run(context, ref, target);
    if (!mounted) return;
    await _reload();
    widget.onTrained?.call();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final angles = _angles(context);
    final code = widget.barcode.trim();
    return Panel(
      key: const Key('cameraTile'),
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const Icon(Icons.center_focus_strong_rounded, size: 20),
              const SizedBox(width: 8),
              Expanded(child: Text(l.ctTitle, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15))),
              Semantics(
                liveRegion: true,
                child: Text(
                  l.ctCount(_count, ProductRefs.slotCount),
                  key: const Key('camTileCount'),
                  style: TextStyle(fontWeight: FontWeight.w700, color: _count == 0 ? pal.textSoft : T.success),
                ),
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(_count == 0 ? l.ctNone : l.ctTrained, style: TextStyle(fontSize: 12, color: pal.textSoft)),
          const SizedBox(height: 10),
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              for (var i = 0; i < ProductRefs.slotCount; i++) ...[
                if (i > 0) const SizedBox(width: 8),
                Expanded(child: _slot(context, i, angles[i])),
              ],
            ],
          ),
          const SizedBox(height: 6),
          // 4-й эталон — штрихкод: сам код хранится в карточке, здесь только состояние и быстрый скан
          ConstrainedBox(
            constraints: const BoxConstraints(minHeight: 48),
            child: Row(
              children: [
                Icon(code.isEmpty ? Icons.qr_code_2_rounded : Icons.check_circle_outline_rounded, size: 20, color: code.isEmpty ? pal.textSoft : T.success),
                const SizedBox(width: 8),
                Expanded(child: Text(code.isEmpty ? l.ctBarcodeNone : l.ctBarcodeOk(code), key: const Key('camBarcodeState'), style: const TextStyle(fontSize: 13))),
                TextButton(
                  key: const Key('camScanBarcode'),
                  style: TextButton.styleFrom(minimumSize: const Size(64, 48)),
                  onPressed: widget.onScanBarcode,
                  child: Text(l.peScan),
                ),
              ],
            ),
          ),
          const SizedBox(height: 6),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              FilledButton.tonalIcon(
                key: const Key('camTrain'),
                style: FilledButton.styleFrom(minimumSize: const Size(64, 48)),
                onPressed: _busy ? null : _train,
                icon: const Icon(Icons.school_outlined, size: 20),
                label: Text(l.ctTrain),
              ),
              if (_count > 0)
                OutlinedButton.icon(
                  key: const Key('camReset'),
                  style: OutlinedButton.styleFrom(minimumSize: const Size(64, 48), foregroundColor: T.danger),
                  onPressed: _busy ? null : _resetAll,
                  icon: const Icon(Icons.delete_sweep_outlined, size: 20),
                  label: Text(l.ctReset),
                ),
            ],
          ),
          const SizedBox(height: 6),
          Text(l.ctNote, style: TextStyle(fontSize: 11, color: pal.textSoft)),
        ],
      ),
    );
  }

  Widget _slot(BuildContext context, int i, String angle) {
    final l = context.l;
    final pal = context.pal;
    final img = _slots[i];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Semantics(
          button: true,
          label: img == null ? l.ctSlotAdd(i + 1, angle) : l.ctSlotRetake(i + 1),
          excludeSemantics: true,
          child: InkWell(
            key: Key('camSlot$i'),
            borderRadius: BorderRadius.circular(10),
            onTap: _busy ? null : () => _capture(i),
            child: Container(
              height: 88,
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(10),
                border: Border.all(color: img == null ? pal.border : T.success, width: img == null ? 1 : 1.5),
              ),
              clipBehavior: Clip.antiAlias,
              child: img == null
                  ? Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Icon(Icons.add_a_photo_outlined, color: pal.textSoft),
                        const SizedBox(height: 4),
                        Padding(
                          padding: const EdgeInsets.symmetric(horizontal: 4),
                          child: Text(angle, textAlign: TextAlign.center, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: pal.textSoft)),
                        ),
                      ],
                    )
                  : Image.memory(
                      img,
                      key: Key('camSlotImage$i'),
                      fit: BoxFit.cover,
                      cacheWidth: 240,
                      gaplessPlayback: true,
                      errorBuilder: (_, _, _) => const Center(child: Icon(Icons.image_outlined)),
                    ),
            ),
          ),
        ),
        SizedBox(
          height: 48,
          child: img == null
              ? Center(child: Text(l.ctSlot(i + 1), style: TextStyle(fontSize: 12, color: pal.textSoft)))
              : Semantics(
                  button: true,
                  label: l.ctRemove,
                  excludeSemantics: true,
                  child: IconButton(
                    key: Key('camSlotRemove$i'),
                    tooltip: l.ctRemove,
                    style: IconButton.styleFrom(minimumSize: const Size(48, 48), foregroundColor: T.danger),
                    onPressed: _busy ? null : () => _remove(i),
                    icon: const Icon(Icons.delete_outline_rounded),
                  ),
                ),
        ),
      ],
    );
  }
}
