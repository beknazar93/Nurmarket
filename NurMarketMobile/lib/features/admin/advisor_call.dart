import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/live_call.dart';
import '../../core/ai/vision.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';

/// Реплика звонка: [text] — как сказано (с кодами [Д1]); table — таблица с цифрами по вопросу (текстовый советник).
class CallTurn {
  final bool user;
  final String text;
  final bool table;
  const CallTurn(this.user, this.text, {this.table = false});
}

/// 2026-10-22 (звонок ИИ мобилки): владелец — «и голосовой звонок в ИИ в мобилке тоже добавь!». Экран звонка с Нур Советником,
/// как «Разговор» в программе владельца (AiAdvisorWindow): крупный круг («Слушаю… / Думаю… / Говорю…», нажатие — перебить),
/// живые субтитры обеих сторон, «Микрофон выкл.», громкая связь, красная «Завершить». Данные магазина — тем же сбором, что
/// текстовый советник ([prepare]: должники кодами [Д1], права пользователя). Вопрос с цифрами — в фоне таблица текстовым
/// советником ([askTable]); после звонка весь разговор и таблицы уходят в чат. Звонок до 10 минут, за минуту — предупреждение.
class AdvisorCallScreen extends ConsumerStatefulWidget {
  final Future<String> Function() prepare;
  final Future<String> Function(String question) askTable;
  final String Function(String text) reveal;
  final LiveAudio? audio;
  final LiveConnect? connect;
  final Duration maxDuration;
  const AdvisorCallScreen({
    super.key,
    required this.prepare,
    required this.askTable,
    required this.reveal,
    this.audio,
    this.connect,
    this.maxDuration = const Duration(minutes: 10),
  });

  @override
  ConsumerState<AdvisorCallScreen> createState() => _AdvisorCallScreenState();
}

