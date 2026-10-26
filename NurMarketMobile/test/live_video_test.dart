// 2026-10-26 (видеозвонок, В1): движок видео для звонка с ИИ на фейках — без сети, камеры, микрофона и Google.
// Проверяется: setup с видео, отправка кадров в LiveCall, политика частоты (чистая функция), экономия, паузы по тишине,
// лимиты, сворачивание/шторка, переключение камеры, чёткий снимок, отказ модели от видео, удаление временных файлов.
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' show AppLifecycleState;

import 'package:camera/camera.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/live_call.dart';
import 'package:nurmarket/core/ai/live_video.dart';

class _FakeSocket implements LiveSocket {
  final ctrl = StreamController<Object?>();
  final sent = <Map<String, dynamic>>[];
  @override
  int? closeCode;
  @override
  String? closeReason;
  bool closed = false;
  @override
  Stream<Object?> get frames => ctrl.stream;
  @override
  void send(String text) => sent.add(jsonDecode(text) as Map<String, dynamic>);
  @override
  Future<void> close() async {
    closed = true;
    if (!ctrl.isClosed) await ctrl.close();
  }

  void push(Map<String, dynamic> m) => ctrl.add(jsonEncode(m));
  List<Map<String, dynamic>> get videos => [
    for (final m in sent)
      if (m['realtimeInput'] is Map && (m['realtimeInput'] as Map).containsKey('video')) m,
  ];
}

class _FakeAudio implements LiveAudio {
  final ctrl = StreamController<Object?>.broadcast();
  @override
  Stream<Object?> get events => ctrl.stream;
  @override
  Future<bool> start({required bool speaker}) async => true;
  @override
  Future<void> play(Uint8List pcm) async {}
  @override
  Future<void> flush() async {}
  @override
  Future<void> setMuted(bool muted) async {}
  @override
  Future<void> setSpeaker(bool on) async {}
  @override
  Future<void> stop() async {}
}

Uint8List _pcm(double amp) {
  final b = ByteData(3200);
  for (var i = 0; i < 1600; i++) {
    b.setInt16(i * 2, ((i.isEven ? 1 : -1) * amp * 32767 / 6).round(), Endian.little);
  }
  return b.buffer.asUint8List();
}

Uint8List _jpeg([int n = 2000]) {
  final b = Uint8List(n);
  b[0] = 0xFF;
  b[1] = 0xD8;
  b[2] = 0xFF;
  for (var i = 3; i < n; i++) {
    b[i] = i % 251;
  }
  return b;
}

Future<void> _settle([int ms = 20]) => Future<void>.delayed(Duration(milliseconds: ms));

Future<void> _waitFor(bool Function() cond, {int ms = 3000}) async {
  final end = DateTime.now().add(Duration(milliseconds: ms));
  while (!cond()) {
    if (DateTime.now().isAfter(end)) fail('условие не выполнилось за $ms мс');
    await _settle(10);
  }
}

/// Фейковая камера: ведёт журнал вызовов, умеет падать и ждать.
class _FakeFrames implements LiveFrames {
  final log = <String>[];
  bool open = false;
  int grabs = 0;
  int failGrabs = 0;
  FrameException? startError;
  Completer<void>? startGate;
  Uint8List frame = _jpeg();
  Uint8List? stillBytes;
  String? barcode;
  Duration stillDelay = Duration.zero;
  bool stillFails = false;
  bool stillLosesCamera = false;
  bool purged = false;
  CameraLensDirection lens = CameraLensDirection.back;
  @override
  bool hasFront = true;
  @override
  bool hasBack = true;
  @override
  CameraController? get controller => null;
  @override
  bool get isOpen => open;
  @override
  Uint8List? get lastFrame => open ? frame : null;

  @override
  Future<void> start({required CameraLensDirection lens}) async {
    log.add('start ${lens.name}');
    final gate = startGate;
    if (gate != null) await gate.future;
    final e = startError;
    if (e != null) throw e;
    this.lens = lens;
    open = true;
  }

  @override
  Future<Uint8List?> grab() async {
    if (!open) return null;
    grabs++;
    if (failGrabs > 0) {
      failGrabs--;
      throw const FrameException(FrameFail.shot);
    }
    return frame;
  }

  @override
  Future<Uint8List?> still(ResolutionPreset preset) async {
    log.add('still ${preset.name}');
    await Future<void>.delayed(stillDelay);
    if (stillFails) throw const FrameException(FrameFail.shot);
    if (stillLosesCamera) open = false;
    return stillBytes;
  }

  @override
  Future<String?> barcodeOfLastStill() async => barcode;
  @override
  Future<void> setTorch(bool on) async => log.add('torch $on');
  @override
  Future<void> stop() async {
    log.add('stop');
    open = false;
  }

  @override
  Future<void> purge() async => purged = true;
}

const _fast = VideoConfig(
  tick: Duration(milliseconds: 10),
  normalInterval: Duration(milliseconds: 40),
  slowInterval: Duration(milliseconds: 150),
  economyInterval: Duration(milliseconds: 120),
  economyVoiceWindow: Duration(milliseconds: 500),
  freshGap: Duration(milliseconds: 30),
  freshWindow: Duration(milliseconds: 300),
  pauseAfter: Duration(seconds: 30),
  stopAfter: Duration(minutes: 3),
  shotTimeout: Duration(milliseconds: 500),
  stillTimeout: Duration(milliseconds: 400),
  stillGrabWait: Duration(milliseconds: 200),
);

