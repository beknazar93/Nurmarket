// 2026-10-26 (видеозвонок, В1): движок видео для звонка с Нур Советником. Камера → JPEG 640×480 в памяти → LiveCall.sendFrame.
// Состав: LiveFrames (источник кадров, настоящий — CameraLiveFrames поверх CameraGate), FramePolicy (чистая функция «слать
// ли кадр сейчас»), LiveVideo (склейка: таймер, паузы, сворачивание, переключение камеры, чёткий снимок, лимиты) и таблица
// текстов ошибок на 5 языков. Экран (advisor_call.dart) этого файла только вызывает — он делается в В2.
// Кадры нигде не пишутся: временный файл takePicture читается и удаляется сразу (в finally), остатки чистятся по имени CAP*.jpg.
import 'dart:async';
import 'dart:io';
import 'dart:ui' show AppLifecycleState;

import 'package:camera/camera.dart';
import 'package:flutter/foundation.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:path_provider/path_provider.dart';

import '../../features/scan/live_shot.dart' show CameraGate;
import 'live_call.dart';

// ---------------------------------------------------------------------------------------------------------------------
// Настройки (всё, что нужно подкрутить после проверки на телефоне — раздел 12 ТЗ, — здесь)
// ---------------------------------------------------------------------------------------------------------------------

class VideoConfig {
  /// «Обычный»: слушаю / думаю — 1 кадр в секунду; ИИ говорит — реже.
  final Duration normalInterval;
  final Duration slowInterval;

  /// «Экономия»: 1 кадр в 3 с и только пока человек говорит (голос был не раньше [economyVoiceWindow] назад).
  final Duration economyInterval;
  final Duration economyVoiceWindow;

  /// Начало речи: свежий кадр сразу, но не чаще [freshGap]; ждать его не дольше [freshWindow].
  final Duration freshGap;
  final Duration freshWindow;

  /// Тишина (никто не говорит): [pauseAfter] — кадры не шлём; [stopAfter] — камера выключается.
  final Duration pauseAfter;
  final Duration stopAfter;

  /// Шаг таймера, срок одного кадра, срок чёткого снимка, ожидание текущего кадра перед чётким снимком.
  final Duration tick;
  final Duration shotTimeout;
  final Duration stillTimeout;
  final Duration stillGrabWait;

  /// Сколько раз подряд снимок может не получиться, прежде чем камера выключится.
  final int maxShotFailures;

  /// Остатки временных файлов камеры старше этого возраста удаляются.
  final Duration leftoverAge;

  /// «ИИ видит» горит, пока последний кадр ушёл не раньше этого.
  final Duration seesWindow;
  const VideoConfig({
    this.normalInterval = const Duration(seconds: 1),
    this.slowInterval = const Duration(seconds: 3),
    this.economyInterval = const Duration(seconds: 3),
    this.economyVoiceWindow = const Duration(seconds: 6),
    this.freshGap = const Duration(milliseconds: 500),
    this.freshWindow = const Duration(seconds: 2),
    this.pauseAfter = const Duration(seconds: 60),
    this.stopAfter = const Duration(minutes: 3),
    this.tick = const Duration(milliseconds: 250),
    this.shotTimeout = const Duration(seconds: 3),
    this.stillTimeout = const Duration(seconds: 6),
    this.stillGrabWait = const Duration(milliseconds: 1500),
    this.maxShotFailures = 3,
    this.leftoverAge = const Duration(minutes: 1),
    this.seesWindow = const Duration(seconds: 2),
  });
}

// ---------------------------------------------------------------------------------------------------------------------
// Политика кадров — чистая функция без камеры и сети
// ---------------------------------------------------------------------------------------------------------------------

enum FrameDecision {
  /// Снять и отправить кадр сейчас.
  send,

  /// Сейчас не шлём (рано, ИИ говорит, занято…).
  wait,

  /// Долго тихо: кадры не шлём, предпросмотр остаётся («Пауза — ИИ не видит»).
  pauseSilence,

  /// Совсем долго тихо: камеру выключить.
  stopCamera,
}

/// Всё, что нужно политике, одним объектом (время подставляется — тесты не ждут).
class FrameCtx {
  final DateTime now;
  final CallState call;
  final bool cameraOn;
  final bool micMuted;

  /// Идёт снимок/чёткий снимок/переключение камеры или прошлый кадр ещё не готов.
  final bool busy;
  final bool economy;

  /// Когда последний раз пробовали отправить кадр (null — ещё ни разу).
  final DateTime? lastSent;

  /// Последний голос человека и конец звука ИИ; [activeSince] — когда включили камеру.
  final DateTime lastUserVoice;
  final DateTime lastAiSound;
  final DateTime activeSince;