class _AdvisorCallScreenState extends ConsumerState<AdvisorCallScreen>
    with WidgetsBindingObserver {
  LiveCall? _call;
  final List<CallTurn> _turns = [];
  final _user = StringBuffer();
  final _ai = StringBuffer();
  String? _problem;
  bool _micBlocked = false;
  bool _preparing = true;
  int _tables = 0;
  bool _warned = false;
  Timer? _clock;
  final _scroll = ScrollController();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    WidgetsBinding.instance.addPostFrameCallback((_) => _begin());
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _clock?.cancel();
    // Экран закрыт — звонок кончается без попытки закрыть экран второй раз.
    _call?.onEnded = null;
    _call?.end();
    _scroll.dispose();
    super.dispose();
  }

  // Свернули приложение — звонок завершается корректно (микрофон не остаётся включённым в фоне).
  @override
  void didChangeAppLifecycleState(AppLifecycleState s) {
    if ((s == AppLifecycleState.paused || s == AppLifecycleState.hidden) &&
        _call != null &&
        !_call!.ended) {
      _call!.end(CallEnd.focus);
    }
  }

  Future<void> _begin() async {
    final l = context.l;
    setState(() {
      _problem = null;
      _micBlocked = false;
      _preparing = true;
    });
    if (widget.audio == null && !Platform.isAndroid) {
      setState(() => _problem = l.callUnsupported);
      return;
    }
    try {
      if (widget.audio == null) {
        var mic = await PlatformLiveAudio.micPermission();
        if (mic != 'granted') mic = await PlatformLiveAudio.requestMic();
        if (mic != 'granted') {
          if (mounted) {
            setState(() {
              _problem = l.callNoMic;
              _micBlocked = true;
            });
          }
          return;
        }
      }
      final vision = ref.read(visionProvider);
      final key = await vision.key();
      if (key == null) {
        if (mounted) setState(() => _problem = l.advisorNoKey);
        return;
      }
      final instruction = await widget.prepare();
      if (!mounted) return;
      final call = LiveCall(
        apiKey: key,
        instruction: instruction,
        audio: widget.audio ?? PlatformLiveAudio(),
        connect: widget.connect,
        maxDuration: widget.maxDuration,
      );
      call.onUserText = (t) {
        if (mounted) setState(() => _user.write(t));
      };
      call.onAiText = (t) {
        if (mounted) setState(() => _ai.write(t));
      };
      call.onTurnDone = _turnDone;
      call.onWarning = () {
        if (!mounted) return;
        setState(() => _warned = true);
        toast(context, l.callLimitSoon);
      };
      call.onEnded = (why, detail) {
        _turnDone(true);
        _clock?.cancel();
        if (!mounted) return;
        if (detail != null &&
            (detail.contains('API key') || detail.contains('API_KEY'))) {
          unawaited(vision.markBadKey(key));
        }
        switch (why) {
          case CallEnd.user:
            Navigator.of(context).pop(_turns);
          case CallEnd.limit:
            setState(
              () => _problem = l.callEndedLimit(widget.maxDuration.inMinutes),
            );
          case CallEnd.focus:
            setState(() => _problem = l.callEndedFocus);
          case CallEnd.error:
            setState(() => _problem = l.callFailed(detail ?? ''));
        }
      };
      setState(() {
        _call = call;
        _preparing = false;
      });
      _clock = Timer.periodic(const Duration(seconds: 1), (_) {
        if (mounted) setState(() {});
      });
      await call.start();
    } on PlatformException catch (e) {
      if (mounted) setState(() => _problem = l.callFailed(e.message ?? e.code));
    } on MissingPluginException {
      if (mounted) setState(() => _problem = l.callUnsupported);
    } catch (e) {
      if (mounted) setState(() => _problem = l.callFailed('$e'));
    }
  }

  /// Конец реплики: сказанное — в список; вопрос с цифрами — таблица текстовым советником в фоне.
  void _turnDone(bool interrupted) {
    final said = _user.toString().trim();
    final answer = _ai.toString().trim();
    _user.clear();
    _ai.clear();
    if (said.isEmpty && answer.isEmpty) return;
    if (said.isNotEmpty) _turns.add(CallTurn(true, said));
    if (answer.isNotEmpty) {
      _turns.add(CallTurn(false, interrupted ? '$answer …' : answer));
    }
    if (!interrupted && said.isNotEmpty && looksLikeDataQuestion(said)) {
      final call = _call;
      unawaited(
        widget.askTable(said).then((table) {
          if (table.trim().isEmpty) return;
          _turns.add(CallTurn(false, table, table: true));
          if (mounted) setState(() => _tables++);
          // Звонок помнит таблицу — следующий вопрос «а подробнее?» отвечается по ней (как LiveTablePassAsync на ПК).
          if (call != null && !call.ended) {
            call.sendText(
              '[Программа] На экране таблица по вопросу владельца (вслух не зачитывай, просто запомни): ${table.length > 900 ? '${table.substring(0, 900)}…' : table}',
            );
          }
        }, onError: (_) {}),
      );
    }
    if (mounted) setState(() {});
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (_scroll.hasClients) {
        _scroll.animateTo(
          _scroll.position.maxScrollExtent,
          duration: const Duration(milliseconds: 200),
          curve: Curves.easeOut,
        );
      }
    });
  }

  void _close() {
    final call = _call;
    if (call != null && !call.ended) {
      call.end(); // onEnded закроет экран
    } else {
      Navigator.of(context).pop(_turns);
    }
  }

  String _stateText(AppLocalizations l, CallState s) => switch (s) {
    CallState.connecting => l.callConnecting,
    CallState.listening => l.callListening,
    CallState.thinking => l.callThinking,
    CallState.speaking => l.callSpeaking,
    CallState.ended => l.callEnded,
  };

  static String _mmss(Duration d) =>
      '${d.inMinutes.toString().padLeft(2, '0')}:${(d.inSeconds % 60).toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final call = _call;
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _close();
      },
      child: Scaffold(
        appBar: ScreenTitle(l.callTitle, subtitle: l.advisorTitle, back: false),
        body: SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
            child: Column(
              children: [
                Text(
                  l.callCost(widget.maxDuration.inMinutes),
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    color: _warned ? T.danger : pal.textSoft,
                    fontSize: 12,
                  ),
                ),
                if (call != null && !call.ended)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      '${_mmss(DateTime.now().difference(call.started))} / ${_mmss(widget.maxDuration)}',
                      style: TextStyle(
                        color: pal.textSoft,
                        fontWeight: FontWeight.w700,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ),
                const SizedBox(height: 12),
                // Крупный круг: пульсирует от голоса; пока ИИ говорит — нажатие перебивает.
                ValueListenableBuilder<CallState>(
                  valueListenable:
                      call?.state ?? ValueNotifier(CallState.connecting),
                  builder: (_, s, _) => Column(
                    children: [
                      ValueListenableBuilder<double>(
                        valueListenable: call?.level ?? ValueNotifier(0),
                        builder: (_, level, _) => GestureDetector(
                          onTap: call?.interrupt,
                          child: AnimatedContainer(
                            duration: const Duration(milliseconds: 120),
                            width:
                                150 +
                                (s == CallState.listening ? level * 40 : 0),
                            height:
                                150 +
                                (s == CallState.listening ? level * 40 : 0),
                            decoration: BoxDecoration(
                              shape: BoxShape.circle,
                              color: switch (s) {
                                CallState.speaking => T.accent,
                                CallState.thinking => T.accentSoft,
                                CallState.ended => pal.border,
                                _ => T.navy,
                              },
                              boxShadow: [
                                BoxShadow(
                                  color: T.navy.withValues(alpha: .18),
                                  blurRadius: 24,
                                  spreadRadius: 2,
                                ),
                              ],
                            ),
                            child: Icon(
                              s == CallState.speaking
                                  ? Icons.graphic_eq_rounded
                                  : (call?.muted ?? false
                                        ? Icons.mic_off_rounded
                                        : Icons.mic_rounded),
                              size: 64,
                              color:
                                  s == CallState.speaking ||
                                      s == CallState.thinking
                                  ? T.navy
                                  : Colors.white,
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(height: 12),
                      Text(
                        _problem != null
                            ? ''
                            : (_preparing ? l.callPreparing : _stateText(l, s)),
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          fontSize: 17,
                          fontWeight: FontWeight.w800,
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 8),
                if (_tables > 0)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 6),
                    child: Chip(
                      avatar: const Icon(Icons.table_chart_outlined, size: 18),
                      label: Text('${l.callTableReady} · $_tables'),
                    ),
                  ),
                // Живые субтитры: прошлые реплики и то, что говорится сейчас.
                Expanded(
                  child: ListView(
                    controller: _scroll,
                    children: [
                      for (final t in _turns.where((t) => !t.table))
                        _line(context, t.user, widget.reveal(t.text)),
                      if (_user.isNotEmpty)
                        _line(context, true, _user.toString(), live: true),
                      if (_ai.isNotEmpty)
                        _line(
                          context,
                          false,
                          widget.reveal(_ai.toString()),
                          live: true,
                        ),
                    ],
                  ),
                ),
                if (_problem != null)
                  Panel(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Text(
                          _problem!,
                          style: const TextStyle(fontWeight: FontWeight.w600),
                        ),
                        const SizedBox(height: 8),
                        Wrap(
                          spacing: 8,
                          runSpacing: 6,
                          children: [
                            if (_micBlocked)
                              FilledButton.tonal(
                                onPressed: () =>
                                    PlatformLiveAudio.openSettings(),
                                child: Text(l.callOpenSettings),
                              ),
                            FilledButton.tonal(
                              onPressed: () {
                                _call = null;
                                _begin();
                              },
                              child: Text(l.retry),
                            ),
                            TextButton(
                              onPressed: () =>
                                  Navigator.of(context).pop(_turns),
                              child: Text(l.callClose),
                            ),
                          ],
                        ),
                      ],
                    ),
                  ),
                const SizedBox(height: 10),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton.icon(
                        style: OutlinedButton.styleFrom(
                          minimumSize: const Size.fromHeight(52),
                        ),
                        onPressed: call == null || call.ended
                            ? null
                            : () async {
                                await call.setMuted(!call.muted);
                                if (mounted) setState(() {});
                              },
                        icon: Icon(
                          call?.muted ?? false
                              ? Icons.mic_off_rounded
                              : Icons.mic_none_rounded,
                        ),
                        label: Text(
                          call?.muted ?? false ? l.callMicOff : l.callMicOn,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: OutlinedButton.icon(
                        style: OutlinedButton.styleFrom(
                          minimumSize: const Size.fromHeight(52),
                        ),
                        onPressed: call == null || call.ended
                            ? null
                            : () async {
                                await call.setSpeaker(!call.speaker);
                                if (mounted) setState(() {});
                              },
                        icon: Icon(
                          call?.speaker ?? true
                              ? Icons.volume_up_rounded
                              : Icons.phone_in_talk_rounded,
                        ),
                        label: Text(
                          call?.speaker ?? true
                              ? l.callSpeakerOn
                              : l.callSpeakerOff,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                FilledButton.icon(
                  style: FilledButton.styleFrom(
                    backgroundColor: T.danger,
                    foregroundColor: Colors.white,
                    minimumSize: const Size.fromHeight(56),
                  ),
                  onPressed: _close,
                  icon: const Icon(Icons.call_end_rounded),
                  label: Text(
                    call == null || call.ended ? l.callClose : l.callEnd,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _line(
    BuildContext context,
    bool user,
    String text, {
    bool live = false,
  }) {
    final pal = context.pal;
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Text.rich(
        TextSpan(
          children: [
            TextSpan(
              text: '${user ? context.l.callYou : context.l.advisorTitle}: ',
              style: const TextStyle(fontWeight: FontWeight.w800),
            ),
            TextSpan(text: text),
          ],
        ),
        style: TextStyle(
          color: live ? pal.text : pal.textSoft,
          fontSize: 14,
          height: 1.3,
        ),
      ),
    );
  }
}
