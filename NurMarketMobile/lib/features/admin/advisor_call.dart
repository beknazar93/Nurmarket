import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/live_call.dart';
import '../../core/ai/live_video.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../assistant/call_consent.dart';
import '../assistant/call_controller.dart';
import '../assistant/call_video_view.dart';

export '../assistant/call_turn.dart';

/// 2026-10-22 (звонок ИИ мобилки): экран звонка с Нур Советником, как «Разговор» в программе владельца (AiAdvisorWindow): крупный
/// круг («Слушаю… / Думаю… / Говорю…», нажатие — перебить), живые субтитры обеих сторон, «Микрофон выкл.», громкая связь,
/// красная «Завершить». Данные магазина — тем же сбором, что текстовый советник; после звонка разговор и таблицы уходят в чат.
///
/// 2026-10-26 (голос-управление, В4 + видеозвонок): экран больше не владеет звонком — он только показывает состояние
/// [AssistantCallController] (звонок живёт над всем приложением). «Свернуть» (и кнопка «назад») убирает экран, а звонок идёт
/// мини-плашкой поверх разделов; завершает только «Завершить». Добавлено: камера (согласие, предпросмотр, быстрые кнопки,
/// карточки), «Ещё 10 минут», метка «ИИ слушает», сжатая раскладка, когда внизу открыта панель таблицы.
/// Макеты — Mobile-Call-Voice, Mobile-Call-Video, Mobile-Call-Table.
class AdvisorCallScreen extends ConsumerStatefulWidget {
  /// Подмена контроллера — для тестов; в приложении берётся [callControllerProvider].
  final AssistantCallController? controller;
  const AdvisorCallScreen({super.key, this.controller});

  @override
  ConsumerState<AdvisorCallScreen> createState() => _AdvisorCallScreenState();
}

class _AdvisorCallScreenState extends ConsumerState<AdvisorCallScreen> with TickerProviderStateMixin {
  late final AssistantCallController c = widget.controller ?? ref.read(callControllerProvider);
  final _scroll = ScrollController();
  late final AnimationController _ring = AnimationController(vsync: this, duration: const Duration(milliseconds: 1800))..repeat();
  late final AnimationController _eq = AnimationController(vsync: this, duration: const Duration(milliseconds: 700))..repeat();
  int _seenTurns = 0;

  @override
  void initState() {
    super.initState();
    c.addListener(_toEnd);
  }

  @override
  void dispose() {
    c.removeListener(_toEnd);
    _ring.dispose();
    _eq.dispose();
    _scroll.dispose();
    super.dispose();
  }

