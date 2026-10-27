import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/link/kassa_link.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/beep.dart';
import '../../core/util/ui.dart';
import '../profile/kassa_link_screen.dart';
import 'shot_flow.dart';

// 2026-10-27, владелец: «при сканировании мобилки камерой товар попадает прямо в кассу десктопа». Режим «Сканер для кассы»
// (М-П4): камера сканирует непрерывно, каждый штрихкод уходит на кассу (op "scan"), на экране — индикатор связи, последний
// код и его статус, счётчик; звук и вибрация — на ответ кассы. Отправка, очередь (≤ 50 при потере связи) и поиск кассы —
// в lib/core/link/kassa_link.dart; здесь только антидребезг и показ.

/// Что показано как результат последнего кода.
enum KassaScanStatus {
  none,
  sending,
  added,
  notFound,
  tillInactive,
  queued,
  queueFull,
  expired,
  failed,

  /// 2026-10-27: касса: скан старше 30 с — отсканировать снова.
  stale,

  /// 2026-10-27: касса занята оплатой — код в очереди, повторится сам.
  tillBusy,

  /// 2026-10-27 (аудит мобилки, С-4): касса отказывает телефону (отключён, приём выключен, ключ устарел, время) — код не отправлялся.
  refused,
}

class KassaScannerController extends ChangeNotifier {
  KassaScannerController({KassaLink? link, this.userName, this.onFlash}) : link = link ?? KassaLink.instance;

  final KassaLink link;

  /// Имя сотрудника для hello (из профиля).
  final String Function()? userName;

  /// Вспышка рамки сканера: true — успех, false — ошибка.
  final void Function(bool ok)? onFlash;

  /// Антидребезг: тот же код чаще чем раз в 1,5 с не уходит (правило сканера продажи — [ScanRepeat]).
  final _repeat = ScanRepeat();

  String? lastCode;
  String? lastProduct;
  KassaScanStatus lastStatus = KassaScanStatus.none;

  /// Сколько товаров кассе удалось добавить за время работы режима.
  int added = 0;

  /// Как часто проверяется связь, пока сканов нет.
  static const beatEvery = Duration(seconds: 25);

  StreamSubscription<KassaScanEvent>? _sub;
  Timer? _beat;
  bool _disposed = false;
  DateTime _lastActivity = DateTime(2000);

  Future<void> start() async {
    await link.ensureLoaded();
    if (_disposed) return;
    final name = userName?.call();
    if (name != null) link.deviceName = name;
    _sub = link.events.listen(_onEvent);
    if (link.isPaired) unawaited(link.check(background: true));
    // связь проверяется раз в 25 с, если сканов не было: индикатор не «залипает» зелёным, когда кассу выключили.
    // 2026-10-27 (аудит мобилки, С-7): было раз в 8 с — лишняя нагрузка на батарею и Wi-Fi; поиск по подсети сам идёт реже (kassa_link.dart).
    _beat = Timer.periodic(beatEvery, (_) {
      if (link.isPaired && !link.checking && link.pendingCount == 0 && DateTime.now().difference(_lastActivity) > const Duration(seconds: 20)) {
        unawaited(link.check(background: true));
      }
    });
    notifyListeners();
  }

  /// Камера увидела код (вызывается на каждый кадр с кодом — повторы отсекает [ScanRepeat]).
  void onCode(String code) {
    if (_disposed || !link.isPaired) return;
    final now = DateTime.now();
    if (!_repeat.isNew(code, now)) return;
    _repeat.counted(code, now);
    _lastActivity = now;
    // 2026-10-27 (аудит мобилки, С-4): касса заведомо откажет — не шлём, говорим почему и проверяем связь ещё раз (вдруг уже исправили)
    if (link.status.refusesScans) {
      lastCode = code;
      lastProduct = null;
      lastStatus = KassaScanStatus.refused;
      _feedback(false);
      if (!link.checking) unawaited(link.check(discover: false));
      notifyListeners();
      return;
    }
    switch (link.submitScan(code)) {
      case KassaSubmit.accepted:
        lastCode = code;
        lastProduct = null;
        lastStatus = KassaScanStatus.sending;
      case KassaSubmit.queueFull:
        lastCode = code;
        lastProduct = null;
        lastStatus = KassaScanStatus.queueFull;
        _feedback(false);
      case KassaSubmit.notPaired:
        return;
    }
    notifyListeners();
  }

