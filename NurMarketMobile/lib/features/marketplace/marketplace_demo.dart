import 'dart:async';

import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart' show L10nX;
import 'marketplace_content.dart';
import 'marketplace_strings.dart';

/// 2026-11-27 (Маркетплейс мобилки): демонстрация «как работает» — 3–5 шагов «что нажимает → что видит», нарисованных виджетами по тем же данным, что и в кассе
/// (MarketplaceDemos.*, MockStep). Не GIF: следует теме (светлая/тёмная), языку и размеру шрифта, ничего не весит и не обрезается. Шаги листаются сами раз в 4 секунды;
/// при «убрать анимацию» в системе автопоказа нет. Кнопки «назад / далее / остановить» — по 48 dp.
class MpDemoPlayer extends StatefulWidget {
  final List<MpStep> steps;
  final String lang;
  final Duration interval;
  const MpDemoPlayer({super.key, required this.steps, required this.lang, this.interval = const Duration(seconds: 4)});

  @override
  State<MpDemoPlayer> createState() => _MpDemoPlayerState();
}

class _MpDemoPlayerState extends State<MpDemoPlayer> {
  int _i = 0;
  bool _playing = true;
  Timer? _timer;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _arm();
  }

  void _arm() {
    _timer?.cancel();
    _timer = null;
    final reduce = MediaQuery.of(context).disableAnimations;
    if (_playing && !reduce && widget.steps.length > 1) {
      _timer = Timer.periodic(widget.interval, (_) {
        if (mounted) setState(() => _i = (_i + 1) % widget.steps.length);
      });
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  void _go(int to) {
    final n = widget.steps.length;
    setState(() {
      _i = (to % n + n) % n;
      _playing = false; // ручной выбор останавливает автопоказ
    });
    _arm();
  }

  @override
  Widget build(BuildContext context) {
    final steps = widget.steps;
    if (steps.isEmpty) return const SizedBox.shrink();
    final step = steps[_i.clamp(0, steps.length - 1)];
    final p = context.pal;
    final lang = widget.lang;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        AnimatedSwitcher(
          duration: const Duration(milliseconds: 250),
          child: KeyedSubtree(key: ValueKey(_i), child: MpMockWindow(title: step.title.of(lang), parts: step.parts, lang: lang)),
        ),
        const SizedBox(height: 10),
        Semantics(
          liveRegion: true,
          child: Text(step.caption.of(lang), style: const TextStyle(fontWeight: FontWeight.w700, height: 1.35)),
        ),
        const SizedBox(height: 4),
        Row(
          children: [
            IconButton(
              tooltip: context.t5('Назад', 'Артка', 'Orqaga', 'Back', 'Geri'),
              constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
              onPressed: steps.length > 1 ? () => _go(_i - 1) : null,
              icon: const Icon(Icons.chevron_left_rounded),
            ),
            Expanded(
              child: Text(Ms.demoStep(context, _i + 1, steps.length), textAlign: TextAlign.center, style: TextStyle(fontSize: 12, color: p.textSoft)),
            ),
            IconButton(
              tooltip: _playing ? Ms.demoPause(context) : Ms.demoPlay(context),
              constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
              onPressed: steps.length > 1
                  ? () {
                      setState(() => _playing = !_playing);
                      _arm();
                    }
                  : null,
              icon: Icon(_playing ? Icons.pause_circle_outline_rounded : Icons.play_circle_outline_rounded),
            ),
            IconButton(
              tooltip: context.t5('Далее', 'Кийинки', 'Keyingi', 'Next', 'İleri'),
              constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
              onPressed: steps.length > 1 ? () => _go(_i + 1) : null,
              icon: const Icon(Icons.chevron_right_rounded),
            ),
          ],
        ),
        Text(Ms.demoNote(context), style: TextStyle(fontSize: 11.5, color: p.textSoft)),
      ],
    );
  }
}

/// Макет окна кассы из простых частей (строка, поле, кнопка, плашка, переключатель, итог, «таблетки», плитки). Hot-часть подсвечена рамкой акцента.
class MpMockWindow extends StatelessWidget {
  final String title;
  final List<MpPart> parts;
  final String lang;
  const MpMockWindow({super.key, required this.title, required this.parts, required this.lang});