void main() {
  setUp(LiveCall.resetForTest);

  // -------------------------------------------------------------------------------------------------------------------
  group('setup и кадры в LiveCall', () {
    late List<_FakeSocket> sockets;
    late _FakeAudio audio;

    LiveCall make({bool video = true, Duration max = const Duration(minutes: 10)}) {
      sockets = [];
      audio = _FakeAudio();
      return LiveCall(
        apiKey: 'test-key',
        instruction: 'ДАННЫЕ',
        audio: audio,
        maxDuration: max,
        warnBefore: Duration.zero,
        video: video,
        connect: (uri, headers) async {
          final s = _FakeSocket();
          sockets.add(s);
          return s;
        },
      );
    }

    Future<LiveCall> ready({bool video = true}) async {
      final call = make(video: video);
      final started = call.start();
      await _settle();
      sockets.single.push({'setupComplete': <String, dynamic>{}});
      await started;
      return call;
    }

    test('setupJson: без video — как раньше; с video — только mediaResolution LOW в generationConfig', () {
      final plain = LiveCall.setupJson('m', 'инструкция', null);
      final withVideo = LiveCall.setupJson('m', 'инструкция', null, video: true);
      expect((plain['setup'] as Map)['generationConfig'], {
        'responseModalities': ['AUDIO'],
      });
      expect(plain, LiveCall.setupJson('m', 'инструкция', null, video: false));
      final gc = (withVideo['setup'] as Map)['generationConfig'] as Map;
      expect(gc, {'responseModalities': ['AUDIO'], 'mediaResolution': 'MEDIA_RESOLUTION_LOW'});
      // остальное в setup то же
      final a = Map<String, dynamic>.from(plain['setup'] as Map)..remove('generationConfig');
      final b = Map<String, dynamic>.from(withVideo['setup'] as Map)..remove('generationConfig');
      expect(b, a);
    });

    test('звонок с video: в первом setup есть mediaResolution, без video — нет', () async {
      final v = make();
      unawaited(v.start());
      await _settle();
      expect(((sockets.single.sent.single['setup'] as Map)['generationConfig'] as Map)['mediaResolution'], 'MEDIA_RESOLUTION_LOW');
      v.end();
      final p = make(video: false);
      unawaited(p.start());
      await _settle();
      expect(((sockets.single.sent.single['setup'] as Map)['generationConfig'] as Map).containsKey('mediaResolution'), isFalse);
      p.end();
    });

    test('sendFrame: realtimeInput.video, image/jpeg, base64 без искажений; счётчики', () async {
      final call = await ready();
      final jpeg = _jpeg(5000);
      expect(call.sendFrame(jpeg), isTrue);
      final m = sockets.single.videos.single['realtimeInput'] as Map;
      expect(m.keys, ['video']);
      expect((m['video'] as Map)['mimeType'], 'image/jpeg');
      expect(base64Decode((m['video'] as Map)['data'] as String), jpeg);
      expect(call.sentFrames, 1);
      expect(call.sentBytes, 5000);
      expect(call.videoStatsLine, contains('кадров 1'));
      expect(call.videoStatsLine, isNot(contains('test-key')));
      call.end();
    });

    test('sendFrame молчит: connecting, до setupComplete, после end, не JPEG, > 150 КБ, звонок без video', () async {
      final call = make();
      expect(call.sendFrame(_jpeg()), isFalse); // до start
      final started = call.start();
      await _settle();
      expect(call.sendFrame(_jpeg()), isFalse); // сокет есть, setupComplete ещё нет
      expect(sockets.single.videos, isEmpty);
      sockets.single.push({'setupComplete': <String, dynamic>{}});
      await started;
      expect(call.sendFrame(Uint8List.fromList([1, 2, 3, 4, 5])), isFalse); // не JPEG
      expect(call.skippedFrames, 1);
      expect(call.sendFrame(_jpeg(150 * 1024 + 1)), isFalse); // тяжелее 150 КБ
      expect(call.skippedFrames, 2);
      expect(call.sendFrame(_jpeg(150 * 1024)), isTrue); // ровно предел — уходит
      expect(sockets.single.videos.length, 1);
      call.end();
      await _settle();
      expect(call.sendFrame(_jpeg()), isFalse); // после end
      expect(sockets.single.videos.length, 1);

      final voice = await ready(video: false);
      expect(voice.sendFrame(_jpeg()), isFalse); // звонок без video
      voice.end();
    });

    test('переподключение: кадры не идут, пока новый сокет не прислал setupComplete', () async {
      final call = await ready();
      expect(call.sendFrame(_jpeg()), isTrue);
      sockets.first.closeCode = 1000;
      await sockets.first.ctrl.close(); // Google закрыл (раз в ~10 минут) — тихо переподключаемся
      await _settle();
      expect(sockets.length, 2);
      expect(call.state.value, CallState.connecting);
      expect(call.sendFrame(_jpeg()), isFalse);
      expect(sockets[1].videos, isEmpty);
      expect(((sockets[1].sent.first['setup'] as Map)['generationConfig'] as Map)['mediaResolution'], isNotNull);
      sockets[1].push({'setupComplete': <String, dynamic>{}});
      await _settle();
      expect(call.state.value, CallState.listening);
      expect(call.sendFrame(_jpeg()), isTrue);
      expect(sockets[1].videos.length, 1);
      call.end();
    });

    test('модель не принимает видео после первого кадра: onVideoRejected, новый setup без mediaResolution, кадров больше нет', () async {
      final call = await ready();
      final reasons = <String>[];
      call.onVideoRejected = reasons.add;
      expect(call.sendFrame(_jpeg()), isTrue);
      sockets.first.closeCode = 1007;
      sockets.first.closeReason = 'Request contains an invalid argument: video input is not supported';
      await sockets.first.ctrl.close();
      await _settle();
      expect(reasons.length, 1);
      expect(reasons.single, isNot(contains('test-key')));
      expect(call.videoActive.value, isFalse);
      expect(sockets.length, 2);
      expect(((sockets[1].sent.first['setup'] as Map)['generationConfig'] as Map).containsKey('mediaResolution'), isFalse);
      sockets[1].push({'setupComplete': <String, dynamic>{}});
      await _settle();
      expect(call.state.value, CallState.listening); // звонок жив — голосом
      expect(call.sendFrame(_jpeg()), isFalse);
      expect(sockets[1].videos, isEmpty);
      expect(reasons.length, 1);
      call.end();
    });

    test('закрытие без причины про видео и спустя время после кадра — обычное переподключение с видео', () async {
      final call = await ready();
      final reasons = <String>[];
      call.onVideoRejected = reasons.add;
      expect(call.sendFrame(_jpeg()), isTrue);
      sockets.first.closeCode = 1011;
      sockets.first.closeReason = 'Internal error';
      await sockets.first.ctrl.close();
      await _settle();
      expect(reasons, isEmpty);
      expect(call.videoActive.value, isTrue);
      expect(((sockets[1].sent.first['setup'] as Map)['generationConfig'] as Map)['mediaResolution'], isNotNull);
      call.end();
    });

    test('setup с видео отвергнут до setupComplete: та же модель без видео, звонок идёт голосом', () async {
      final call = make();
      final reasons = <String>[];
      call.onVideoRejected = reasons.add;
      final started = call.start();
      await _settle();
      sockets.first.closeCode = 1007;
      sockets.first.closeReason = 'Invalid media resolution for this model';
      await sockets.first.ctrl.close();
      await _settle();
      expect(sockets.length, 2);
      final setup = sockets[1].sent.single['setup'] as Map;
      expect(setup['model'], 'models/${LiveCall.videoFirst}'); // модель та же (для видео первая — gemini-2.5 native audio)
      expect((setup['generationConfig'] as Map).containsKey('mediaResolution'), isFalse);
      sockets[1].push({'setupComplete': <String, dynamic>{}});
      await started;
      expect(reasons.length, 1);
      expect(call.videoActive.value, isFalse);
      expect(call.model, LiveCall.videoFirst);
      expect(call.sendFrame(_jpeg()), isFalse);
      call.end();
    });

    test('лимит ключа (RESOURCE_EXHAUSTED) после кадров: звонок завершается ошибкой, без переподключения', () async {
      final call = await ready();
      CallEnd? why;
      String? detail;
      call.onEnded = (w, d) {
        why = w;
        detail = d;
      };
      String? stats;
      call.onVideoStats = (l) => stats = l;
      expect(call.sendFrame(_jpeg()), isTrue);
      sockets.first.closeCode = 1011;
      sockets.first.closeReason = 'RESOURCE_EXHAUSTED: quota exceeded';
      await sockets.first.ctrl.close();
      await _settle();
      expect(why, CallEnd.error);
      expect(detail, contains('RESOURCE_EXHAUSTED'));
      expect(call.quotaExceeded, isTrue);
      expect(sockets.length, 1);
      expect(stats, contains('кадров 1')); // итоги видео — одной строкой при конце звонка
    });

    test('voiceStart растёт на начале реплики, а не на каждом куске голоса', () async {
      final call = await ready();
      final n0 = call.voiceStart.value;
      audio.ctrl.add(_pcm(0.5));
      await _settle();
      audio.ctrl.add(_pcm(0.5));
      await _settle();
      expect(call.voiceStart.value, n0 + 1);
      call.end();
    });
  });

  // -------------------------------------------------------------------------------------------------------------------
  group('FramePolicy (чистая функция)', () {
    final t0 = DateTime(2026, 10, 26, 12);
    const p = FramePolicy();

    FrameCtx ctx({
      Duration at = Duration.zero,
      CallState call = CallState.listening,
      bool cameraOn = true,
      bool muted = false,
      bool busy = false,
      bool economy = false,
      Duration? sentAgo,
      Duration voiceAgo = const Duration(seconds: 1),
      Duration? aiSoundAgo,
      Duration? speechStartAgo,
    }) {
      final now = t0.add(at);
      return FrameCtx(
        now: now,
        call: call,
        cameraOn: cameraOn,
        micMuted: muted,
        busy: busy,
        economy: economy,
        lastSent: sentAgo == null ? null : now.subtract(sentAgo),
        lastUserVoice: now.subtract(voiceAgo),
        lastAiSound: aiSoundAgo == null ? DateTime(2000) : now.subtract(aiSoundAgo),
        activeSince: t0.subtract(const Duration(hours: 1)),
        speechStartedAt: speechStartAgo == null ? null : now.subtract(speechStartAgo),
      );
    }

    FrameDecision d(FrameCtx c) => p.decide(c);
    const s = Duration(seconds: 1);

    test('обычный режим: слушаю — 1 кадр в секунду, ИИ говорит — 1 кадр в 3 секунды', () {
      expect(d(ctx(sentAgo: s * 0.5)), FrameDecision.wait);
      expect(d(ctx(sentAgo: s)), FrameDecision.send);
      expect(d(ctx(call: CallState.thinking, sentAgo: s)), FrameDecision.send);
      expect(d(ctx(call: CallState.speaking, sentAgo: s * 2)), FrameDecision.wait);
      expect(d(ctx(call: CallState.speaking, sentAgo: s * 3)), FrameDecision.send);
      expect(d(ctx()), FrameDecision.send); // ещё ни одного кадра
    });

    test('экономия: без голоса — 0; после голоса — 1 кадр в 3 с; ИИ говорит — 0', () {
      expect(d(ctx(economy: true, voiceAgo: s * 20, sentAgo: s * 10)), FrameDecision.wait);
      expect(d(ctx(economy: true, voiceAgo: s * 5, sentAgo: s * 2)), FrameDecision.wait);
      expect(d(ctx(economy: true, voiceAgo: s * 5, sentAgo: s * 3)), FrameDecision.send);
      expect(d(ctx(economy: true, voiceAgo: s * 7, sentAgo: s * 9)), FrameDecision.wait); // голос был > 6 с назад
      expect(d(ctx(economy: true, call: CallState.speaking, voiceAgo: s * 2, sentAgo: s * 9)), FrameDecision.wait);
      expect(d(ctx(economy: true, voiceAgo: s * 5)), FrameDecision.send);
    });

    test('начало речи: свежий кадр сразу, но не чаще 0,5 с; в обоих режимах', () {
      for (final eco in [false, true]) {
        expect(d(ctx(economy: eco, speechStartAgo: s * 0.1, voiceAgo: s * 0.1, sentAgo: s * 0.7)), FrameDecision.send, reason: 'eco=$eco');
        expect(d(ctx(economy: eco, speechStartAgo: s * 0.1, voiceAgo: s * 0.1, sentAgo: s * 0.3)), FrameDecision.wait, reason: 'eco=$eco');
      }
      // даже когда по расписанию рано (ИИ говорит, кадр был 1 с назад), перебившая речь получает свежий кадр
      expect(d(ctx(call: CallState.speaking, speechStartAgo: s * 0.1, voiceAgo: s * 0.1, sentAgo: s)), FrameDecision.send);
      // кадр уже был после начала речи — по обычному расписанию
      expect(d(ctx(speechStartAgo: s * 1.5, voiceAgo: s * 1.5, sentAgo: s * 0.6)), FrameDecision.wait);
      // ждать свежего кадра дольше окна не нужно
      expect(d(ctx(economy: true, speechStartAgo: s * 3, voiceAgo: s * 3, sentAgo: s * 3.5)), FrameDecision.send); // обычная экономия
    });

    test('микрофон выключен, камера выключена, connecting/ended, камера занята — кадров нет', () {
      expect(d(ctx(muted: true)), FrameDecision.wait);
      expect(d(ctx(cameraOn: false)), FrameDecision.wait);
      expect(d(ctx(call: CallState.connecting)), FrameDecision.wait);
      expect(d(ctx(call: CallState.ended)), FrameDecision.wait);
      expect(d(ctx(busy: true)), FrameDecision.wait);
      expect(d(ctx(busy: true, speechStartAgo: s * 0.1)), FrameDecision.wait);
    });

    test('тишина: 60 с — пауза, 3 мин — камера выключается; голос ИИ и речь человека тишину сбрасывают', () {
      expect(d(ctx(voiceAgo: s * 59)), FrameDecision.send);
      expect(d(ctx(voiceAgo: s * 60)), FrameDecision.pauseSilence);
      expect(d(ctx(voiceAgo: s * 179)), FrameDecision.pauseSilence);
      expect(d(ctx(voiceAgo: s * 180)), FrameDecision.stopCamera);
      expect(d(ctx(voiceAgo: s * 100, aiSoundAgo: s * 5)), FrameDecision.send); // ИИ недавно говорил
      expect(d(ctx(call: CallState.speaking, voiceAgo: s * 500, sentAgo: s * 4)), FrameDecision.send); // ИИ говорит сейчас
      expect(d(ctx(economy: true, voiceAgo: s * 100)), FrameDecision.pauseSilence);
      // камеру выключаем и при выключенном микрофоне — беречь батарею
      expect(d(ctx(muted: true, voiceAgo: s * 200)), FrameDecision.stopCamera);
      // активность отсчитывается и от включения камеры
      final c = FrameCtx(
        now: t0.add(s * 30),
        call: CallState.listening,
        cameraOn: true,
        micMuted: false,
        busy: false,
        economy: false,
        lastSent: null,
        lastUserVoice: DateTime(2000),
        lastAiSound: DateTime(2000),
        activeSince: t0,
      );
      expect(p.decide(c), FrameDecision.send);
      expect(p.silence(c), s * 30);
    });
  });

  // -------------------------------------------------------------------------------------------------------------------
  group('LiveVideo (камера + политика + звонок)', () {
    late List<_FakeSocket> sockets;
    late _FakeAudio audio;
    late _FakeFrames frames;
    late LiveCall call;
    late LiveVideo video;

    Future<void> setUpCall({VideoConfig config = _fast, bool economy = false, bool withVideo = true}) async {
      sockets = [];
      audio = _FakeAudio();
      frames = _FakeFrames();
      call = LiveCall(
        apiKey: 'test-key',
        instruction: 'x',
        audio: audio,
        warnBefore: Duration.zero,
        video: withVideo,
        connect: (uri, headers) async {
          final s = _FakeSocket();
          sockets.add(s);
          return s;
        },
      );
      video = LiveVideo(call: call, frames: frames, config: config, economy: economy);
      final started = call.start();
      await _settle();
      sockets.single.push({'setupComplete': <String, dynamic>{}});
      await started;
    }

    tearDown(() async {
      await video.dispose();
      call.end();
    });

    test('обычный режим: кадры идут сами, «ИИ видит» горит; камера открывается как 640×480 задняя', () async {
      await setUpCall();
      expect(await video.start(), isTrue);
      expect(frames.log.first, 'start back');
      await _waitFor(() => sockets.single.videos.length >= 3);
      expect(video.state.value.phase, VideoPhase.live);
      expect(video.aiSees, isTrue);
      expect(call.sentFrames, greaterThanOrEqualTo(3));
      await video.stop();
      expect(video.state.value.phase, VideoPhase.off);
      expect(frames.open, isFalse);
      final n = sockets.single.videos.length;
      await _settle(150);
      expect(sockets.single.videos.length, n); // после выключения кадров нет
    });

    test('экономия: без голоса кадров нет; голос — свежий кадр сразу и дальше по 3 с (здесь по 120 мс)', () async {
      await setUpCall(economy: true);
      await video.start();
      await _settle(250);
      expect(sockets.single.videos, isEmpty);
      audio.ctrl.add(_pcm(0.5)); // человек начал говорить
      await _waitFor(() => sockets.single.videos.isNotEmpty);
      final first = sockets.single.videos.length;
      expect(first, lessThanOrEqualTo(2));
      audio.ctrl.add(_pcm(0.5));
      await _settle(100);
      audio.ctrl.add(_pcm(0.5));
      await _waitFor(() => sockets.single.videos.length > first);
      // голос стих — кадры прекращаются через окно голоса (500 мс) плюс интервал
      await _settle(900);
      final n = sockets.single.videos.length;
      await _settle(300);
      expect(sockets.single.videos.length, n);
    });

    test('микрофон выключен — кадров нет; включили — идут', () async {
      await setUpCall();
      await call.setMuted(true);
      await video.start();
      await _settle(200);
      expect(sockets.single.videos, isEmpty);
      await call.setMuted(false);
      await _waitFor(() => sockets.single.videos.isNotEmpty);
    });

    test('тишина: пауза (предпросмотр остаётся), голос снимает паузу, долгая тишина выключает камеру', () async {
      await setUpCall(
        config: const VideoConfig(
          tick: Duration(milliseconds: 10),
          normalInterval: Duration(milliseconds: 30),
          freshGap: Duration(milliseconds: 20),
          pauseAfter: Duration(milliseconds: 250),
          stopAfter: Duration(milliseconds: 1200),
          shotTimeout: Duration(milliseconds: 500),
        ),
      );
      await video.start();
      await _waitFor(() => video.state.value.phase == VideoPhase.pausedSilence);
      expect(frames.open, isTrue); // предпросмотр остаётся
      final n = sockets.single.videos.length;
      expect(n, greaterThan(0));
      await _settle(150);
      expect(sockets.single.videos.length, n); // пауза: кадров нет
      audio.ctrl.add(_pcm(0.5)); // сказали что-нибудь — ИИ снова видит
      await _waitFor(() => video.state.value.phase == VideoPhase.live && sockets.single.videos.length > n);
      // дальше тишина до остановки
      await _waitFor(() => video.state.value.phase == VideoPhase.stoppedIdle, ms: 4000);
      expect(frames.open, isFalse);
      expect(videoStateText(video.state.value, 'ru'), contains('батаре'));
      expect(video.cameraOn, isFalse);
    });

    test('сворачивание: inactive — камера отпущена, звонок жив; resumed — вернулась; paused — выключена насовсем', () async {
      await setUpCall();
      await video.start(lensDirection: CameraLensDirection.back);
      await video.onLifecycle(AppLifecycleState.inactive);
      expect(frames.open, isFalse);
      expect(video.state.value.phase, VideoPhase.pausedSystem);
      expect(call.ended, isFalse);
      await video.onLifecycle(AppLifecycleState.resumed);
      expect(frames.open, isTrue);
      expect(video.state.value.phase, VideoPhase.live);
      await _waitFor(() => sockets.single.videos.isNotEmpty);
      await video.onLifecycle(AppLifecycleState.paused);
      expect(frames.open, isFalse);
      expect(video.state.value.phase, VideoPhase.off);
      await video.onLifecycle(AppLifecycleState.resumed);
      expect(frames.open, isFalse); // после paused сам не включается
    });

    test('resumed без предыдущей камеры ничего не включает; inactive при выключенной камере — тоже', () async {
      await setUpCall();
      await video.onLifecycle(AppLifecycleState.inactive);
      await video.onLifecycle(AppLifecycleState.resumed);
      expect(frames.log, isEmpty);
    });

    test('окно «Разрешить камеру» (inactive, пока камера открывается) её не отменяет', () async {
      await setUpCall();
      frames.startGate = Completer<void>();
      final f = video.start();
      await _settle();
      expect(video.state.value.phase, VideoPhase.starting);
      await video.onLifecycle(AppLifecycleState.inactive);
      frames.startGate!.complete();
      expect(await f, isTrue);
      expect(video.cameraOn, isTrue);
    });

    test('отмена: выключили, пока камера открывалась — камера тут же отпущена, кадров нет', () async {
      await setUpCall();
      frames.startGate = Completer<void>();
      final f = video.start();
      await _settle();
      await video.stop();
      frames.startGate!.complete();
      expect(await f, isFalse);
      expect(frames.open, isFalse);
      expect(video.cameraOn, isFalse);
      await _settle(100);
      expect(sockets.single.videos, isEmpty);
    });

    test('ошибки камеры: нет права, занята, нет передней — понятные ключи; звонок продолжается', () async {
      await setUpCall();
      frames.startError = const FrameException(FrameFail.denied);
      expect(await video.start(), isFalse);
      expect(video.state.value, const VideoState(VideoPhase.error, 'cameraDenied'));
      expect(call.ended, isFalse);
      frames.startError = const FrameException(FrameFail.busy);
      expect(await video.start(), isFalse);
      expect(video.state.value.errorKey, 'cameraBusy');
      frames.startError = const FrameException(FrameFail.noCamera);
      expect(await video.start(lensDirection: CameraLensDirection.front), isFalse);
      expect(video.state.value.errorKey, 'noCamera');
      frames.startError = null;
      expect(await video.start(), isTrue); // «Повторить»
    });

    test('3 неудачных снимка подряд — камера выключается с shotFailed; успешный снимок счёт сбрасывает', () async {
      await setUpCall();
      frames.failGrabs = 2;
      await video.start();
      await _waitFor(() => sockets.single.videos.isNotEmpty);
      expect(video.cameraOn, isTrue); // 2 ошибки, потом удача
      frames.failGrabs = 3;
      await _waitFor(() => !video.cameraOn);
      expect(video.state.value, const VideoState(VideoPhase.error, 'shotFailed'));
      expect(frames.open, isFalse);
    });

    test('конец звонка выключает камеру; лимит ключа — ошибка quota', () async {
      await setUpCall();
      await video.start();
      call.end();
      await _waitFor(() => !frames.open);
      expect(video.state.value.phase, VideoPhase.off);
      expect(video.start(), completion(isFalse)); // после конца звонка не включается
    });

    test('лимит ключа ИИ: камера выключена, состояние quota', () async {
      await setUpCall();
      await video.start();
      await _waitFor(() => sockets.single.videos.isNotEmpty);
      sockets.single.closeCode = 1011;
      sockets.single.closeReason = 'RESOURCE_EXHAUSTED';
      await sockets.single.ctrl.close();
      await _waitFor(() => video.state.value.errorKey == 'quota');
      expect(frames.open, isFalse);
      expect(videoMsg('quota', 'ru'), contains('Лимит'));
    });

    test('модель отвергла видео: камера выключается, состояние videoRejected, повторно не включается', () async {
      await setUpCall();
      await video.start();
      await _waitFor(() => sockets.single.videos.isNotEmpty);
      sockets.single.closeCode = 1007;
      sockets.single.closeReason = 'video is not supported';
      await sockets.single.ctrl.close();
      await _waitFor(() => video.state.value.errorKey == 'videoRejected');
      expect(frames.open, isFalse);
      expect(await video.start(), isFalse);
      expect(video.state.value.errorKey, 'videoRejected');
      sockets[1].push({'setupComplete': <String, dynamic>{}});
      await _settle();
      expect(call.ended, isFalse); // разговор продолжается голосом
    });

    test('звонок без video: камера не включается', () async {
      await setUpCall(withVideo: false);
      expect(await video.start(), isFalse);
      expect(video.state.value.errorKey, 'videoRejected');
      expect(frames.log, isEmpty);
    });

    test('другая камера: кадры на паузе, фронтальная, фонарик гаснет и недоступен; ошибка — прежняя камера', () async {
      await setUpCall();
      await video.start();
      expect(await video.setTorch(true), isTrue);
      expect(video.canSwitch, isTrue);
      expect(await video.switchCamera(), isTrue);
      expect(video.lens, CameraLensDirection.front);
      expect(video.torch, isFalse);
      expect(frames.log.last, 'start front');
      expect(await video.setTorch(true), isFalse); // у фронтальной фонарика нет
      // вернуть заднюю
      expect(await video.switchCamera(), isTrue);
      expect(video.lens, CameraLensDirection.back);
      // сбой: камера не открылась — остаёмся на прежней
      frames.hasFront = true;
      final startsBefore = frames.log.where((l) => l.startsWith('start')).length;
      frames.startError = const FrameException(FrameFail.busy);
      expect(await video.switchCamera(), isFalse);
      expect(frames.log.where((l) => l.startsWith('start')).length, startsBefore + 2); // попытка + возврат
      expect(video.lens, CameraLensDirection.back);
    });

    test('одна камера — переключения нет', () async {
      await setUpCall();
      frames.hasFront = false;
      await video.start();
      expect(video.canSwitch, isFalse);
      expect(await video.switchCamera(), isFalse);
    });

    test('чёткий снимок: кадры на паузе, байты и штрихкод отданы, после — снова live', () async {
      await setUpCall();
      await video.start();
      await _waitFor(() => sockets.single.videos.isNotEmpty);
      frames.stillBytes = _jpeg(9000);
      frames.barcode = '4600051000010';
      frames.stillDelay = const Duration(milliseconds: 150);
      final grabsBefore = frames.grabs;
      final f = video.takeStill(ResolutionPreset.high);
      await _settle(40);
      expect(video.state.value.phase, VideoPhase.shooting);
      expect(videoStateText(video.state.value, 'ky'), isNotNull);
      final r = await f;
      expect(r.ok, isTrue);
      expect(r.bytes!.length, 9000);
      expect(r.barcode, '4600051000010');
      expect(frames.log, contains('still high'));
      expect(video.state.value.phase, VideoPhase.live);
      expect(frames.grabs - grabsBefore, lessThanOrEqualTo(1)); // за время снимка новых кадров не брали
    });

    test('чёткий снимок: не вышел или не уложился в срок — stillFailed; камеру потеряли — выключается', () async {
      await setUpCall();
      await video.start();
      frames.stillFails = true;
      var r = await video.takeStill(ResolutionPreset.veryHigh);
      expect(r.ok, isFalse);
      expect(r.errorKey, 'stillFailed');
      frames.stillFails = false;
      frames.stillDelay = const Duration(seconds: 2);
      r = await video.takeStill(ResolutionPreset.high);
      expect(r.errorKey, 'stillFailed');
      expect(video.cameraOn, isTrue);
      frames.stillDelay = Duration.zero;
      frames.stillLosesCamera = true;
      frames.stillBytes = _jpeg();
      await video.takeStill(ResolutionPreset.high);
      await _waitFor(() => !video.cameraOn);
      expect(video.state.value.errorKey, 'cameraBusy');
    });

    test('dispose: камера отпущена, остатки файлов убраны, голос после этого ничего не запускает', () async {
      await setUpCall();
      await video.start();
      await video.dispose();
      expect(frames.open, isFalse);
      expect(frames.purged, isTrue);
      final n = sockets.single.videos.length;
      audio.ctrl.add(_pcm(0.5));
      await _settle(150);
      expect(sockets.single.videos.length, n);
    });
  });

  // -------------------------------------------------------------------------------------------------------------------
  group('CameraLiveFrames: временные файлы', () {
    late Directory dir;
    late List<String> log;
    late int n;

    setUp(() async {
      dir = await Directory.systemTemp.createTemp('live_video_test');
      log = [];
      n = 0;
    });
    tearDown(() async {
      if (await dir.exists()) await dir.delete(recursive: true);
    });

    Future<String> shoot(CameraController? _) async {
      log.add('shoot');
      final f = File('${dir.path}/CAP${++n}.jpg');
      await f.writeAsBytes(_jpeg(3000));
      return f.path;
    }

    CameraLiveFrames make({FrameShooter? shooter, FrameReader? reader, BarcodeReader? barcode, FrameOpener? opener}) => CameraLiveFrames(
      config: const VideoConfig(shotTimeout: Duration(milliseconds: 300), stillTimeout: Duration(milliseconds: 300)),
      opener: opener ??
          (preset, lens) async {
            log.add('open ${preset.name} ${lens.name}');
            return null;
          },
      shooter: shooter ?? shoot,
      reader: reader,
      releaser: (_) {},
      barcode: barcode ?? (p) async => null,
      tempDir: () async => dir,
      hasLens: (l) async => true,
    );

    int files() => dir.listSync().whereType<File>().length;

    test('grab: кадр отдан, файл удалён', () async {
      final fr = make();
      await fr.start(lens: CameraLensDirection.back);
      final b = await fr.grab();
      expect(b, isNotNull);
      expect(b!.length, 3000);
      expect(files(), 0);
      expect(fr.lastFrame, isNotNull);
      expect(fr.hasFront && fr.hasBack, isTrue);
      await fr.stop();
      expect(fr.lastFrame, isNull); // кадр в памяти обнуляется в stop()
      expect(await fr.grab(), isNull); // камера закрыта
    });

    test('grab: ошибка чтения — файл всё равно удалён, FrameException(shot)', () async {
      final fr = make(reader: (p) async => throw const FileSystemException('read failed'));
      await fr.start(lens: CameraLensDirection.back);
      await expectLater(fr.grab(), throwsA(isA<FrameException>().having((e) => e.kind, 'kind', FrameFail.shot)));
      expect(files(), 0);
    });

    test('grab: снимок не уложился в срок — FrameException(shot); следующий кадр не ждёт очереди (занято — null)', () async {
      final gate = Completer<String>();
      final fr = make(shooter: (c) => gate.future);
      await fr.start(lens: CameraLensDirection.back);
      final first = fr.grab();
      expect(await fr.grab(), isNull); // прошлый кадр не готов — пропуск без очереди
      await expectLater(first, throwsA(isA<FrameException>()));
    });

    test('start: нет права / камера занята / нет камеры — свои причины', () async {
      for (final (code, kind) in [
        ('CameraAccessDenied', FrameFail.denied),
        ('CameraAccessDeniedWithoutPrompt', FrameFail.denied),
        ('CameraAccessRestricted', FrameFail.denied),
        ('noCamera', FrameFail.noCamera),
        ('cameraInUse', FrameFail.busy),
      ]) {
        final fr = make(opener: (p, l) async => throw CameraException(code, 'x'));
        await expectLater(fr.start(lens: CameraLensDirection.back), throwsA(isA<FrameException>().having((e) => e.kind, code, kind)));
        expect(fr.isOpen, isFalse);
      }
      final fr = make(opener: (p, l) async => throw StateError('boom'));
      await expectLater(fr.start(lens: CameraLensDirection.front), throwsA(isA<FrameException>().having((e) => e.kind, 'kind', FrameFail.busy)));
    });

    test('still: камера закрывается, открывается в нужном качестве, штрихкод читается ДО удаления файла, потом 640×480', () async {
      var existedForBarcode = false;
      final fr = make(
        barcode: (p) async {
          existedForBarcode = File(p).existsSync();
          log.add('barcode');
          return '4600051000010';
        },
      );
      await fr.start(lens: CameraLensDirection.front);
      log.clear();
      final b = await fr.still(ResolutionPreset.veryHigh);
      expect(b!.length, 3000);
      expect(existedForBarcode, isTrue);
      expect(await fr.barcodeOfLastStill(), '4600051000010');
      expect(log, ['open veryHigh front', 'shoot', 'barcode', 'open medium front']);
      expect(files(), 0);
      expect(fr.isOpen, isTrue);
    });

    test('still: ошибка — файл удалён, камера возвращена в 640×480', () async {
      final fr = make(reader: (p) async => throw const FileSystemException('x'));
      await fr.start(lens: CameraLensDirection.back);
      log.clear();
      await expectLater(fr.still(ResolutionPreset.high), throwsA(isA<FrameException>()));
      expect(files(), 0);
      expect(log.last, 'open medium back');
      expect(fr.isOpen, isTrue);
    });

    test('still: выключили (stop) во время снимка — камера обратно не открывается', () async {
      final gate = Completer<String>();
      final fr = make(shooter: (c) => gate.future);
      await fr.start(lens: CameraLensDirection.back);
      log.clear();
      final f = fr.still(ResolutionPreset.high);
      await _settle();
      await fr.stop();
      final f2 = expectLater(f, throwsA(isA<FrameException>()));
      gate.completeError(StateError('stopped'));
      await f2;
      expect(fr.isOpen, isFalse);
      expect(log.where((l) => l == 'open medium back'), isEmpty);
    });

    test('purgeLeftovers: удаляет только свои файлы CAP*.jpg старше минуты', () async {
      final old = File('${dir.path}/CAP111.jpg')..writeAsBytesSync([1]);
      final fresh = File('${dir.path}/CAP222.jpg')..writeAsBytesSync([1]);
      final other = File('${dir.path}/photo.jpg')..writeAsBytesSync([1]);
      final otherOld = File('${dir.path}/report.pdf')..writeAsBytesSync([1]);
      final nameLike = File('${dir.path}/CAPTURE.txt')..writeAsBytesSync([1]);
      final past = DateTime.now().subtract(const Duration(minutes: 5));
      old.setLastModifiedSync(past);
      other.setLastModifiedSync(past);
      otherOld.setLastModifiedSync(past);
      nameLike.setLastModifiedSync(past);
      final removed = await CameraLiveFrames.purgeLeftovers(dir);
      expect(removed, 1);
      expect(old.existsSync(), isFalse);
      expect(fresh.existsSync(), isTrue);
      expect(other.existsSync(), isTrue);
      expect(otherOld.existsSync(), isTrue);
      expect(nameLike.existsSync(), isTrue);
      expect(await CameraLiveFrames.purgeLeftovers(Directory('${dir.path}/нет')), 0);
    });

    test('purge() при старте и по запросу чистит старые остатки', () async {
      final old = File('${dir.path}/CAP9.jpg')..writeAsBytesSync([1]);
      old.setLastModifiedSync(DateTime.now().subtract(const Duration(minutes: 3)));
      final fr = make();
      await fr.start(lens: CameraLensDirection.back);
      await _settle(50);
      expect(old.existsSync(), isFalse);
    });
  });

  test('тексты ошибок: все ключи на 5 языках, не пустые; ky/uz/tr/en не копия русского', () {
    for (final k in ['cameraDenied', 'cameraBusy', 'noCamera', 'shotFailed', 'stillFailed', 'videoRejected', 'quota', 'paused', 'pausedSilence', 'stopped', 'shooting']) {
      final ru = videoMsg(k, 'ru');
      expect(ru, isNot(k), reason: k);
      for (final l in ['ky', 'uz', 'en', 'tr']) {
        final t = videoMsg(k, l);
        expect(t.trim(), isNotEmpty, reason: '$k/$l');
        expect(t, isNot(ru), reason: '$k/$l совпадает с русским');
      }
    }
    expect(videoMsg('нет такого', 'ru'), 'нет такого');
    expect(videoMsg('paused', 'de'), videoMsg('paused', 'ru')); // неизвестный язык — русский
    expect(videoStateText(const VideoState(VideoPhase.live), 'ru'), isNull);
    expect(videoStateText(const VideoState(VideoPhase.error, 'cameraDenied'), 'tr'), contains('Kamera'));
  });
}