  void _onEvent(KassaScanEvent e) {
    if (_disposed) return;
    lastCode = e.code;
    lastProduct = e.productName;
    switch (e.outcome) {
      case KassaScanOutcome.added:
        lastStatus = KassaScanStatus.added;
        added++;
        _feedback(true);
      case KassaScanOutcome.notFound:
        lastStatus = KassaScanStatus.notFound;
        _feedback(false);
      case KassaScanOutcome.tillInactive:
        lastStatus = KassaScanStatus.tillInactive;
        _feedback(false);
      case KassaScanOutcome.queued:
        lastStatus = KassaScanStatus.queued;
        HapticFeedback.lightImpact(); // код принят телефоном, касса его получит позже — без «успешного» звука
      case KassaScanOutcome.expired:
        lastStatus = KassaScanStatus.expired;
        _feedback(false);
      case KassaScanOutcome.failed:
        lastStatus = KassaScanStatus.failed;
        _feedback(false);
      case KassaScanOutcome.stale:
        lastStatus = KassaScanStatus.stale;
        _feedback(false);
      case KassaScanOutcome.busy:
        lastStatus = KassaScanStatus.tillBusy;
        HapticFeedback.lightImpact(); // не итог: код ждёт в очереди и уйдёт после оплаты
    }
    notifyListeners();
  }

  void _feedback(bool ok) {
    if (ok) {
      HapticFeedback.mediumImpact();
      Beep.ok();
    } else {
      HapticFeedback.heavyImpact();
      Beep.error();
    }
    onFlash?.call(ok);
  }

  @override
  void dispose() {
    _disposed = true;
    _beat?.cancel();
    _sub?.cancel();
    super.dispose();
  }
}

/// Нижняя панель режима: индикатор связи, последний код со статусом, счётчик. Связь не настроена — предложение настроить.
class KassaScannerPanel extends StatelessWidget {
  final KassaScannerController controller;
  final VoidCallback onSetup;
  const KassaScannerPanel({super.key, required this.controller, required this.onSetup});

