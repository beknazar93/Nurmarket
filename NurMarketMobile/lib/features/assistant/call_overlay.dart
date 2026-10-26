// 2026-10-26 (голос-управление, В4): слой звонка над всем приложением (MaterialApp.router builder). Мини-плашка 56 dp поверх
// любого раздела (развернуть — нажатие, «Замолчи» — удержание, «Завершить», микрофон; две позиции — сверху под строкой
// состояния и над нижним меню, место запоминается), строка итога «ИИ: … · Отменить» (8 с), уточнение «две Колы — какую?»,
// «Цена 120 — правильно?», карточка «Сделать» (режим «Спрашивать перед деньгами») и нижняя панель таблицы. Макеты:
// Mobile-Call-Mini / -Action / -Table. Звонок живёт в AssistantCallController: разворачивание — маршрутом AdvisorCallScreen.
import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/live_call.dart' show CallState;
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../admin/advisor_call.dart';
import 'call_controller.dart';
import 'call_table_panel.dart';

class AssistantOverlay extends ConsumerStatefulWidget {
  final Widget child;

  /// Навигатор приложения (go_router): в нём разворачивается экран звонка. null — разворачивать некуда (тесты плашки).
  final GlobalKey<NavigatorState>? navigatorKey;
  const AssistantOverlay({super.key, required this.child, this.navigatorKey});

  @override
  ConsumerState<AssistantOverlay> createState() => _AssistantOverlayState();
}

class _AssistantOverlayState extends ConsumerState<AssistantOverlay> {
  late final AssistantCallController _c = ref.read(callControllerProvider);
  StreamSubscription<String>? _notices;

  /// Высота зоны под плашкой, которую раздел уступает ей (плашка 56 + зазоры).
  static const pillSpace = 64.0;

  @override
  void initState() {
    super.initState();
    _c.presentFull = _present;
    unawaited(_c.loadPillPos());
    _notices = _c.notices.listen((m) {
      if (!mounted) return;
      ScaffoldMessenger.maybeOf(context)?.showSnackBar(SnackBar(content: Text(m)));
    });
  }

  @override
  void dispose() {
    _notices?.cancel();
    if (_c.presentFull == _present) _c.presentFull = null;
    super.dispose();
  }

  Future<void> _present() async {
    final nav = widget.navigatorKey?.currentState;
    if (nav == null || _c.fullShown) return;
    unawaited(_c.showFull(nav, (_) => const AdvisorCallScreen()));
  }

