// 2026-10-27 (мобилка, видеозвонок, М2): панель мастера «добавить товар» на экране звонка — полоса из четырёх слотов с мини-снимками
// «Спереди / Сзади / Сбоку / Штрихкод», кнопка «Снять» (48 dp), «Создать товар» (когда есть название и цена) и «Отмена».
// Тот же мастер ведёт и голос (инструмент product_wizard); панель только показывает состояние и даёт кнопки. Снимки живут в
// памяти звонка и нигде не сохраняются, пока товар не создан.
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../core/ai/assistant/product_wizard.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'call_controller.dart';

class WizardPanel extends StatelessWidget {
  final AssistantCallController c;
  const WizardPanel({super.key, required this.c});

  @override
  Widget build(BuildContext context) {
    final w = c.wizard;
    if (w == null || !w.active) return const SizedBox.shrink();
    final l = context.l;
    final pal = context.pal;
    final next = w.nextSide ?? WizardSide.barcode;
    final slots = <(WizardSide, String)>[
      (WizardSide.front, l.wizSlotFront),
      (WizardSide.back, l.wizSlotBack),
      (WizardSide.side, l.wizSlotSide),
      (WizardSide.barcode, l.wizSlotCode),
    ];
    String sideName(WizardSide s) => slots.firstWhere((e) => e.$1 == s, orElse: () => (s, l.wizSlotSide)).$2;
    // «Снять» снимает следующую недостающую сторону; когда три стороны есть — штрихкод
    final target = (w.barcode.isNotEmpty || w.barcodeSkipped) && w.nextSide == null ? null : next;
    return Material(
      color: pal.panel,
      borderRadius: BorderRadius.circular(T.radiusCard),
      child: Container(
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(T.radiusCard),
          border: Border.all(color: pal.border),
        ),
        padding: const EdgeInsets.fromLTRB(12, 8, 8, 10),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(l.wizTitle, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800)),
                ),
                if (w.barcode.isEmpty && !w.barcodeSkipped)
                  IconButton(onPressed: w.busy ? null : () => _askCode(context), tooltip: l.wizEnterCode, icon: const Icon(Icons.keyboard_alt_outlined)),
                IconButton(onPressed: c.wizardCancel, tooltip: l.cancel, icon: const Icon(Icons.close_rounded)),
              ],
            ),
            Row(
              children: [
                for (final s in slots)
                  Expanded(
                    child: Padding(
                      padding: const EdgeInsets.only(right: 6),
                      child: _Slot(
                        label: s.$2,
                        bytes: w.shots[s.$1],
                        current: s.$1 == next && w.shots[s.$1] == null,
                        done: s.$1 == WizardSide.barcode ? (w.barcode.isNotEmpty || w.barcodeSkipped) : w.shots[s.$1] != null,
                        semantics: l.wizPhotoAlt(s.$2),
                      ),
                    ),
                  ),
              ],
            ),
            if (w.duplicate != null)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(
                  l.wizDuplicate(w.duplicate!.name),
                  style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: pal.warn),
                ),
              )
            else if (w.name.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(
                  (w.price ?? 0) > 0
                      ? l.wizDraft(w.name, w.price! == w.price!.roundToDouble() ? w.price!.toStringAsFixed(0) : w.price!.toStringAsFixed(2))
                      : w.name,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700),
                ),
              )
            else
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(l.wizHint, style: TextStyle(fontSize: 12, color: pal.textSoft)),
              ),
            const SizedBox(height: 8),
            Row(
              children: [
                if (target != null)
                  Expanded(
                    child: FilledButton.icon(
                      style: FilledButton.styleFrom(minimumSize: const Size(0, 48)),
                      onPressed: w.busy ? null : () => c.wizardShoot(target),
                      icon: w.busy
                          ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2.5))
                          : const Icon(Icons.photo_camera_rounded, size: 20),
                      label: Text(l.wizShootSide(sideName(target)), maxLines: 1, overflow: TextOverflow.ellipsis),
                    ),
                  ),
                if (target != null && w.ready) const SizedBox(width: 8),
                if (w.ready)
                  Expanded(
                    child: (target == null ? FilledButton.new : OutlinedButton.new)(
                      style: ButtonStyle(minimumSize: WidgetStateProperty.all(const Size(0, 48))),
                      onPressed: w.busy ? null : c.wizardCreate,
                      child: Text(l.wizCreate, maxLines: 1, overflow: TextOverflow.ellipsis),
                    ),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  /// Ввод штрихкода руками. Сканер, подключённый к телефону, «печатает» цифры в это же поле.
  Future<void> _askCode(BuildContext context) async {
    final code = await showDialog<String>(context: context, builder: (_) => const _CodeDialog());
    if (code != null && code.trim().isNotEmpty) await c.wizardBarcode(code.trim());
  }
}

class _CodeDialog extends StatefulWidget {
  const _CodeDialog();

  @override
  State<_CodeDialog> createState() => _CodeDialogState();
}

class _CodeDialogState extends State<_CodeDialog> {
  final _ctl = TextEditingController();

  @override
  void dispose() {
    _ctl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return AlertDialog(
      title: Text(l.wizEnterCode),
      content: TextField(
        controller: _ctl,
        autofocus: true,
        keyboardType: TextInputType.number,
        decoration: InputDecoration(hintText: l.wizEnterCodeHint),
        onSubmitted: (v) => Navigator.pop(context, v),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text(l.cancel)),
        FilledButton(onPressed: () => Navigator.pop(context, _ctl.text), child: Text(l.done)),
      ],
    );
  }
}

class _Slot extends StatelessWidget {
  final String label;
  final Uint8List? bytes;
  final bool current;
  final bool done;
  final String semantics;
  const _Slot({required this.label, required this.bytes, required this.current, required this.done, required this.semantics});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Semantics(
      label: semantics,
      child: Column(
        children: [
          Container(
            height: 52,
            decoration: BoxDecoration(
              color: pal.window,
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: current ? pal.action : (done ? pal.success : pal.border), width: current ? 2 : 1.2),
            ),
            clipBehavior: Clip.antiAlias,
            child: bytes != null
                ? Stack(
                    fit: StackFit.expand,
                    children: [
                      Image.memory(bytes!, fit: BoxFit.cover, gaplessPlayback: true, cacheWidth: 160),
                      Positioned(right: 2, top: 2, child: Icon(Icons.check_circle_rounded, size: 16, color: pal.success)),
                    ],
                  )
                : Center(child: Icon(done ? Icons.check_circle_rounded : Icons.photo_camera_outlined, size: 22, color: done ? pal.success : pal.textSoft)),
          ),
          const SizedBox(height: 2),
          Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(fontSize: 11, fontWeight: current ? FontWeight.w800 : FontWeight.w600),
          ),
        ],
      ),
    );
  }
}