  Color _tone(BuildContext c, String tone) {
    final p = c.pal;
    return switch (tone) {
      'good' => p.success,
      'warn' => p.warn,
      'bad' => p.danger,
      'accent' => p.action,
      _ => p.text,
    };
  }

  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    return ExcludeSemantics(
      // подпись шага читает экранный диктор; макет — картинка, его детали повторять не нужно
      child: Container(
        decoration: BoxDecoration(
          color: p.soft,
          borderRadius: BorderRadius.circular(T.radiusCard),
          border: Border.all(color: p.border),
        ),
        clipBehavior: Clip.antiAlias,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              color: p.panel,
              child: Text(title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: p.textSoft)),
            ),
            Padding(
              padding: const EdgeInsets.all(12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  for (var i = 0; i < parts.length; i++) ...[if (i > 0) const SizedBox(height: 8), _part(context, parts[i])],
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  BoxDecoration _hot(BuildContext c, bool hot, {Color? fill, double radius = 10}) {
    final p = c.pal;
    return BoxDecoration(
      color: fill ?? p.panel,
      borderRadius: BorderRadius.circular(radius),
      border: Border.all(color: hot ? T.accent : p.border, width: hot ? 2 : 1),
    );
  }

  Widget _part(BuildContext c, MpPart part) {
    final p = c.pal;
    switch (part.kind) {
      case 'input':
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(part.a.of(lang), style: TextStyle(fontSize: 11.5, color: p.textSoft)),
            const SizedBox(height: 4),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              decoration: _hot(c, part.hot),
              child: Text(part.b.of(lang), style: const TextStyle(fontSize: 14)),
            ),
          ],
        );
      case 'row':
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: _hot(c, part.hot),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(part.a.of(lang), style: const TextStyle(fontSize: 13.5, fontWeight: FontWeight.w600)),
                    if (!part.sub.isEmpty) Text(part.sub.of(lang), style: TextStyle(fontSize: 11.5, color: p.textSoft)),
                  ],
                ),
              ),
              if (!part.b.isEmpty) ...[
                const SizedBox(width: 8),
                Flexible(
                  child: Text(part.b.of(lang), textAlign: TextAlign.right, style: TextStyle(fontSize: 13.5, fontWeight: FontWeight.w700, color: _tone(c, part.tone))),
                ),
              ],
            ],
          ),
        );
      case 'btn':
        return Container(
          constraints: const BoxConstraints(minHeight: 44),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          alignment: Alignment.center,
          decoration: BoxDecoration(
            color: part.on ? p.action : Colors.transparent,
            borderRadius: BorderRadius.circular(T.radiusButton),
            border: Border.all(color: part.hot ? T.accent : (part.on ? p.action : p.borderStrong), width: part.hot ? 3 : 1),
          ),
          child: Text(part.a.of(lang), textAlign: TextAlign.center, style: TextStyle(fontWeight: FontWeight.w700, color: part.on ? p.onAction : p.text)),
        );
      case 'note':
        final tone = part.tone;
        final bg = switch (tone) {
          'good' => p.successSoft,
          'warn' => p.warnSoft,
          'bad' => p.dangerSoft,
          'accent' => p.accentSoft,
          _ => p.panel,
        };
        return Container(
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(10), border: Border.all(color: p.border)),
          child: Text(part.a.of(lang), style: TextStyle(fontSize: 13, height: 1.3, color: tone == 'normal' ? p.text : _tone(c, tone))),
        );
      case 'toggle':
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
          decoration: _hot(c, part.hot),
          child: Row(
            children: [
              Expanded(child: Text(part.a.of(lang), style: const TextStyle(fontSize: 13.5))),
              IgnorePointer(child: Switch(value: part.on, onChanged: (_) {})),
            ],
          ),
        );
      case 'total':
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: _hot(c, part.hot),
          child: Row(
            children: [
              Expanded(child: Text(part.a.of(lang), style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w700))),
              const SizedBox(width: 8),
              Flexible(child: Text(part.b.of(lang), textAlign: TextAlign.right, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800))),
            ],
          ),
        );
      case 'chips':
        return Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            for (var i = 0; i < part.items.length; i++)
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                decoration: BoxDecoration(
                  color: i == part.index ? p.action : p.panel,
                  borderRadius: BorderRadius.circular(20),
                  border: Border.all(color: i == part.index ? p.action : p.border),
                ),
                child: Text(part.items[i].of(lang), style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: i == part.index ? p.onAction : p.text)),
              ),
          ],
        );
      case 'tiles':
        return Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            for (var i = 0; i < part.items.length; i++)
              Container(
                constraints: const BoxConstraints(minWidth: 72, minHeight: 44),
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                alignment: Alignment.center,
                decoration: _hot(c, i == part.index),
                child: Text(part.items[i].of(lang), textAlign: TextAlign.center, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
              ),
          ],
        );
    }
    return const SizedBox.shrink();
  }
}