  /// Когда человек начал новую реплику (null — не было).
  final DateTime? speechStartedAt;
  const FrameCtx({
    required this.now,
    required this.call,
    required this.cameraOn,
    required this.micMuted,
    required this.busy,
    required this.economy,
    required this.lastSent,
    required this.lastUserVoice,
    required this.lastAiSound,
    required this.activeSince,
    this.speechStartedAt,
  });
}

class FramePolicy {
  final VideoConfig config;
  const FramePolicy([this.config = const VideoConfig()]);

  /// Сколько молчат: от последнего голоса человека, конца речи ИИ и включения камеры. Пока ИИ говорит — 0.
  Duration silence(FrameCtx c) {
    if (c.call == CallState.speaking) return Duration.zero;
    var last = c.activeSince;
    if (c.lastUserVoice.isAfter(last)) last = c.lastUserVoice;
    final ai = c.lastAiSound.isAfter(c.now) ? c.now : c.lastAiSound;
    if (ai.isAfter(last)) last = ai;
    final d = c.now.difference(last);
    return d.isNegative ? Duration.zero : d;
  }

  FrameDecision decide(FrameCtx c) {
    if (!c.cameraOn || c.call == CallState.connecting || c.call == CallState.ended) return FrameDecision.wait;
    final quiet = silence(c);
    if (quiet >= config.stopAfter) return FrameDecision.stopCamera;
    if (c.micMuted || c.busy) return FrameDecision.wait;
    if (quiet >= config.pauseAfter) return FrameDecision.pauseSilence;
    final since = c.lastSent == null ? null : c.now.difference(c.lastSent!);
    // Начало речи: ИИ должен увидеть то, о чём спрашивают.
    final started = c.speechStartedAt;
    if (started != null && (c.lastSent == null || c.lastSent!.isBefore(started)) && c.now.difference(started) <= config.freshWindow) {
      return since == null || since >= config.freshGap ? FrameDecision.send : FrameDecision.wait;
    }
    final speaking = c.call == CallState.speaking;
    if (c.economy) {
      if (speaking) return FrameDecision.wait;
      final voiceAgo = c.now.difference(c.lastUserVoice);
      if (voiceAgo > config.economyVoiceWindow) return FrameDecision.wait;
      return since == null || since >= config.economyInterval ? FrameDecision.send : FrameDecision.wait;
    }
    final every = speaking ? config.slowInterval : config.normalInterval;
    return since == null || since >= every ? FrameDecision.send : FrameDecision.wait;
  }

  bool shouldSend(FrameCtx c) => decide(c) == FrameDecision.send;
}

// ---------------------------------------------------------------------------------------------------------------------
// Источник кадров
// ---------------------------------------------------------------------------------------------------------------------

enum FrameFail {
  /// Нет права камеры (CameraAccessDenied*).
  denied,

  /// Камера занята или не открылась.
  busy,

  /// Нет такой камеры (передней).
  noCamera,

  /// Снимок не получился.
  shot,
}

class FrameException implements Exception {
  final FrameFail kind;
  const FrameException(this.kind);
  @override
  String toString() => 'FrameException(${kind.name})';
}

/// Камера звонка. В тестах — фейк; настоящая — [CameraLiveFrames].
abstract class LiveFrames {
  /// Открыть камеру (через CameraGate, 640×480). Бросает [FrameException].
  Future<void> start({required CameraLensDirection lens});

  /// Кадр для Live (JPEG в памяти, файл уже удалён). null — камера занята прошлым кадром или закрыта (без очереди).
  /// Бросает [FrameException], если снимок не получился.
  Future<Uint8List?> grab();

  /// Чёткий снимок: камера закрывается, открывается в [preset], снимок, штрихкод со снимка, файл удаляется, камера
  /// возвращается в 640×480. null — камера занята.
  Future<Uint8List?> still(ResolutionPreset preset);
  Future<String?> barcodeOfLastStill();
  Future<void> setTorch(bool on);
  Future<void> stop();

  /// Стереть остатки временных файлов камеры (при старте и конце звонка).
  Future<void> purge();
  CameraController? get controller;
  bool get isOpen;

  /// Последний кадр (в памяти) — показывается на месте предпросмотра, пока камера переоткрывается; stop() обнуляет.
  Uint8List? get lastFrame;
  bool get hasFront;
  bool get hasBack;
}

typedef FrameOpener = Future<CameraController?> Function(ResolutionPreset preset, CameraLensDirection lens);
typedef FrameShooter = Future<String> Function(CameraController? c);
typedef FrameReader = Future<Uint8List> Function(String path);
typedef FrameReleaser = void Function(CameraController c);
typedef BarcodeReader = Future<String?> Function(String path);

