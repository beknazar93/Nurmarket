import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show FilteringTextInputFormatter;

import '../../core/util/ui.dart';

/// 2026-11-07 (владелец: «при повторном скане останавливать сканер и спрашивать количество»): окно «Сколько штук?» после повторного
/// считывания того же штрихкода ([RepeatScanGuard]). Камеру на время окна останавливает экран, который его вызвал.

/// Где сканируем — от этого зависит фраза «Уже в …».
enum RepeatQtyKind { check, receive, scanned, picked, quote }

/// «Уже в чеке: 1 шт. Сколько добавить?» — [have] уже учтено, [need] — сколько нужно всего (сборка заказа; иначе null).
String repeatQtyMessage(BuildContext context, RepeatQtyKind kind, {required double have, required String unit, double? need}) {
  final h = '${qtyText(have)} $unit'.trim();
  switch (kind) {
    case RepeatQtyKind.check:
      return context.t5('Уже в чеке: $h. Сколько добавить?', 'Чекте бар: $h. Канчасын кошосуз?', "Chekda bor: $h. Nechta qo'shiladi?", 'Already in the receipt: $h. How many to add?', 'Fişte zaten var: $h. Kaç tane eklensin?');
    case RepeatQtyKind.receive:
      return context.t5('Уже в приёмке: $h. Сколько добавить?', 'Кабыл алууда бар: $h. Канчасын кошосуз?', "Qabul qilishda bor: $h. Nechta qo'shiladi?", 'Already in the receiving: $h. How many to add?', 'Mal kabulde zaten var: $h. Kaç tane eklensin?');
    case RepeatQtyKind.scanned:
      return context.t5('Уже отсканировано: $h. Сколько добавить?', 'Сканерленди: $h. Канчасын кошосуз?', "Skanerlangan: $h. Nechta qo'shiladi?", 'Already scanned: $h. How many to add?', 'Zaten okutuldu: $h. Kaç tane eklensin?');
    case RepeatQtyKind.picked:
      final n = '${qtyText(need ?? 0)} $unit'.trim();
      return context.t5('Уже собрано: $h из $n. Сколько добавить?', 'Терилди: $h / $n. Канчасын кошосуз?', "Yig'ildi: $h / $n. Nechta qo'shiladi?", 'Picked so far: $h of $n. How many to add?', 'Toplanan: $h / $n. Kaç tane eklensin?');
    case RepeatQtyKind.quote:
      return context.t5('Уже в КП: $h. Сколько добавить?', 'Сунушта бар: $h. Канчасын кошосуз?', "Taklifda bor: $h. Nechta qo'shiladi?", 'Already in the quote: $h. How many to add?', 'Teklifte zaten var: $h. Kaç tane eklensin?');
  }
}

/// Спросить, сколько добавить. null — «Отмена» (ничего не менять). [fractional] — весовой товар (дробное количество), иначе целое.
/// [max] — не больше этого (сборка заказа: не больше недобранного).
Future<double?> askRepeatQty(
  BuildContext context, {
  required String name,
  required String message,
  String unit = '',
  double initial = 1,
  bool fractional = false,
  double? max,
}) async {
  final v = await showDialog<double>(
    context: context,
    builder: (c) => _RepeatQtyDialog(name: name, message: message, unit: unit, initial: initial, fractional: fractional, max: max),
  );
  // очень большое количество — переспросить (случайный скан, лишний нуль), как в askNumber
  if (v != null && NumKind.qty.needsSure(v) && context.mounted) {
    if (!await confirmAsk(context, title: context.l.qtySure(qtyText(v)), danger: false)) return null;
  }
  return v;
}

class _RepeatQtyDialog extends StatefulWidget {
  final String name;
  final String message;
  final String unit;
  final double initial;
  final bool fractional;
  final double? max;
  const _RepeatQtyDialog({required this.name, required this.message, required this.unit, required this.initial, required this.fractional, this.max});
  @override
  State<_RepeatQtyDialog> createState() => _RepeatQtyDialogState();
}