  void _toEnd() {
    final n = c.turns.length;
    if (n == _seenTurns) return;
    _seenTurns = n;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (_scroll.hasClients) {
        _scroll.animateTo(_scroll.position.maxScrollExtent, duration: const Duration(milliseconds: 200), curve: Curves.easeOut);
      }
    });
  }

  String get _lang => Localizations.localeOf(context).languageCode;

  /// «Назад»: идёт звонок — сворачивается (звонок продолжается); сообщение о конце — закрывается.
  void _back() {
    if (c.phase == CallPhase.active || c.phase == CallPhase.preparing) {
      c.minimize();
    } else {
      c.dismiss();
    }
  }

  Future<void> _toggleCamera() async {
    final v = c.video;
    if (v == null) return;
    if (v.cameraOn || v.state.value.phase == VideoPhase.starting) {
      await c.stopCamera();
      return;
    }
    if (await c.needsConsent()) {
      if (!mounted) return;
      final ok = await VideoConsentDialog.ask(context);
      if (!ok || !mounted) return;
      await c.acceptConsent();
    }
    await c.startCamera();
  }

  static String _mmss(Duration d) => '${d.inMinutes.toString().padLeft(2, '0')}:${(d.inSeconds % 60).toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _back();
      },
      child: ListenableBuilder(
        listenable: Listenable.merge([c, c.panelInset]),
        builder: (context, _) => Scaffold(body: SafeArea(child: _body(context))),
      ),
    );
  }

  Widget _body(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final call = c.call;
    final live = c.phase == CallPhase.active && call != null && !call.ended;
    final v = c.video;
    final vphase = v?.state.value.phase ?? VideoPhase.off;
    final camOn = live && v != null && vphase != VideoPhase.off && vphase != VideoPhase.stoppedIdle && vphase != VideoPhase.error;
    final inset = c.tables.isClosed || c.tables.current == null || !live ? 0.0 : c.panelInset.value;
    final compact = inset > 0;
    final size = MediaQuery.sizeOf(context);
    final landscape = size.width > size.height && camOn && !compact;
    final minutes = c.total.inMinutes;
    final left = c.total - c.elapsed;

    // --- верх: заголовок и «Свернуть»
    final header = Padding(
      padding: const EdgeInsets.fromLTRB(16, 4, 8, 4),
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 56),
        child: Row(
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(l.callTitle, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800, height: 1.15)),
                  Text(
                    compact && live ? '${_mmss(c.elapsed)} / ${_mmss(c.total)} · ${l.advisorTitle}' : l.advisorTitle,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(fontSize: 12, color: pal.textSoft, fontWeight: FontWeight.w600, fontFeatures: const [FontFeature.tabularFigures()]),
                  ),
                ],
              ),
            ),
            if (live || c.phase == CallPhase.preparing)
              Semantics(
                button: true,
                label: l.callMinimize,
                child: OutlinedButton.icon(
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size(0, 48),
                    padding: const EdgeInsets.symmetric(horizontal: 12),
                    backgroundColor: pal.panel,
                    foregroundColor: pal.text,
                    side: const BorderSide(color: T.borderStrong, width: 1.5),
                    textStyle: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700),
                  ),
                  onPressed: c.minimize,
                  icon: const Icon(Icons.keyboard_arrow_down_rounded, size: 20),
                  label: Text(l.callMinimize),
                ),
              ),
          ],
        ),
      ),
    );

    // --- строки: стоимость, таймер, «Ещё 10 минут»
    final cost = Text(
      c.warned ? l.callLimitSoon : (camOn ? l.vcCost(minutes) : l.callCost(minutes)),
      textAlign: TextAlign.center,
      style: TextStyle(color: c.warned ? pal.danger : pal.textSoft, fontSize: 12, fontWeight: c.warned ? FontWeight.w700 : FontWeight.w400, height: 1.3),
    );
    final timer = live
        ? Text.rich(
            TextSpan(
              children: [
                TextSpan(text: _mmss(c.elapsed), style: TextStyle(color: c.warned ? pal.danger : pal.text)),
                TextSpan(text: ' / ${_mmss(c.total)}'),
              ],
            ),
            style: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w700, fontSize: 14, fontFeatures: const [FontFeature.tabularFigures()]),
          )
        : null;
    final economy = camOn
        ? _EconomySwitch(on: c.economy, label: l.vcEconomy, onChanged: c.setEconomy)
        : null;
    final extend = live && c.warned && left > Duration.zero
        ? Center(
            child: FilledButton.icon(
              style: FilledButton.styleFrom(backgroundColor: T.accent, foregroundColor: T.navy, minimumSize: const Size(0, 48), textStyle: const TextStyle(fontSize: 13, fontWeight: FontWeight.w800)),
              onPressed: c.extend,
              icon: const Icon(Icons.schedule_rounded, size: 18),
              label: Text(l.callExtend),
            ),
          )
        : null;

    // --- круг состояния
    Widget orb(double dia, {bool mini = false}) => _Orb(c: c, size: dia, ring: _ring, eq: _eq, mini: mini);
    final noLink = live && c.noLinkLeft != null;
    final stateLabel = _stateLabel(l, call, noLink);
    final stateSoft = noLink || (call?.muted ?? false) || c.phase == CallPhase.problem;
    // с открытой панелью таблицы места мало: круг 96 dp (на низком экране — 72) и без метки «ИИ слушает»
    final short = size.height < 520; // горизонтально, без камеры: места мало
    // плашка с сообщением (ошибка камеры, нет связи, медленный сервер, конец звонка) забирает место — круг меньше, метки нет
    final crowded = vphase == VideoPhase.error || vphase == VideoPhase.stoppedIdle || c.toolSlow || noLink || c.phase == CallPhase.problem;
    final lowRoom = short || crowded || (compact && size.height - inset < 400);
    final orbBlock = Column(
      children: [
        orb(short || (compact && lowRoom) ? 72 : (crowded || compact ? 96 : (camOn ? 120 : 150))),
        const SizedBox(height: 10),
        if (c.problem == null || c.phase == CallPhase.active)
          Text(stateLabel, textAlign: TextAlign.center, style: TextStyle(fontSize: 17, fontWeight: FontWeight.w800, color: stateSoft ? pal.textSoft : pal.text)),
        if (!lowRoom) ...[const SizedBox(height: 6), _recChip(l, call, noLink)],
      ],
    );

    // --- субтитры
    final turns = [for (final t in c.turns) if (!t.table) t];
    final subs = ListView(
      controller: _scroll,
      shrinkWrap: compact,
      physics: compact ? const NeverScrollableScrollPhysics() : null,
      children: [
        for (final t in (compact ? turns.skip(math.max(0, turns.length - 2)) : turns)) _line(context, t.user, c.reveal(t.text)),
        if (c.liveUser.isNotEmpty) _line(context, true, c.liveUser, live: true),
        if (c.liveAi.isNotEmpty) _line(context, false, c.reveal(c.liveAi), live: true),
      ],
    );

    // --- плашки: сервер медленно, нет связи, конец звонка
    final plates = <Widget>[
      if (live && c.toolSlow)
        _Plate(
          kind: _PlateKind.info,
          icon: Icons.schedule_rounded,
          minHeight: 44,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(l.callSlowServer, style: TextStyle(fontSize: 13, color: pal.text.withValues(alpha: .8))),
              if (c.toolPending) Text(l.callStillLoading, style: TextStyle(fontSize: 11, color: pal.textSoft, fontWeight: FontWeight.w600)),
            ],
          ),
        ),
      if (noLink)
        _Plate(
          kind: _PlateKind.warn,
          icon: Icons.wifi_off_rounded,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(l.callNoLinkWork, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: pal.warn)),
              Text('${l.callNoLinkEnds(c.noLinkLeft ?? 0)}. ${l.callNoLinkHelp}', style: TextStyle(fontSize: 12, color: pal.textSoft)),
            ],
          ),
        ),
      if (c.phase == CallPhase.problem && c.problem != null)
        _Plate(
          kind: c.micBlocked || c.problem == l.callEndedFocus || c.problem == l.callNoLinkWork ? _PlateKind.warn : (c.problem!.startsWith(l.callEndedLimit(c.total.inMinutes).split(':').first) ? _PlateKind.info : _PlateKind.bad),
          icon: Icons.info_outline_rounded,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(c.problem!, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600, height: 1.3)),
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 6,
                children: [
                  if (c.micBlocked) FilledButton.tonal(onPressed: PlatformLiveAudio.openSettings, child: Text(l.callOpenSettings)),
                  FilledButton.tonal(style: FilledButton.styleFrom(minimumSize: const Size(0, 48)), onPressed: c.retry, child: Text(l.retry)),
                ],
              ),
            ],
          ),
        ),
      ..._videoPlates(context, l, v, vphase),
    ];

    // --- кнопки
    final cam = c.hasCamera && live;
    final micOn = !(call?.muted ?? false);
    final ctrl = Row(
      children: [
        Expanded(child: _CtrlBtn(icon: micOn ? Icons.mic_none_rounded : Icons.mic_off_rounded, label: micOn ? l.callMicOn : l.callMicOff, muted: !micOn, onTap: live ? c.toggleMute : null)),
        if (cam && !compact) ...[
          const SizedBox(width: 8),
          Expanded(
            child: _CtrlBtn(
              icon: Icons.photo_camera_outlined,
              label: v?.cameraOn == true || vphase == VideoPhase.starting ? l.vcCameraOn : l.vcCamera,
              on: v?.cameraOn == true || vphase == VideoPhase.starting,
              onTap: call.videoActive.value == false ? null : _toggleCamera,
            ),
          ),
        ],
        const SizedBox(width: 8),
        Expanded(
          child: _CtrlBtn(
            icon: (call?.speaker ?? true) ? Icons.volume_up_rounded : Icons.phone_in_talk_rounded,
            label: (call?.speaker ?? true) ? l.callSpeakerOn : l.callSpeakerOff,
            onTap: live ? c.toggleSpeaker : null,
          ),
        ),
        if (compact) ...[
          const SizedBox(width: 8),
          Expanded(child: _CtrlBtn(icon: Icons.call_end_rounded, label: l.callEnd, danger: true, onTap: live ? c.endCall : null)),
        ],
      ],
    );
    final endBtn = FilledButton.icon(
      style: FilledButton.styleFrom(
        backgroundColor: c.phase == CallPhase.problem ? (pal.dark ? T.accent : T.navy) : T.danger,
        foregroundColor: c.phase == CallPhase.problem ? (pal.dark ? T.navy : Colors.white) : Colors.white,
        minimumSize: const Size.fromHeight(T.ctaHeight),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(T.radiusCta)),
      ),
      onPressed: live || c.phase == CallPhase.preparing ? c.endCall : c.dismiss,
      icon: Icon(live || c.phase == CallPhase.preparing ? Icons.call_end_rounded : Icons.close_rounded, size: 22),
      label: Text(live || c.phase == CallPhase.preparing ? l.callEnd : l.callClose, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
    );

    // --- камера
    final camTop = camOn
        ? CallCamView(
            c: c,
            lang: _lang,
            orb: orb(56, mini: true),
            height: landscape ? null : math.min(size.width * 4 / 3 - 32 * 4 / 3, size.height * (size.height <= 760 ? .42 : .44)) / (MediaQuery.textScalerOf(context).scale(1) >= 1.25 ? 1.3 : 1),
          )
        : null;
    final card = live ? c.videoCard : null;

    Widget content;
    if (landscape) {
      content = Padding(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Expanded(flex: 58, child: camTop!),
            const SizedBox(width: 12),
            Expanded(
              flex: 42,
              // боковая колонка прокручивается: при шрифте 1.3 на низком экране всё сразу не помещается
              child: ListView(
                padding: EdgeInsets.zero,
                children: [
                  ?timer,
                  ?economy,
                  const SizedBox(height: 4),
                  if (card != null) VideoResultCardView(c: c, card: card) else SizedBox(height: 84, child: ClipRect(child: subs)),
                  const SizedBox(height: 6),
                  ...plates.map((p) => Padding(padding: const EdgeInsets.only(bottom: 6), child: p)),
                  QuickAskButtons(c: c, grid: true),
                  const SizedBox(height: 6),
                  ctrl,
                  const SizedBox(height: 6),
                  endBtn,
                ],
              ),
            ),
          ],
        ),
      );
    } else {
      content = Padding(
        padding: EdgeInsets.fromLTRB(16, 4, 16, 12 + (compact ? inset : 0)),
        child: Stack(
          children: [
            Column(
              children: [
                if (!compact) ...[
                  if (camOn) ...[
                    Wrap(alignment: WrapAlignment.spaceBetween, crossAxisAlignment: WrapCrossAlignment.center, spacing: 8, children: [?timer, ?economy]),
                  ] else if (!short) ...[
                    cost,
                    if (timer != null) Padding(padding: const EdgeInsets.only(top: 4), child: timer),
                  ],
                  if (extend != null) Padding(padding: const EdgeInsets.only(top: 6), child: extend),
                  const SizedBox(height: 6),
                ],
                if (camOn) ...[
                  camTop!,
                  if (!compact) Padding(padding: const EdgeInsets.only(top: 4), child: cost),
                  const SizedBox(height: 8),
                  QuickAskButtons(c: c),
                  const SizedBox(height: 6),
                ] else
                  orbBlock,
                const SizedBox(height: 6),
                Expanded(child: ClipRect(child: subs)),
                ...plates.map((p) => Padding(padding: const EdgeInsets.only(bottom: 6), child: p)),
                const SizedBox(height: 4),
                ctrl,
                if (!compact) ...[const SizedBox(height: 8), endBtn],
              ],
            ),
            if (card != null)
              Positioned(
                left: 0,
                right: 0,
                bottom: 12 + 56 + 8 + 52 + 10,
                child: VideoResultCardView(c: c, card: card),
              ),
          ],
        ),
      );
    }
    return Column(children: [header, Expanded(child: content)]);
  }

  String _stateLabel(AppLocalizations l, LiveCall? call, bool noLink) {
    if (c.phase == CallPhase.preparing) return l.callPreparing;
    if (c.phase == CallPhase.problem || call == null || call.ended) return l.callEnded;
    if (noLink) return l.callConnecting;
    if (call.muted) return l.callMicOff;
    return switch (call.state.value) {
      CallState.connecting => l.callConnecting,
      CallState.listening => l.callListening,
      CallState.thinking => c.toolSearching ? l.callSearching : l.callThinking,
      CallState.speaking => l.callSpeaking,
      CallState.ended => l.callEnded,
    };
  }

  /// Метка под кругом: «● ИИ слушает» (пока слушает и микрофон включён) или «Перебили — слушаю» (3 с после перебивания).
  Widget _recChip(AppLocalizations l, LiveCall? call, bool noLink) {
    final pal = context.pal;
    final listening = c.phase == CallPhase.active && call != null && !call.ended && !noLink && !call.muted && call.state.value == CallState.listening;
    final interrupted = c.interruptedAt != null && DateTime.now().difference(c.interruptedAt!) < const Duration(seconds: 3) && listening;
    if (interrupted) {
      return Container(
        height: 28,
        padding: const EdgeInsets.symmetric(horizontal: 10),
        decoration: BoxDecoration(color: pal.accentSoft, borderRadius: BorderRadius.circular(14)),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.back_hand_outlined, size: 14, color: pal.text),
            const SizedBox(width: 6),
            Text(l.callInterrupted, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.text)),
          ],
        ),
      );
    }
    if (!listening) return const SizedBox(height: 28);
    return Container(
      height: 28,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(color: pal.panel, borderRadius: BorderRadius.circular(14), border: Border.all(color: pal.border)),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          AnimatedBuilder(
            animation: _ring,
            builder: (_, _) => Container(width: 8, height: 8, decoration: BoxDecoration(shape: BoxShape.circle, color: T.danger.withValues(alpha: .25 + .75 * (.5 + .5 * math.sin(_ring.value * 2 * math.pi * 1.5))))),
          ),
          const SizedBox(width: 6),
          Text(l.callAiListens, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.text.withValues(alpha: .8))),
        ],
      ),
    );
  }

  /// Ошибки и состояния камеры (тексты движка videoMsg — на 5 языках).
  List<Widget> _videoPlates(BuildContext context, AppLocalizations l, LiveVideo? v, VideoPhase phase) {
    if (v == null || c.phase != CallPhase.active) return const [];
    final st = v.state.value;
    final out = <Widget>[];
    if (phase == VideoPhase.error) {
      final key = st.errorKey ?? 'cameraBusy';
      final text = videoMsg(key, _lang);
      out.add(
        _Plate(
          kind: key == 'cameraDenied' ? _PlateKind.bad : _PlateKind.warn,
          icon: key == 'videoRejected' ? Icons.warning_amber_rounded : Icons.photo_camera_outlined,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(text, style: const TextStyle(fontSize: 13, height: 1.3)),
              if (key == 'cameraDenied' || key == 'cameraBusy')
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: OutlinedButton(
                    style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)),
                    onPressed: key == 'cameraDenied' ? PlatformLiveAudio.openSettings : c.startCamera,
                    child: Text(key == 'cameraDenied' ? l.callOpenSettings : l.retry),
                  ),
                ),
            ],
          ),
        ),
      );
    } else if (phase == VideoPhase.stoppedIdle) {
      out.add(_Plate(kind: _PlateKind.info, icon: Icons.battery_saver_rounded, child: Text(videoMsg('stopped', _lang), style: const TextStyle(fontSize: 13, height: 1.3))));
    }
    return out;
  }

  Widget _line(BuildContext context, bool user, String text, {bool live = false}) {
    final pal = context.pal;
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Text.rich(
        TextSpan(
          children: [
            TextSpan(text: '${user ? context.l.callYou : context.l.advisorTitle}: ', style: const TextStyle(fontWeight: FontWeight.w800)),
            TextSpan(text: text),
          ],
        ),
        style: TextStyle(color: live ? pal.text : pal.textSoft, fontSize: 14, height: 1.3),
      ),
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Элементы
// ---------------------------------------------------------------------------------------------------------------------

enum _PlateKind { info, warn, bad }

class _Plate extends StatelessWidget {
  final _PlateKind kind;
  final IconData icon;
  final Widget child;
  final double minHeight;
  const _Plate({required this.kind, required this.icon, required this.child, this.minHeight = 52});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final (bg, fg) = switch (kind) {
      _PlateKind.info => (pal.accentSoft, pal.text),
      _PlateKind.warn => (pal.warnSoft, pal.warn),
      _PlateKind.bad => (pal.dangerSoft, pal.danger),
    };
    return Container(
      constraints: BoxConstraints(minHeight: minHeight),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(T.radiusCard)),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(padding: const EdgeInsets.only(top: 1), child: Icon(icon, size: 22, color: fg)),
          const SizedBox(width: 10),
          Expanded(child: child),
        ],
      ),
    );
  }
}