class CameraLiveFrames implements LiveFrames {
  /// Имя временного файла takePicture у camera_android_camerax: File.createTempFile("CAP", ".jpg", cacheDir) —
  /// проверено по исходникам плагина 0.7.5+1 (ImageCaptureProxyApi). Удаляем только такие файлы.
  static const tempPrefix = 'CAP';
  static const tempSuffix = '.jpg';

  final VideoConfig config;
  final FrameOpener _opener;
  final FrameShooter _shooter;
  final FrameReader _reader;
  final FrameReleaser _releaser;
  final BarcodeReader _barcode;
  final Future<Directory> Function() _tempDir;
  final Future<bool> Function(CameraLensDirection) _hasLens;

  CameraLiveFrames({
    this.config = const VideoConfig(),
    FrameOpener? opener,
    FrameShooter? shooter,
    FrameReader? reader,
    FrameReleaser? releaser,
    BarcodeReader? barcode,
    Future<Directory> Function()? tempDir,
    Future<bool> Function(CameraLensDirection)? hasLens,
  }) : _opener = opener ?? ((preset, lens) => CameraGate.open(preset, lens: lens)),
       _shooter = shooter ?? _defaultShooter,
       _reader = reader ?? ((p) => File(p).readAsBytes()),
       _releaser = releaser ?? CameraGate.release,
       _barcode = barcode ?? _defaultBarcode,
       _tempDir = tempDir ?? getTemporaryDirectory,
       _hasLens = hasLens ?? CameraGate.hasLens;

  static Future<String> _defaultShooter(CameraController? c) async {
    if (c == null || !c.value.isInitialized) throw StateError('camera closed');
    return (await c.takePicture()).path;
  }

  /// Штрихкод со снимка: analyzeImage по файлу, камеру не занимает (как в payment_qr.dart).
  static Future<String?> _defaultBarcode(String path) async {
    final scanner = MobileScannerController(autoStart: false);
    try {
      final r = await scanner.analyzeImage(path);
      final v = r?.barcodes.firstOrNull?.rawValue;
      return v == null || v.trim().isEmpty ? null : v.trim();
    } finally {
      await scanner.dispose();
    }
  }

  CameraController? _controller;
  bool _open = false;
  bool _starting = false;
  bool _busy = false;
  bool _stilling = false;
  CameraLensDirection _lens = CameraLensDirection.back;
  Uint8List? _last;
  String? _lastBarcode;
  bool _front = false;
  bool _back = true;
  bool _purged = false;

  /// Растёт при start/stop: чёткий снимок, которого выключили на полпути, камеру обратно не открывает.
  int _epoch = 0;

  @override
  CameraController? get controller => _controller;
  @override
  bool get isOpen => _open;
  @override
  Uint8List? get lastFrame => _last;
  @override
  bool get hasFront => _front;
  @override
  bool get hasBack => _back;

  @override
  Future<void> start({required CameraLensDirection lens}) async {
    if (_starting) throw const FrameException(FrameFail.busy);
    _starting = true;
    _epoch++;
    try {
      if (!_purged) {
        _purged = true;
        unawaited(purge());
      }
      await _closeCamera();
      _lens = lens;
      await _openCamera(ResolutionPreset.medium, lens);
      _front = await _hasLens(CameraLensDirection.front);
      _back = await _hasLens(CameraLensDirection.back);
    } finally {
      _starting = false;
    }
  }

  Future<void> _openCamera(ResolutionPreset preset, CameraLensDirection lens) async {
    final CameraController? c;
    try {
      c = await _opener(preset, lens);
    } on CameraException catch (e) {
      throw FrameException(_kindOf(e));
    } on FrameException {
      rethrow;
    } catch (_) {
      throw const FrameException(FrameFail.busy);
    }
    _controller = c;
    _open = true;
    if (c != null) {
      try {
        await c.setFlashMode(FlashMode.off); // вспышка «auto» мигала бы при каждом кадре
      } catch (_) {
        // не все камеры умеют
      }
    }
  }

  static FrameFail _kindOf(CameraException e) {
    switch (e.code) {
      case 'CameraAccessDenied':
      case 'CameraAccessDeniedWithoutPrompt':
      case 'CameraAccessRestricted':
        return FrameFail.denied;
      case 'noCamera':
        return FrameFail.noCamera;
      default:
        return FrameFail.busy;
    }
  }

  Future<void> _closeCamera() async {
    final c = _controller;
    _controller = null;
    _open = false;
    if (c != null) _releaser(c);
  }