class _RepeatQtyDialogState extends State<_RepeatQtyDialog> {
  late final TextEditingController _ctrl;
  bool _closed = false;

  double get _step => widget.fractional ? 0.5 : 1;

  @override
  void initState() {
    super.initState();
    final text = qtyText(widget.initial <= 0 ? 1 : widget.initial).replaceAll(RegExp('[   ]'), '');
    // прежнее число выделено: набор заменяет его (как в askNumber)
    _ctrl = TextEditingController.fromValue(TextEditingValue(text: text, selection: TextSelection(baseOffset: 0, extentOffset: text.length)));
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  /// Число из поля; для штучного — целое; null — не годится.
  double? get _value {
    final v = parseNum(_ctrl.text);
    if (v == null || v <= 0) return null;
    final r = widget.fractional ? double.parse(v.toStringAsFixed(3)) : v.roundToDouble();
    if (r <= 0) return null;
    final m = widget.max;
    return m != null && r > m ? m : r;
  }

  void _bump(double d) {
    final cur = parseNum(_ctrl.text) ?? 0;
    var next = double.parse((cur + d).toStringAsFixed(3));
    if (next < _step) next = _step;
    final m = widget.max;
    if (m != null && next > m) next = m;
    setState(() => _ctrl.text = qtyText(next).replaceAll(RegExp('[   ]'), ''));
  }

  void _close([double? v]) {
    if (_closed) return; // Enter и кнопка подряд — закрыть один раз
    _closed = true;
    Navigator.pop(context, v);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final ok = _value != null;
    return AlertDialog(
      title: Text(widget.name, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(context.t5('Повторный скан', 'Кайра сканерлөө', 'Qayta skanerlash', 'Repeat scan', 'Tekrar tarama'), style: TextStyle(fontSize: 12, color: context.pal.textSoft)),
          const SizedBox(height: 4),
          Text(widget.message, style: const TextStyle(fontWeight: FontWeight.w700)),
          const SizedBox(height: 12),
          Row(
            children: [
              IconButton.outlined(
                tooltip: context.t5('Меньше', 'Азыраак', 'Kamaytirish', 'Less', 'Azalt'),
                onPressed: () => _bump(-_step),
                icon: const Icon(Icons.remove_rounded),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: TextField(
                  key: const Key('repeatQtyField'),
                  controller: _ctrl,
                  autofocus: true,
                  textAlign: TextAlign.center,
                  keyboardType: widget.fractional ? const TextInputType.numberWithOptions(decimal: true) : TextInputType.number,
                  textInputAction: TextInputAction.done,
                  inputFormatters: [if (widget.fractional) numberLimit(NumKind.qty) else FilteringTextInputFormatter.digitsOnly],
                  style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800),
                  decoration: InputDecoration(suffixText: widget.unit.isEmpty ? null : widget.unit),
                  onChanged: (_) => setState(() {}),
                  onSubmitted: (_) => _value == null ? null : _close(_value),
                ),
              ),
              const SizedBox(width: 8),
              IconButton.outlined(
                tooltip: context.t5('Больше', 'Көбүрөөк', "Ko'paytirish", 'More', 'Artır'),
                onPressed: () => _bump(_step),
                icon: const Icon(Icons.add_rounded),
              ),
            ],
          ),
        ],
      ),
      actions: [
        TextButton(key: const Key('repeatQtyCancel'), onPressed: () => _close(), child: Text(l.cancel)),
        FilledButton(
          key: const Key('repeatQtyAdd'),
          style: FilledButton.styleFrom(minimumSize: const Size(110, 48)), // касание 48 dp
          onPressed: ok ? () => _close(_value) : null,
          child: Text(context.t5('Добавить', 'Кошуу', "Qo'shish", 'Add', 'Ekle')),
        ),
      ],
    );
  }
}