  @override
  Widget build(BuildContext context) {
    final mq = MediaQuery.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([_c, _c.tables]),
      builder: (context, _) {
        final live = _c.phase == CallPhase.active;
        final mini = live && _c.view == CallView.mini;
        final panel = live && !_c.tables.isClosed && _c.tables.current != null;
        final pos = panel ? PillPos.top : _c.pillPos;
        final topPad = mini && pos == PillPos.top ? mq.padding.top + pillSpace : 0.0;
        final base = Positioned.fill(
          // Структура одна и та же всегда (Padding + MediaQuery): иначе при появлении плашки раздел пересоздавался бы.
          child: Padding(
            padding: EdgeInsets.only(top: topPad),
            child: MediaQuery.removePadding(context: context, removeTop: topPad > 0, child: widget.child),
          ),
        );
        final lineTop = mq.padding.top + (mini && pos == PillPos.top ? 6 + 56 + 8 : 8);
        return Stack(
          children: [
            base,
            if (panel) Positioned.fill(child: CallTablePanel(c: _c)),
            if (live && _c.actionLine != null) Positioned(left: 16, right: 16, top: lineTop, child: _ActionLineView(key: ValueKey('line${_c.actionLine!.id}'), c: _c, line: _c.actionLine!)),
            if (live && _c.numberPrompt != null && _c.actionLine == null) Positioned(left: 16, right: 16, top: lineTop, child: _NumberPromptView(c: _c, p: _c.numberPrompt!)),
            if (mini && _c.noLinkLeft != null) Positioned(left: 16, right: 16, top: lineTop, child: _NoLinkCard(c: _c)),
            if (mini)
              Positioned(
                left: 16,
                right: 16,
                top: pos == PillPos.top ? mq.padding.top + 6 : null,
                bottom: pos == PillPos.bottom ? mq.padding.bottom + 76 : null,
                child: _Pill(c: _c),
              ),
            if (live && _c.ambiguous != null) Positioned.fill(child: _AmbiguousSheet(c: _c, p: _c.ambiguous!)),
            if (live && _c.moneyPrompt != null) Positioned.fill(child: _MoneyCard(c: _c, p: _c.moneyPrompt!)),
          ],
        );
      },
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Мини-плашка
// ---------------------------------------------------------------------------------------------------------------------

class _Pill extends StatefulWidget {
  final AssistantCallController c;
  const _Pill({required this.c});

  @override
  State<_Pill> createState() => _PillState();
}

class _PillState extends State<_Pill> with SingleTickerProviderStateMixin {
  late final AnimationController _anim = AnimationController(vsync: this, duration: const Duration(milliseconds: 700))..repeat();
  bool _hold = false;
  double _dragY = 0;

  AssistantCallController get c => widget.c;

  @override
  void dispose() {
    _anim.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final st = c.pillState;
    final bg = switch (st) {
      PillState.noLink => T.brandOrange,
      PillState.micOff => pal.dark ? const Color(0xFF3D3D3D) : const Color(0xFF666666),
      _ => pal.heroBg,
    };
    final fg = switch (st) {
      PillState.noLink => T.navy,
      PillState.micOff => pal.dark ? const Color(0xFFCCCCCC) : Colors.white,
      _ => Colors.white,
    };
    final text = switch (st) {
      PillState.listening => l.callListening,
      PillState.thinking => l.callSearching,
      PillState.speaking => l.callSpeakingShort,
      PillState.noLink => l.callNoLink,
      PillState.micOff => l.callMicOff,
    };
    final dot = switch (st) {
      PillState.listening => const Color(0xFFFB2C36),
      PillState.thinking || PillState.speaking => T.accent,
      PillState.noLink => T.navy,
      PillState.micOff => const Color(0xFFAAAAAA),
    };
    final blinks = st == PillState.listening || st == PillState.thinking;
    final tm = c.elapsed;
    final mm = tm.inMinutes.toString().padLeft(2, '0');
    final ss = (tm.inSeconds % 60).toString().padLeft(2, '0');
    final pill = AnimatedScale(
      scale: _hold ? .97 : 1,
      duration: const Duration(milliseconds: 120),
      child: Semantics(
        button: true,
        label: l.callOpenFull,
        child: Material(
          color: bg,
          elevation: 8,
          shadowColor: const Color(0x47000000),
          borderRadius: BorderRadius.circular(28),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: () => unawaited(c.expand()),
            onLongPress: () {
              setState(() => _hold = true);
              c.interrupt();
              Future<void>.delayed(const Duration(milliseconds: 900), () {
                if (mounted) setState(() => _hold = false);
              });
            },
            child: SizedBox(
              height: 56,
              child: Row(
                children: [
                  const SizedBox(width: 14),
                  AnimatedBuilder(
                    animation: _anim,
                    builder: (_, _) => Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: dot.withValues(alpha: blinks ? (.4 + .6 * (.5 + .5 * math.sin(_anim.value * 2 * math.pi))) : 1),
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(text, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(color: fg, fontSize: 13, fontWeight: FontWeight.w700)),
                  ),
                  if (st == PillState.listening || st == PillState.speaking) _Bars(anim: _anim, color: fg, level: c.call?.level),
                  if (st == PillState.thinking) _Dots(anim: _anim, color: fg),
                  const SizedBox(width: 8),
                  Text('$mm:$ss', style: TextStyle(color: fg.withValues(alpha: .9), fontSize: 13, fontWeight: FontWeight.w700, fontFeatures: const [FontFeature.tabularFigures()])),
                  const SizedBox(width: 6),
                  Container(width: 1, height: 28, color: fg.withValues(alpha: .25)),
                  _RoundBtn(icon: c.call?.muted == true ? Icons.mic_off_rounded : Icons.mic_none_rounded, label: c.call?.muted == true ? l.callMicOff : l.callMicOn, color: fg, onTap: c.toggleMute),
                  _RoundBtn(icon: Icons.call_end_rounded, label: l.callEnd, color: Colors.white, bg: st == PillState.noLink ? T.navy : T.danger, onTap: c.endCall),
                  const SizedBox(width: 4),
                ],
              ),
            ),
          ),
        ),
      ),
    );
    return GestureDetector(
      onVerticalDragStart: (_) => _dragY = 0,
      onVerticalDragUpdate: (d) => _dragY += d.delta.dy,
      onVerticalDragEnd: (d) {
        if (_dragY > 40 || d.velocity.pixelsPerSecond.dy > 300) {
          c.setPillPos(PillPos.bottom);
        } else if (_dragY < -40 || d.velocity.pixelsPerSecond.dy < -300) {
          c.setPillPos(PillPos.top);
        }
      },
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          pill,
          if (_hold)
            Padding(
              padding: const EdgeInsets.only(top: 10),
              child: Material(
                color: pal.panel,
                elevation: 4,
                borderRadius: BorderRadius.circular(24),
                child: Container(
                  height: 48,
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  decoration: BoxDecoration(borderRadius: BorderRadius.circular(24), border: Border.all(color: pal.border)),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(Icons.stop_rounded, size: 20, color: pal.danger),
                      const SizedBox(width: 8),
                      Text(l.callHush, style: TextStyle(color: pal.text, fontSize: 14, fontWeight: FontWeight.w800)),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _RoundBtn extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color color;
  final Color? bg;
  final VoidCallback onTap;
  const _RoundBtn({required this.icon, required this.label, required this.color, required this.onTap, this.bg});

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    label: label,
    excludeSemantics: true,
    child: InkResponse(
      onTap: onTap,
      radius: 24,
      child: Container(
        width: 48,
        height: 48,
        alignment: Alignment.center,
        decoration: bg == null ? null : BoxDecoration(color: bg, shape: BoxShape.circle),
        child: Icon(icon, size: 22, color: color),
      ),
    ),
  );
}

/// Четыре полосы «уровня голоса» (макет: .lv): высота от 6 до 18 dp.
class _Bars extends StatelessWidget {
  final AnimationController anim;
  final Color color;
  final ValueNotifier<double>? level;
  const _Bars({required this.anim, required this.color, this.level});

  @override
  Widget build(BuildContext context) => SizedBox(
    height: 18,
    child: AnimatedBuilder(
      animation: anim,
      builder: (_, _) => Row(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          for (var i = 0; i < 4; i++)
            Container(
              width: 3,
              height: 6 + 12 * (.5 + .5 * math.sin((anim.value + i * .14) * 2 * math.pi)),
              margin: const EdgeInsets.symmetric(horizontal: 1),
              decoration: BoxDecoration(color: color.withValues(alpha: .9), borderRadius: BorderRadius.circular(2)),
            ),
        ],
      ),
    ),
  );
}

class _Dots extends StatelessWidget {
  final AnimationController anim;
  final Color color;
  const _Dots({required this.anim, required this.color});

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: anim,
    builder: (_, _) => Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var i = 0; i < 3; i++)
          Container(
            width: 6,
            height: 6,
            margin: const EdgeInsets.symmetric(horizontal: 2),
            decoration: BoxDecoration(shape: BoxShape.circle, color: color.withValues(alpha: .25 + .75 * math.max(0, math.sin((anim.value - i * .16) * 2 * math.pi)))),
          ),
      ],
    ),
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Строка итога, «нет связи», уточнения
// ---------------------------------------------------------------------------------------------------------------------

BoxDecoration _cardDeco(Palette pal) => BoxDecoration(
  color: pal.panel,
  borderRadius: BorderRadius.circular(T.radiusCard),
  border: Border.all(color: pal.border),
  boxShadow: [BoxShadow(color: pal.dark ? const Color(0x80000000) : const Color(0x29191919), blurRadius: 24, offset: const Offset(0, 8))],
);

class _ActionLineView extends StatelessWidget {
  final AssistantCallController c;
  final ActionLine line;
  const _ActionLineView({super.key, required this.c, required this.line});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final (icon, color) = switch (line.kind) {
      ActionKind.ok => (Icons.check_circle_outline_rounded, pal.success),
      ActionKind.lock => (Icons.lock_outline_rounded, pal.textSoft),
      ActionKind.bad => (Icons.error_outline_rounded, pal.danger),
      ActionKind.warn => (Icons.schedule_rounded, pal.warn),
    };
    final prefix = l.aiSays('').trim();
    return Material(
      color: Colors.transparent,
      child: Container(
        constraints: const BoxConstraints(minHeight: 48),
        decoration: _cardDeco(pal),
        clipBehavior: Clip.antiAlias,
        child: Stack(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 4, 4, 7),
              child: Row(
                children: [
                  Icon(icon, size: 22, color: color),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Padding(
                          padding: const EdgeInsets.only(top: 6),
                          child: Text.rich(
                            TextSpan(
                              children: [
                                TextSpan(text: '$prefix ', style: const TextStyle(fontWeight: FontWeight.w800)),
                                TextSpan(text: line.text),
                              ],
                            ),
                            style: TextStyle(fontSize: 13, height: 1.3, color: pal.text),
                          ),
                        ),
                        if (line.sub != null)
                          Padding(
                            padding: const EdgeInsets.only(top: 1, bottom: 2),
                            child: Text(line.sub!, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11, color: pal.textSoft, fontWeight: FontWeight.w600)),
                          ),
                      ],
                    ),
                  ),
                  if (line.undoable)
                    Padding(
                      padding: const EdgeInsets.only(left: 6, top: 3),
                      child: OutlinedButton(
                        style: OutlinedButton.styleFrom(
                          minimumSize: const Size(0, 40),
                          padding: const EdgeInsets.symmetric(horizontal: 12),
                          side: const BorderSide(color: T.borderStrong),
                          foregroundColor: pal.text,
                          textStyle: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700),
                        ),
                        onPressed: c.undoFromUi,
                        child: Text(l.aiUndo),
                      ),
                    ),
                ],
              ),
            ),
            Positioned(
              left: 0,
              bottom: 0,
              right: 0,
              child: TweenAnimationBuilder<double>(
                tween: Tween(begin: 1, end: 0),
                duration: c.deps.actionLife,
                builder: (_, v, _) => Align(alignment: Alignment.centerLeft, child: FractionallySizedBox(widthFactor: v, child: Container(height: 3, color: T.accent))),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _NumberPromptView extends StatelessWidget {
  final AssistantCallController c;
  final NumberPrompt p;
  const _NumberPromptView({required this.c, required this.p});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final prefix = l.aiSays('').trim();
    return Material(
      color: Colors.transparent,
      child: Container(
        decoration: _cardDeco(pal),
        padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(Icons.info_outline_rounded, size: 22, color: pal.warn),
                const SizedBox(width: 8),
                Expanded(
                  child: Text.rich(
                    TextSpan(children: [TextSpan(text: '$prefix ', style: const TextStyle(fontWeight: FontWeight.w800)), TextSpan(text: p.say)]),
                    style: TextStyle(fontSize: 13, height: 1.3, color: pal.text),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            Padding(
              padding: const EdgeInsets.only(left: 30),
              child: Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  FilledButton.tonal(
                    style: FilledButton.styleFrom(minimumSize: const Size(0, 48), backgroundColor: pal.accentSoft, foregroundColor: pal.text),
                    onPressed: () => c.answerNumber(true),
                    child: Text(l.aiYesNumber(p.number), style: const TextStyle(fontWeight: FontWeight.w700)),
                  ),
                  OutlinedButton(
                    style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48), foregroundColor: pal.text),
                    onPressed: () => c.answerNumber(false),
                    child: Text(l.aiNoRepeat, style: const TextStyle(fontWeight: FontWeight.w700)),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _NoLinkCard extends StatelessWidget {
  final AssistantCallController c;
  const _NoLinkCard({required this.c});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final left = c.noLinkLeft ?? 0;
    return Material(
      color: Colors.transparent,
      child: Container(
        decoration: _cardDeco(pal),
        clipBehavior: Clip.antiAlias,
        child: Stack(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 10),
              child: Row(
                children: [
                  Icon(Icons.wifi_off_rounded, size: 22, color: pal.warn),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(l.callNoLinkWork, style: TextStyle(fontSize: 13, color: pal.text)),
                        Text(l.callNoLinkEnds(left), style: TextStyle(fontSize: 11, color: pal.textSoft, fontWeight: FontWeight.w600)),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            Positioned(
              left: 0,
              bottom: 0,
              right: 0,
              child: Align(
                alignment: Alignment.centerLeft,
                child: FractionallySizedBox(
                  widthFactor: (left / c.deps.noLinkLimit.inSeconds).clamp(0.0, 1.0),
                  child: Container(height: 3, color: T.brandOrange),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _AmbiguousSheet extends StatelessWidget {
  final AssistantCallController c;
  final AmbiguousPrompt p;
  const _AmbiguousSheet({required this.c, required this.p});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final mq = MediaQuery.of(context);
    return Stack(
      children: [
        Positioned.fill(
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTap: c.dismissAmbiguous,
            child: ColoredBox(color: pal.dark ? const Color(0x99000000) : const Color(0x73191919)),
          ),
        ),
        Positioned(
          left: 0,
          right: 0,
          bottom: 0,
          child: Material(
            color: pal.panel,
            borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
            elevation: 12,
            child: Padding(
              padding: EdgeInsets.fromLTRB(16, 10, 16, 12 + mq.padding.bottom),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Center(child: Container(width: 32, height: 4, decoration: BoxDecoration(color: T.borderStrong, borderRadius: BorderRadius.circular(2)))),
                  Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Padding(padding: const EdgeInsets.only(top: 8), child: Text(l.aiPickTitle, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800))),
                            Text(l.aiAmbiguousHint, style: TextStyle(fontSize: 12, color: pal.textSoft, fontWeight: FontWeight.w600)),
                          ],
                        ),
                      ),
                      _RoundBtn(icon: Icons.close_rounded, label: l.tblClose, color: pal.text, onTap: c.dismissAmbiguous),
                    ],
                  ),
                  const SizedBox(height: 8),
                  for (final o in p.options)
                    Padding(
                      padding: const EdgeInsets.only(bottom: 8),
                      child: InkWell(
                        borderRadius: BorderRadius.circular(T.radiusButton),
                        onTap: () => c.pickOption(o.n),
                        child: Container(
                          constraints: const BoxConstraints(minHeight: 56),
                          padding: const EdgeInsets.fromLTRB(8, 6, 12, 6),
                          decoration: BoxDecoration(borderRadius: BorderRadius.circular(T.radiusButton), border: Border.all(color: T.borderStrong, width: 1.5)),
                          child: Row(
                            children: [
                              Container(
                                width: 36,
                                height: 36,
                                alignment: Alignment.center,
                                decoration: BoxDecoration(shape: BoxShape.circle, color: pal.dark ? T.accent : T.navy),
                                child: Text('${o.n}', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 15, color: pal.dark ? T.navy : Colors.white)),
                              ),
                              const SizedBox(width: 12),
                              Expanded(child: Text(o.title, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700, height: 1.2))),
                              if (o.price != null) Text(o.price!, style: TextStyle(fontSize: 15, fontWeight: FontWeight.w800, color: pal.price, fontFeatures: const [FontFeature.tabularFigures()])),
                            ],
                          ),
                        ),
                      ),
                    ),
                  Center(child: TextButton(style: TextButton.styleFrom(minimumSize: const Size(0, 48)), onPressed: c.dismissAmbiguous, child: Text(l.aiNotThat))),
                ],
              ),
            ),
          ),
        ),
      ],
    );
  }
}

class _MoneyCard extends StatelessWidget {
  final AssistantCallController c;
  final MoneyPrompt p;
  const _MoneyCard({required this.c, required this.p});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final mq = MediaQuery.of(context);
    return Stack(
      children: [
        Positioned.fill(child: ColoredBox(color: pal.dark ? const Color(0x99000000) : const Color(0x73191919))),
        Positioned(
          left: 16,
          right: 16,
          bottom: 24 + mq.padding.bottom,
          child: Material(
            color: pal.panel,
            borderRadius: BorderRadius.circular(20),
            elevation: 12,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 14),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Row(
                    children: [
                      Icon(Icons.shield_outlined, size: 16, color: pal.textSoft),
                      const SizedBox(width: 6),
                      Flexible(child: Text(l.aiModeAskMoney, style: TextStyle(fontSize: 12, color: pal.textSoft))),
                    ],
                  ),
                  const SizedBox(height: 8),
                  Text(l.aiConfirmDo(p.what), style: const TextStyle(fontSize: 19, fontWeight: FontWeight.w800, height: 1.2)),
                  if (p.amount != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Text(p.amount!, style: TextStyle(fontSize: 34, fontWeight: FontWeight.w800, color: pal.price, height: 1.1, fontFeatures: const [FontFeature.tabularFigures()])),
                    ),
                  if (p.detail != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(p.detail!, style: TextStyle(fontSize: 13.5, color: pal.textSoft))),
                  const SizedBox(height: 14),
                  FilledButton.icon(
                    style: FilledButton.styleFrom(
                      backgroundColor: T.accent,
                      foregroundColor: T.navy,
                      minimumSize: const Size.fromHeight(T.ctaHeight),
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(T.radiusCta)),
                    ),
                    onPressed: () => c.answerMoney(true),
                    icon: const Icon(Icons.check_rounded, size: 22),
                    label: Text(l.aiDo, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
                  ),
                  const SizedBox(height: 8),
                  OutlinedButton(
                    style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), foregroundColor: pal.text),
                    onPressed: () => c.answerMoney(false),
                    child: Text(l.aiDont),
                  ),
                  const SizedBox(height: 6),
                  Center(child: Text(l.aiOrSayYes, style: TextStyle(fontSize: 12, color: pal.textSoft))),
                ],
              ),
            ),
          ),
        ),
      ],
    );
  }
}

/// Состояние для подписей (используют экран звонка и тесты).
String callStateText(AppLocalizations l, CallState s) => switch (s) {
  CallState.connecting => l.callConnecting,
  CallState.listening => l.callListening,
  CallState.thinking => l.callThinking,
  CallState.speaking => l.callSpeaking,
  CallState.ended => l.callEnded,
};