  @override
  Future<Uint8List?> grab() async {
    if (!_open || _busy || _stilling) return null;
    _busy = true;
    final c = _controller;
    String? path;
    try {
      path = await _shooter(c).timeout(config.shotTimeout);
      final bytes = await _reader(path);
      if (!_open) return null; // камеру выключили, пока снимали — кадр выбрасываем
      _last = bytes;
      return bytes;
    } catch (_) {
      throw const FrameException(FrameFail.shot);
    } finally {
      await _delete(path);
      _busy = false;
    }
  }

  @override
  Future<Uint8List?> still(ResolutionPreset preset) async {
    if (!_open || _busy || _stilling) return null;
    _stilling = true;
    _lastBarcode = null;
    final lens = _lens;
    final epoch = _epoch;
    String? path;
    try {
      await _closeCamera();
      await _openCamera(preset, lens);
      if (epoch != _epoch) throw const FrameException(FrameFail.shot); // выключили, пока камера открывалась
      path = await _shooter(_controller).timeout(config.stillTimeout);
      final bytes = await _reader(path);
      try {
        _lastBarcode = await _barcode(path); // сначала штрихкод по файлу — потом файл удаляется
      } catch (_) {
        _lastBarcode = null;
      }
      return bytes;
    } on FrameException {
      rethrow;
    } catch (_) {
      throw const FrameException(FrameFail.shot);
    } finally {
      await _delete(path);
      await _closeCamera();
      if (epoch == _epoch) {
        try {
          await _openCamera(ResolutionPreset.medium, lens);
          if (epoch != _epoch) await _closeCamera(); // выключили, пока возвращали камеру
        } catch (_) {
          // вернуть камеру не вышло: isOpen=false, LiveVideo выключит камеру с понятным текстом
        }
      }
      _stilling = false;
    }
  }

  @override
  Future<String?> barcodeOfLastStill() async => _lastBarcode;

  @override
  Future<void> setTorch(bool on) async {
    final c = _controller;
    if (c == null || !_open) return;
    try {
      await c.setFlashMode(on ? FlashMode.torch : FlashMode.off);
    } catch (_) {
      // у части камер фонарика нет
    }
  }

  @override
  Future<void> stop() async {
    _epoch++;
    _last = null;
    _lastBarcode = null;
    await _closeCamera();
  }

  Future<void> _delete(String? path) async {
    if (path == null) return;
    try {
      final f = File(path);
      if (await f.exists()) await f.delete();
    } catch (_) {
      // файл в кэше приложения; остаток уберёт purge()
    }
  }

  @override
  Future<void> purge() async {
    try {
      await purgeLeftovers(await _tempDir(), minAge: config.leftoverAge);
    } catch (_) {}
  }