class _CtrlBtn extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback? onTap;
  final bool on;
  final bool muted;
  final bool danger;
  const _CtrlBtn({required this.icon, required this.label, required this.onTap, this.on = false, this.muted = false, this.danger = false});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final Color bg = danger ? T.danger : (on ? T.accent : (muted ? (pal.dark ? const Color(0xFF333333) : T.soft) : pal.panel));
    final Color fg = danger ? Colors.white : (on ? T.navy : (muted ? pal.text.withValues(alpha: .8) : pal.text));
    final Color border = danger ? T.danger : (on ? T.accent : (muted ? pal.border : T.borderStrong));
    return OutlinedButton.icon(
      style: OutlinedButton.styleFrom(
        minimumSize: const Size.fromHeight(52),
        padding: const EdgeInsets.symmetric(horizontal: 8),
        backgroundColor: bg,
        foregroundColor: fg,
        side: BorderSide(color: border, width: 1.5),
        textStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
      ),
      onPressed: onTap,
      icon: Icon(icon, size: 20),
      label: Text(label, maxLines: 1, overflow: TextOverflow.ellipsis),
    );
  }
}

class _EconomySwitch extends StatelessWidget {
  final bool on;
  final String label;
  final ValueChanged<bool> onChanged;
  const _EconomySwitch({required this.on, required this.label, required this.onChanged});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Semantics(
      toggled: on,
      label: label,
      child: InkWell(
        onTap: () => onChanged(!on),
        borderRadius: BorderRadius.circular(12),
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 48),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Flexible(child: Text(label, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.text.withValues(alpha: .8)))),
              const SizedBox(width: 8),
              AnimatedContainer(
                duration: const Duration(milliseconds: 200),
                width: 40,
                height: 22,
                padding: const EdgeInsets.all(2),
                decoration: BoxDecoration(color: on ? T.accent : T.borderStrong, borderRadius: BorderRadius.circular(11)),
                alignment: on ? Alignment.centerRight : Alignment.centerLeft,
                child: Container(width: 18, height: 18, decoration: BoxDecoration(shape: BoxShape.circle, color: on ? T.navy : Colors.white)),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Круг состояния звонка: тёмный — слушаю (пульс от голоса + кольца), жёлтый — говорю (эквалайзер), светло-жёлтый — думаю (точки),
/// серый — конец / нет связи / микрофон выкл. Нажатие — перебить.
class _Orb extends StatelessWidget {
  final AssistantCallController c;
  final double size;
  final AnimationController ring;
  final AnimationController eq;
  final bool mini;
  const _Orb({required this.c, required this.size, required this.ring, required this.eq, this.mini = false});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final call = c.call;
    final ended = c.phase == CallPhase.problem || (call?.ended ?? true) && c.phase != CallPhase.preparing;
    final noLink = c.phase == CallPhase.active && c.noLinkLeft != null;
    final muted = call?.muted ?? false;
    final preparing = c.phase == CallPhase.preparing || (c.phase == CallPhase.active && call?.state.value == CallState.connecting && !noLink);
    return ValueListenableBuilder<CallState>(
      valueListenable: call?.state ?? ValueNotifier(CallState.connecting),
      builder: (_, s, _) => ValueListenableBuilder<double>(
        valueListenable: call?.level ?? ValueNotifier(0),
        builder: (_, level, _) {
          final listening = !ended && !noLink && !muted && !preparing && s == CallState.listening;
          final dia = size + (listening && !mini ? level * 40 : 0);
          final Color bg;
          Color fg = Colors.white;
          if (ended || noLink) {
            bg = pal.border;
            fg = pal.textSoft;
          } else if (muted) {
            bg = T.borderStrong;
            fg = pal.text;
          } else if (preparing) {
            bg = pal.dark ? const Color(0xFF3D3D3D) : T.navy;
          } else {
            switch (s) {
              case CallState.speaking:
                bg = T.accent;
                fg = T.navy;
              case CallState.thinking:
                bg = pal.accentSoft;
                fg = pal.text;
              case CallState.ended:
                bg = pal.border;
                fg = pal.textSoft;
              default:
                bg = pal.dark ? const Color(0xFF3D3D3D) : T.navy;
            }
          }
          final iconSize = size * (mini ? .5 : .43);
          Widget inner;
          if (ended) {
            inner = Icon(Icons.call_end_rounded, size: size * .37, color: fg);
          } else if (noLink) {
            inner = Icon(Icons.wifi_off_rounded, size: size * .37, color: fg);
          } else if (muted) {
            inner = Icon(Icons.mic_off_rounded, size: iconSize, color: fg);
          } else if (preparing) {
            inner = SizedBox(width: size * .27, height: size * .27, child: CircularProgressIndicator(strokeWidth: 4, color: Colors.white, backgroundColor: Colors.white30));
          } else if (s == CallState.speaking) {
            inner = _EqBars(eq: eq, color: fg, small: size < 80);
          } else if (s == CallState.thinking) {
            inner = _ThinkDots(anim: eq, color: fg);
          } else {
            inner = Icon(Icons.mic_rounded, size: iconSize, color: fg);
          }
          return Semantics(
            button: true,
            label: context.l.callSpeaking,
            child: GestureDetector(
              onTap: c.interrupt,
              onLongPress: c.interrupt,
              child: SizedBox(
                width: size + (mini ? 0 : 40) + (listening ? 30 : 0),
                height: size + (mini ? 0 : 40) + (listening ? 30 : 0),
                child: Stack(
                  alignment: Alignment.center,
                  clipBehavior: Clip.none,
                  children: [
                    if (listening && !mini)
                      for (var i = 0; i < 3; i++)
                        AnimatedBuilder(
                          animation: ring,
                          builder: (_, _) {
                            final t = (ring.value + (1 - i * .33)) % 1.0;
                            return Opacity(
                              opacity: (.45 * (1 - t)).clamp(0.0, 1.0),
                              child: Transform.scale(
                                scale: 1 + .42 * t,
                                child: Container(width: dia, height: dia, decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: pal.dark ? T.accent : T.navy, width: 2))),
                              ),
                            );
                          },
                        ),
                    AnimatedContainer(
                      duration: const Duration(milliseconds: 120),
                      width: dia,
                      height: dia,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: bg,
                        boxShadow: ended || noLink || mini ? null : [BoxShadow(color: T.navy.withValues(alpha: .18), blurRadius: 24, spreadRadius: 2)],
                      ),
                      child: Center(child: inner),
                    ),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
  }
}

class _EqBars extends StatelessWidget {
  final AnimationController eq;
  final Color color;
  final bool small;
  const _EqBars({required this.eq, required this.color, this.small = false});

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: eq,
    builder: (_, _) => Row(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        for (var i = 0; i < (small ? 4 : 5); i++)
          Container(
            width: small ? 3 : 6,
            height: (small ? 5 : 10) + (small ? 13 : 28) * (.5 + .5 * math.sin((eq.value + i * .12) * 2 * math.pi)),
            margin: EdgeInsets.symmetric(horizontal: small ? 1 : 2),
            decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(3)),
          ),
      ],
    ),
  );
}

class _ThinkDots extends StatelessWidget {
  final AnimationController anim;
  final Color color;
  const _ThinkDots({required this.anim, required this.color});

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: anim,
    builder: (_, _) => Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var i = 0; i < 3; i++)
          Container(
            width: 8,
            height: 8,
            margin: EdgeInsets.only(left: 2.5, right: 2.5, bottom: 3 * math.max(0, math.sin((anim.value - i * .17) * 2 * math.pi))),
            decoration: BoxDecoration(shape: BoxShape.circle, color: color.withValues(alpha: .25 + .75 * math.max(0, math.sin((anim.value - i * .17) * 2 * math.pi)))),
          ),
      ],
    ),
  );
}