  @override
  Widget build(BuildContext context) {
    final link = controller.link;
    return ListenableBuilder(
      listenable: Listenable.merge([controller, link]),
      builder: (context, _) {
        if (!link.loaded) return const SizedBox.shrink();
        final l = context.l;
        final pal = context.pal;
        final box = BoxDecoration(
          color: pal.panel,
          borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        );
        final pad = EdgeInsets.fromLTRB(16, 14, 16, MediaQuery.paddingOf(context).bottom + 14);
        if (!link.isPaired) {
          return Container(
            decoration: box,
            padding: pad,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l.klSetupAsk, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
                const SizedBox(height: 4),
                Text(l.klScanQrHint, style: TextStyle(fontSize: 13, color: pal.textSoft)),
                const SizedBox(height: 12),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(T.ctaHeight)),
                  onPressed: onSetup,
                  child: Text(l.klSetup),
                ),
              ],
            ),
          );
        }
        final st = link.status;
        final checking = link.checking && st != KassaLinkStatus.online;
        final stateText = checking ? (link.discovering ? l.klSearching : l.klChecking) : st.text(l);
        final pending = link.pendingCount;
        // 2026-10-27 (аудит мобилки, С-3/С-4): при отказе или «касса не найдена» — что делать и кнопки вместо «товар сразу попадёт в чек»
        final help = checking ? null : st.help(l);
        final rePair = st == KassaLinkStatus.revoked || st == KassaLinkStatus.rePair;
        const btn = Size.fromHeight(48);
        return Container(
          decoration: box,
          padding: pad,
          // на узком экране и крупном шрифте панель не закрывает камеру целиком — прокручивается
          child: ConstrainedBox(
            constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * .62),
            child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // индикатор связи: зелёный — касса на связи, красный — нет
              Semantics(
                container: true,
                liveRegion: true,
                label: a11yJoin([stateText, if (link.kassaName.isNotEmpty) l.klKassaName(link.kassaName), if (pending > 0) l.klQueueLeft(pending)]),
                child: ExcludeSemantics(
                  child: Row(
                    children: [
                      Container(
                        width: 56,
                        height: 56,
                        decoration: BoxDecoration(
                          shape: BoxShape.circle,
                          color: st.softColor(pal),
                          border: Border.all(color: st.color(pal), width: 3),
                        ),
                        child: Icon(st.icon, color: st.color(pal), size: 28),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              stateText,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: st.color(pal)),
                            ),
                            if (link.kassaName.isNotEmpty)
                              Text(
                                l.klKassaName(link.kassaName),
                                maxLines: 2, // 2026-10-27 (аудит, Н-2): длинное имя кассы не обрезается до «касса…»
                                overflow: TextOverflow.ellipsis,
                                style: TextStyle(fontSize: 13, color: pal.textSoft),
                              ),
                          ],
                        ),
                      ),
                      if (pending > 0) ...[
                        const SizedBox(width: 8),
                        Text(
                          l.klQueueLeft(pending),
                          style: TextStyle(fontWeight: FontWeight.w700, fontSize: 13, color: pal.warn),
                        ),
                      ],
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              if (help != null) ...[
                Semantics(liveRegion: true, child: Text(help, style: TextStyle(fontSize: 13.5, height: 1.3, color: pal.text))),
                const SizedBox(height: 10),
                if (rePair)
                  FilledButton.icon(
                    style: FilledButton.styleFrom(minimumSize: btn),
                    onPressed: onSetup,
                    icon: const Icon(Icons.qr_code_scanner_rounded),
                    label: Text(l.klRescanQr),
                  )
                else ...[
                  FilledButton.icon(
                    style: FilledButton.styleFrom(minimumSize: btn),
                    onPressed: link.checking ? null : () => link.check(),
                    icon: const Icon(Icons.sync_rounded),
                    label: Text(l.klCheckNow),
                  ),
                  if (st == KassaLinkStatus.notFound) ...[
                    const SizedBox(height: 8),
                    OutlinedButton.icon(
                      style: OutlinedButton.styleFrom(minimumSize: btn),
                      onPressed: onSetup,
                      icon: const Icon(Icons.qr_code_scanner_rounded),
                      label: Text(l.klRescanQr),
                    ),
                  ],
                ],
                const SizedBox(height: 12),
              ],
              // пока связи нет, «товар сразу попадёт в чек» не пишем: без последнего кода — только подсказка выше
              if (controller.lastCode != null || help == null) _lastScan(context, controller),
              const SizedBox(height: 8),
              Text(
                l.klCounter(controller.added),
                style: TextStyle(fontWeight: FontWeight.w700, fontSize: 13, color: pal.textSoft),
              ),
            ],
          ),
            ),
          ),
        );
      },
    );
  }

  Widget _lastScan(BuildContext context, KassaScannerController c) {
    final l = context.l;
    final pal = context.pal;
    final code = c.lastCode;
    if (code == null) return Text(l.klScannerHint, style: TextStyle(fontSize: 14, color: pal.textSoft));
    final (String text, IconData icon, Color fg, Color bg) = switch (c.lastStatus) {
      KassaScanStatus.added => (l.klScAdded, Icons.check_circle_rounded, pal.success, pal.successSoft),
      KassaScanStatus.notFound => (l.klScNotFound, Icons.search_off_rounded, pal.warn, pal.warnSoft),
      KassaScanStatus.tillInactive => (l.klScTillInactive, Icons.desktop_access_disabled_rounded, pal.warn, pal.warnSoft),
      KassaScanStatus.queued => (l.klScQueued, Icons.hourglass_top_rounded, pal.warn, pal.warnSoft),
      KassaScanStatus.queueFull => (l.klScQueueFull, Icons.block_rounded, pal.danger, pal.dangerSoft),
      KassaScanStatus.expired => (l.klScExpired, Icons.error_outline_rounded, pal.danger, pal.dangerSoft),
      KassaScanStatus.stale => (l.klScStale, Icons.timer_off_rounded, pal.danger, pal.dangerSoft),
      KassaScanStatus.tillBusy => (l.klScBusy, Icons.hourglass_top_rounded, pal.warn, pal.warnSoft),
      KassaScanStatus.failed => (l.klScFailed, Icons.error_outline_rounded, pal.danger, pal.dangerSoft),
      KassaScanStatus.refused => (l.klScRefused, Icons.block_rounded, pal.danger, pal.dangerSoft),
      _ => (l.klScSending, Icons.upload_rounded, pal.textSoft, pal.window),
    };
    final product = c.lastProduct;
    return Semantics(
      container: true,
      liveRegion: true,
      label: '${l.klLastCode}: $code. $text${product == null ? '' : '. $product'}',
      child: ExcludeSemantics(
        child: Container(
          constraints: const BoxConstraints(minHeight: 48),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(T.radiusButton)),
          child: Row(
            children: [
              Icon(icon, color: fg, size: 24),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      text,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(fontWeight: FontWeight.w800, fontSize: 15, color: fg),
                    ),
                    Text(
                      product == null || product.isEmpty ? code : '$product · $code',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(fontSize: 13, color: pal.text),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