  /// Удалить остатки CAP*.jpg старше [minAge] (свежие могут принадлежать идущему снимку другого экрана). Чужие файлы не трогает.
  static Future<int> purgeLeftovers(Directory dir, {Duration minAge = const Duration(minutes: 1), DateTime? now}) async {
    var n = 0;
    if (!await dir.exists()) return 0;
    final at = now ?? DateTime.now();
    await for (final e in dir.list(followLinks: false)) {
      if (e is! File) continue;
      final name = e.uri.pathSegments.isEmpty ? '' : e.uri.pathSegments.last;
      if (!name.startsWith(tempPrefix) || !name.toLowerCase().endsWith(tempSuffix)) continue;
      try {
        if (at.difference(await e.lastModified()) < minAge) continue;
        await e.delete();
        n++;
      } catch (_) {}
    }
    return n;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Состояние и тексты
// ---------------------------------------------------------------------------------------------------------------------

enum VideoPhase {
  /// Камера выключена.
  off,

  /// Открывается (может висеть окно «Разрешить камеру»).
  starting,

  /// Включена, кадры идут по политике.
  live,

  /// Долго тихо: предпросмотр остаётся, кадры не идут.
  pausedSilence,

  /// Шторка/системное окно: камера отпущена, вернётся сама.
  pausedSystem,

  /// Чёткий снимок.
  shooting,

  /// Смена камеры.
  switching,

  /// Выключилась сама после долгой тишины.
  stoppedIdle,

  /// Ошибка — [VideoState.errorKey] (ключ для [videoMsg]).
  error,
}

class VideoState {
  final VideoPhase phase;
  final String? errorKey;
  const VideoState(this.phase, [this.errorKey]);
  @override
  bool operator ==(Object other) => other is VideoState && other.phase == phase && other.errorKey == errorKey;
  @override
  int get hashCode => Object.hash(phase, errorKey);
  @override
  String toString() => 'VideoState($phase${errorKey == null ? '' : ', $errorKey'})';
}

/// Чёткий снимок для «Что это? / Накладная / Полка / Ценник»: JPEG и штрихкод с него (если прочитался).
class VideoStill {
  final Uint8List? bytes;
  final String? barcode;
  final String? errorKey;
  const VideoStill({this.bytes, this.barcode, this.errorKey});
  bool get ok => bytes != null;
}

// 2026-10-26 (видеозвонок): тексты ошибок и подписей движка — ru, ky, uz, en, tr (как nav_tools/toolMsg). Экран В2/В3
// может заменить их ключами l10n, но без них движок уже говорит понятно на всех языках.
const _vm = <String, List<String>>{
  'cameraDenied': [
    'Нет доступа к камере. Разрешите камеру для NurCRM в настройках телефона.',
    'Камерага кирүүгө уруксат жок. Телефондун жөндөөлөрүндө NurCRM үчүн камераны уруксаттаңыз.',
    "Kameraga ruxsat yo'q. Telefon sozlamalarida NurCRM uchun kameraga ruxsat bering.",
    'No camera access. Allow the camera for NurCRM in the phone settings.',
    'Kameraya erişim yok. Telefon ayarlarında NurCRM için kameraya izin verin.',
  ],
  'cameraBusy': [
    'Камера занята. Повторите через секунду',
    'Камера бош эмес. Бир секунддан кийин кайталаңыз',
    "Kamera band. Bir soniyadan keyin qayta urinib ko'ring",
    'The camera is busy. Try again in a second',
    'Kamera meşgul. Bir saniye sonra tekrar deneyin',
  ],
  'noCamera': [
    'На телефоне нет такой камеры',
    'Телефондо мындай камера жок',
    "Telefonda bunday kamera yo'q",
    'This phone has no such camera',
    'Bu telefonda böyle bir kamera yok',
  ],
  'shotFailed': [
    'Камера не отдаёт кадры — выключил её. Попробуйте снова',
    'Камера кадр бербей жатат — өчүрдүм. Кайра аракет кылыңыз',
    "Kamera kadr bermayapti — o'chirdim. Qayta urinib ko'ring",
    'The camera is not giving frames — I turned it off. Try again',
    'Kamera görüntü vermiyor — kapattım. Tekrar deneyin',
  ],
  'stillFailed': [
    'Камера не сделала снимок. Повторить?',
    'Камера сүрөткө тартпады. Кайталайбызбы?',
    "Kamera suratga olmadi. Qayta urinamizmi?",
    'The camera did not take the shot. Try again?',
    'Kamera fotoğraf çekmedi. Tekrar deneyelim mi?',
  ],
  'videoRejected': [
    'Эта модель ИИ не принимает видео — разговор продолжается голосом',
    'Бул ИИ модели видеону кабыл албайт — маектеш үн менен улантылат',
    "Bu SI modeli videoni qabul qilmaydi — suhbat ovoz bilan davom etadi",
    'This AI model does not accept video — the call continues by voice',
    'Bu YZ modeli videoyu kabul etmiyor — görüşme sesle devam ediyor',
  ],
  'quota': [
    'Лимит ключа ИИ на сегодня исчерпан. Включите «Экономию» или попробуйте позже',
    'ИИ ачкычынын бүгүнкү лимити түгөндү. «Үнөмдөөнү» күйгүзүңүз же кийинчерээк аракет кылыңыз',
    "SI kalitining bugungi limiti tugadi. «Tejash»ni yoqing yoki keyinroq urinib ko'ring",
    'Today\'s AI key limit is used up. Turn on "Economy" or try later',
    'YZ anahtarının bugünkü limiti doldu. «Tasarruf»u açın veya daha sonra deneyin',
  ],
  'paused': [
    'Пауза — ИИ не видит',
    'Тыныгуу — ИИ көрбөйт',
    "Pauza — SI ko'rmayapti",
    'Paused — AI cannot see',
    'Duraklatıldı — YZ görmüyor',
  ],
  'pausedSilence': [
    'Долго тихо — камера на паузе. Скажите что-нибудь, и ИИ снова увидит',
    'Көпкө чейин унчукпай калдык — камера тыныгууда. Бир нерсе айтыңыз, ИИ кайра көрөт',
    "Uzoq jimlik — kamera pauzada. Biror narsa ayting, SI yana ko'radi",
    'Quiet for a long time — the camera is paused. Say something and the AI will see again',
    'Uzun süre sessizlik — kamera duraklatıldı. Bir şey söyleyin, YZ yeniden görecek',
  ],
  'stopped': [
    'Камера выключена, чтобы беречь батарею. Нажмите «Камера», чтобы включить',
    'Батареяны үнөмдөө үчүн камера өчүрүлдү. Күйгүзүү үчүн «Камера» баскычын басыңыз',
    "Batareyani asrash uchun kamera o'chirildi. Yoqish uchun «Kamera»ni bosing",
    'The camera is off to save battery. Tap "Camera" to turn it on',
    'Pili korumak için kamera kapatıldı. Açmak için «Kamera»ya dokunun',
  ],
  'shooting': [
    'Снимаю чётко — держите ровно…',
    'Так тартып жатам — түз кармаңыз…',
    "Aniq suratga olyapman — tekis ushlang…",
    'Taking a sharp shot — hold steady…',
    'Net çekiyorum — sabit tutun…',
  ],
};

/// Текст по ключу на языке [lang] (ru, ky, uz, en, tr; нет перевода — русский).
String videoMsg(String key, String lang) {
  final m = _vm[key];
  if (m == null) return key;
  final i = switch (lang) {
    'ky' => 1,
    'uz' => 2,
    'en' => 3,
    'tr' => 4,
    _ => 0,
  };
  return m[i];
}

/// Текст для состояния камеры; null — показывать нечего.
String? videoStateText(VideoState s, String lang) {
  switch (s.phase) {
    case VideoPhase.error:
      return videoMsg(s.errorKey ?? 'cameraBusy', lang);
    case VideoPhase.pausedSilence:
      return videoMsg('pausedSilence', lang);
    case VideoPhase.pausedSystem:
      return videoMsg('paused', lang);
    case VideoPhase.stoppedIdle:
      return videoMsg('stopped', lang);
    case VideoPhase.shooting:
      return videoMsg('shooting', lang);
    default:
      return null;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Склейка: камера + политика + LiveCall
// ---------------------------------------------------------------------------------------------------------------------

/// Видео одного звонка. Создаётся рядом с [LiveCall] (`video: true`), камера включается [start], после конца звонка
/// [dispose]. Всё время держит камеру только через [LiveFrames] (CameraGate); сам кадры нигде не пишет.
class LiveVideo {
  final LiveCall call;
  final LiveFrames frames;
  final VideoConfig config;
  final FramePolicy policy;

  /// «Экономия»: 1 кадр в 3 с и только пока человек говорит. Меняется на лету.
  bool economy;

  final state = ValueNotifier<VideoState>(const VideoState(VideoPhase.off));

  /// Когда последний кадр ушёл в Google (для «ИИ видит»).
  final lastSentAt = ValueNotifier<DateTime?>(null);
  CameraLensDirection lens = CameraLensDirection.back;
  bool torch = false;

  LiveVideo({required this.call, required this.frames, this.config = const VideoConfig(), this.economy = false})
    : policy = FramePolicy(config) {
    call.voiceStart.addListener(_onVoiceStart);
    call.videoActive.addListener(_onVideoActive);
    call.state.addListener(_onCallState);
  }

  Timer? _timer;
  bool _cameraOn = false;
  bool _starting = false;
  bool _grabbing = false;
  bool _stillBusy = false;
  bool _switching = false;
  bool _resumeAfterInactive = false;
  bool _disposed = false;
  int _gen = 0;
  int _fails = 0;
  DateTime? _lastAttempt;
  DateTime? _speechStartedAt;
  DateTime _activeSince = DateTime.now();

  bool get cameraOn => _cameraOn;

  /// «ИИ видит»: камера в рабочем состоянии и кадр ушёл недавно.
  bool get aiSees {
    final t = lastSentAt.value;
    return state.value.phase == VideoPhase.live && t != null && DateTime.now().difference(t) <= config.seesWindow;
  }

  bool get canSwitch => frames.hasFront && frames.hasBack;

  void _set(VideoPhase p, [String? errorKey]) {
    if (_disposed) return;
    final s = VideoState(p, errorKey);
    if (state.value != s) state.value = s;
  }

  void _onVoiceStart() => _speechStartedAt = DateTime.now();

  void _onVideoActive() {
    if (call.videoActive.value || _disposed) return;
    // Модель видео не приняла: камера выключается, разговор идёт голосом.
    unawaited(_halt(VideoPhase.error, 'videoRejected'));
  }

  void _onCallState() {
    if (call.state.value != CallState.ended) return;
    unawaited(_halt(call.quotaExceeded ? VideoPhase.error : VideoPhase.off, call.quotaExceeded ? 'quota' : null));
  }

  /// Включить камеру. false — не вышло (причина в [state]: cameraDenied / cameraBusy / noCamera / videoRejected).
  Future<bool> start({CameraLensDirection lensDirection = CameraLensDirection.back}) async {
    if (_disposed || call.ended) return false;
    if (!call.videoActive.value) {
      _set(VideoPhase.error, 'videoRejected');
      return false;
    }
    if (_cameraOn) return true;
    if (_starting) return false;
    _starting = true;
    _set(VideoPhase.starting);
    final gen = ++_gen;
    try {
      await frames.start(lens: lensDirection);
    } on FrameException catch (e) {
      _starting = false;
      if (gen == _gen) _set(VideoPhase.error, _keyOf(e.kind));
      return false;
    } catch (_) {
      _starting = false;
      if (gen == _gen) _set(VideoPhase.error, 'cameraBusy');
      return false;
    }
    _starting = false;
    if (gen != _gen || _disposed || call.ended) {
      // Выключили или звонок кончился, пока камера открывалась.
      await frames.stop();
      return false;
    }
    lens = lensDirection;
    torch = false;
    _cameraOn = true;
    _fails = 0;
    _lastAttempt = null;
    lastSentAt.value = null;
    _speechStartedAt = null;
    _activeSince = DateTime.now();
    _set(VideoPhase.live);
    _timer?.cancel();
    _timer = Timer.periodic(config.tick, (_) => _tick());
    return true;
  }

  static String _keyOf(FrameFail k) => switch (k) {
    FrameFail.denied => 'cameraDenied',
    FrameFail.noCamera => 'noCamera',
    FrameFail.shot => 'shotFailed',
    FrameFail.busy => 'cameraBusy',
  };

  /// Выключить камеру (кнопка «Камера выкл.»): камера отпущена сразу.
  Future<void> stop() => _halt(VideoPhase.off);

  Future<void> _halt(VideoPhase phase, [String? errorKey]) async {
    _gen++; // отменяет открывающуюся камеру и кадр, который ещё снимается
    _timer?.cancel();
    _timer = null;
    final was = _cameraOn || _starting;
    _cameraOn = false;
    _starting = false;
    _resumeAfterInactive = phase == VideoPhase.pausedSystem;
    _set(phase, errorKey);
    if (was || frames.isOpen) {
      try {
        await frames.stop();
      } catch (_) {}
    }
    lastSentAt.value = null;
  }

  FrameCtx _ctx(DateTime now) => FrameCtx(
    now: now,
    call: call.state.value,
    cameraOn: _cameraOn,
    micMuted: call.muted,
    busy: _grabbing || _stillBusy || _switching,
    economy: economy,
    lastSent: _lastAttempt,
    lastUserVoice: call.lastVoiceAt,
    lastAiSound: call.aiSoundUntil,
    activeSince: _activeSince,
    speechStartedAt: _speechStartedAt,
  );

  void _tick() {
    if (!_cameraOn || _disposed) return;
    final cur = state.value.phase;
    if (cur != VideoPhase.live && cur != VideoPhase.pausedSilence) return; // снимок/смена камеры: не вмешиваемся
    switch (policy.decide(_ctx(DateTime.now()))) {
      case FrameDecision.send:
        _set(VideoPhase.live);
        unawaited(_sendOne());
      case FrameDecision.wait:
        if (cur == VideoPhase.pausedSilence) _set(VideoPhase.live);
      case FrameDecision.pauseSilence:
        _set(VideoPhase.pausedSilence);
      case FrameDecision.stopCamera:
        unawaited(_halt(VideoPhase.stoppedIdle));
    }
  }

  Future<void> _sendOne() async {
    if (_grabbing) return;
    _grabbing = true;
    _lastAttempt = DateTime.now(); // и при неудаче: не долбим камеру 4 раза в секунду
    final gen = _gen;
    try {
      final bytes = await frames.grab();
      if (gen != _gen || !_cameraOn || bytes == null) return;
      _fails = 0;
      if (call.sendFrame(bytes)) lastSentAt.value = DateTime.now();
    } on FrameException {
      if (gen != _gen) return;
      _fails++;
      if (_fails >= config.maxShotFailures) {
        unawaited(_halt(VideoPhase.error, 'shotFailed'));
      }
    } finally {
      _grabbing = false;
    }
  }

  /// Состояние приложения. inactive (шторка, системное окно): камера отпускается, звонок идёт, на resumed камера возвращается.
  /// paused/hidden: камера выключена совсем (сам звонок завершает экран, как раньше).
  Future<void> onLifecycle(AppLifecycleState s) async {
    switch (s) {
      case AppLifecycleState.inactive:
        // Окно «Разрешить камеру» тоже даёт inactive: пока камера открывается, её не трогаем (как в LiveShotPage).
        if (_cameraOn && !_starting && !call.ended) await _halt(VideoPhase.pausedSystem);
      case AppLifecycleState.resumed:
        if (_resumeAfterInactive && !_cameraOn && !_starting && !call.ended && !_disposed) {
          _resumeAfterInactive = false;
          await start(lensDirection: lens);
        }
      case AppLifecycleState.paused:
      case AppLifecycleState.hidden:
      case AppLifecycleState.detached:
        _resumeAfterInactive = false;
        if (_cameraOn || _starting || state.value.phase == VideoPhase.pausedSystem) await _halt(VideoPhase.off);
    }
  }

  /// Другая камера (фронтальная ↔ задняя). Кадры на паузе; не вышло — вернуть прежнюю. false — не вышло или не те условия.
  Future<bool> switchCamera() async {
    if (!_cameraOn || _switching || _stillBusy || !canSwitch) return false;
    final to = lens == CameraLensDirection.back ? CameraLensDirection.front : CameraLensDirection.back;
    _switching = true;
    final gen = _gen;
    _set(VideoPhase.switching);
    try {
      await _waitGrab();
      try {
        await frames.start(lens: to);
        if (gen != _gen || _disposed) {
          await frames.stop();
          return false;
        }
        lens = to;
        torch = false; // фонарик только у задней, при смене — гаснет
        _set(VideoPhase.live);
        return true;
      } on FrameException catch (e) {
        if (gen != _gen) return false;
        try {
          await frames.start(lens: lens); // прежняя камера возвращается
          _set(VideoPhase.live);
          _toastKey = _keyOf(e.kind);
        } catch (_) {
          unawaited(_halt(VideoPhase.error, _keyOf(e.kind)));
        }
        return false;
      }
    } finally {
      _switching = false;
    }
  }

  /// Ключ ошибки последней неудачной смены камеры (камера осталась прежней) — экран покажет тост и сбросит.
  String? _toastKey;
  String? takeToastKey() {
    final k = _toastKey;
    _toastKey = null;
    return k;
  }

  /// Фонарик (только задняя камера). Возвращает новое состояние.
  Future<bool> setTorch(bool on) async {
    if (!_cameraOn || lens != CameraLensDirection.back) return false;
    await frames.setTorch(on);
    torch = on;
    return torch;
  }

  /// Чёткий снимок. Кадры на паузе, на время переоткрытия камеры экран показывает [LiveFrames.lastFrame]. Нет снимка за
  /// [VideoConfig.stillTimeout] — [VideoStill.errorKey]=stillFailed (кнопка «Повторить»). Файл удалён к возврату.
  Future<VideoStill> takeStill(ResolutionPreset preset) async {
    if (!_cameraOn || _stillBusy || _switching) return const VideoStill(errorKey: 'cameraBusy');
    _stillBusy = true;
    final gen = _gen;
    _set(VideoPhase.shooting);
    try {
      await _waitGrab();
      final bytes = await frames.still(preset).timeout(config.stillTimeout);
      if (bytes == null) return const VideoStill(errorKey: 'stillFailed');
      return VideoStill(bytes: bytes, barcode: await frames.barcodeOfLastStill());
    } on TimeoutException {
      return const VideoStill(errorKey: 'stillFailed');
    } on FrameException catch (e) {
      return VideoStill(errorKey: e.kind == FrameFail.shot ? 'stillFailed' : _keyOf(e.kind));
    } catch (_) {
      return const VideoStill(errorKey: 'stillFailed');
    } finally {
      _stillBusy = false;
      if (gen == _gen && !_disposed && _cameraOn) {
        if (frames.isOpen) {
          _lastAttempt = null;
          _set(VideoPhase.live);
        } else {
          // чёткий снимок забрал камеру и вернуть не вышло
          unawaited(_halt(VideoPhase.error, 'cameraBusy'));
        }
      }
    }
  }

  Future<void> _waitGrab() async {
    final end = DateTime.now().add(config.stillGrabWait);
    while (_grabbing && DateTime.now().isBefore(end)) {
      await Future<void>.delayed(const Duration(milliseconds: 20));
    }
  }

  /// Конец звонка/экрана: камера отпущена, остатки файлов убраны, слушатели сняты.
  Future<void> dispose() async {
    if (_disposed) return;
    await _halt(VideoPhase.off);
    _disposed = true;
    call.voiceStart.removeListener(_onVoiceStart);
    call.videoActive.removeListener(_onVideoActive);
    call.state.removeListener(_onCallState);
    try {
      await frames.purge();
    } catch (_) {}
  }
}
