import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';

import '../providers.dart';
import '../tester/tester_mode.dart' show AppBuild, TesterGate;

/// 2026-11-01, владелец: «добавь в мобилку обновление через GitHub». Выпуски мобилки лежат в публичном репозитории
/// beknazar93/Nurmarket-Mobile (тег `mobile-X.Y.Z`, один универсальный APK). Приложение раз в сутки смотрит, нет ли версии
/// новее, показывает «Доступна версия X», скачивает APK в кэш и открывает системную установку поверх (ключ подписи тот же —
/// данные и вход сохраняются). Образец — AndroidApkUpdateService.cs в кассе. Только Android; любые сбои сети — тихо.
class AppRelease {
  final String version; // '1.18.14'
  final String apkUrl;
  final String apkName;
  final int size; // байт, 0 — GitHub не сообщил
  final String notes;
  final bool prerelease;

  /// 2026-11-01 (контрольный аудит, М2-08): `digest` ассета GitHub, вид `sha256:<hex>`; пусто — GitHub не прислал (проверяется только размер).
  final String digest;
  const AppRelease({required this.version, required this.apkUrl, required this.apkName, this.size = 0, this.notes = '', this.prerelease = false, this.digest = ''});

  Map<String, dynamic> toJson() => {'v': version, 'u': apkUrl, 'n': apkName, 's': size, 'b': notes, 'p': prerelease, 'd': digest};

  static AppRelease? fromJson(Object? m) {
    if (m is! Map) return null;
    final v = '${m['v'] ?? ''}', u = '${m['u'] ?? ''}';
    if (UpdateRules.parseVersion(v) == null || !UpdateRules.trustedApkUrl(u)) return null;
    return AppRelease(version: v, apkUrl: u, apkName: '${m['n'] ?? 'update.apk'}', size: (m['s'] as num?)?.toInt() ?? 0, notes: '${m['b'] ?? ''}', prerelease: m['p'] == true, digest: '${m['d'] ?? ''}');
  }
}

/// Чистые правила (разбор тега, сравнение версий, выбор выпуска) — без сети, их проверяют тесты.
class UpdateRules {
  static const repo = 'beknazar93/Nurmarket-Mobile';
  static const releasesUrl = 'https://api.github.com/repos/$repo/releases?per_page=20';

  /// Ограничение «Что нового»: длинное описание обрезается.
  static const maxNotes = 1500;

  /// Минимальный размер настоящего APK (1 МБ): пустой файл или страница ошибки не пойдут в установку.
  static const minApkBytes = 1024 * 1024;

  /// `mobile-1.18.14`, `v1.18.14`, `1.18.14` → [1, 18, 14]. Тестовые хвосты (`1.18.14-test1`) и мусор → null.
  static List<int>? parseVersion(String tag) {
    var t = tag.trim();
    if (t.toLowerCase().startsWith('mobile-')) {
      t = t.substring(7);
    } else if (t.startsWith('v') || t.startsWith('V')) {
      t = t.substring(1);
    }
    final m = RegExp(r'^(\d+)\.(\d+)(?:\.(\d+))?$').firstMatch(t);
    if (m == null) return null;
    return [int.parse(m[1]!), int.parse(m[2]!), int.parse(m[3] ?? '0')];
  }

  static int compare(List<int> a, List<int> b) {
    for (var i = 0; i < 3; i++) {
      final c = a[i].compareTo(b[i]);
      if (c != 0) return c;
    }
    return 0;
  }

  /// [version] новее [current] (оба — «X.Y.Z»).
  static bool isNewer(String version, String current) {
    final a = parseVersion(version), b = parseVersion(current);
    return a != null && b != null && compare(a, b) > 0;
  }

  /// APK берём только из нашего репозитория на GitHub по https.
  /// 2026-11-02 (аудит №2, М3-07): ссылка разбирается как Uri (схема, хост, первые сегменты пути), а не сверяется префиксом строки;
  /// сегменты `..` и пустые отсекаются.
  static bool trustedApkUrl(String url) => _trustedAsset(url, '.apk');

  /// Файл-«стоп» с отозванными версиями в репозитории выпусков (в ассетах любого выпуска): `{"blocked": ["1.18.15", …]}` или список строк.
  static const blockedAssetName = 'blocked-versions.json';

  static bool _trustedAsset(String url, String ext) {
    final u = Uri.tryParse(url);
    if (u == null || u.scheme != 'https' || u.host != 'github.com' || !u.path.toLowerCase().endsWith(ext)) return false;
    final parts = repo.split('/');
    final s = u.pathSegments;
    if (s.length < 5 || s[0] != parts[0] || s[1] != parts[1] || s[2] != 'releases') return false;
    return !s.any((x) => x.isEmpty || x == '.' || x == '..');
  }

  /// Адрес файла `blocked-versions.json` из списка выпусков (первый найденный, свежие выпуски идут первыми); null — файла нет.
  static String? blockedAssetUrl(Object? json) {
    if (json is! List) return null;
    for (final r in json) {
      final assets = r is Map ? r['assets'] : null;
      if (assets is! List) continue;
      for (final a in assets) {
        if (a is Map && '${a['name'] ?? ''}'.toLowerCase() == blockedAssetName && _trustedAsset('${a['browser_download_url'] ?? ''}', '.json')) {
          return '${a['browser_download_url']}';
        }
      }
    }
    return null;
  }

  /// Содержимое `blocked-versions.json` → множество версий вида `1.18.15` (теги `mobile-1.18.15` приводятся к версии); мусор → пусто.
  static Set<String> parseBlocked(String? text) {
    try {
      final data = jsonDecode(text ?? '');
      final list = data is Map ? data['blocked'] : data;
      if (list is! List) return {};
      return {
        for (final x in list)
          if (parseVersion('$x') != null) parseVersion('$x')!.join('.'),
      };
    } catch (_) {
      return {};
    }
  }

  /// Выпуск помечен отозванным в описании: `[blocked]`, `[revoked]` или `[отозван]` (без учёта регистра).
  static bool revokedInNotes(String body) {
    final t = body.toLowerCase();
    return t.contains('[blocked]') || t.contains('[revoked]') || t.contains('[отозван]');
  }

  /// `sha256:ABCD…` → строчный hex из 64 знаков; другое (пусто, иной алгоритм, мусор) → null.
  static String? digestHex(String digest) {
    final m = RegExp(r'^sha256:([0-9a-fA-F]{64})$').firstMatch(digest.trim());
    return m?.group(1)?.toLowerCase();
  }

  /// Самый новый выпуск по версии из тега, у которого есть APK.
  /// 2026-11-02 (аудит №2, М3-07): обычным пользователям — ТОЛЬКО стабильные выпуски ([allowPrerelease] = false); тестовые (prerelease) —
  /// в режиме тестировщика. Отозванные версии ([blocked] или пометка в описании) не предлагаются: берётся следующий разрешённый выпуск.
  static AppRelease? pickLatest(Object? json, {bool allowPrerelease = false, Set<String> blocked = const {}}) {
    if (json is! List) return null;
    AppRelease? best;
    List<int>? bestV;
    for (final r in json) {
      if (r is! Map || r['draft'] == true) continue;
      final v = parseVersion('${r['tag_name'] ?? ''}');
      if (v == null) continue;
      if (r['prerelease'] == true && !allowPrerelease) continue;
      if (blocked.contains(v.join('.')) || revokedInNotes('${r['body'] ?? ''}')) continue;
      Map? apk;
      final assets = r['assets'];
      if (assets is List) {
        for (final a in assets) {
          if (a is Map && '${a['name'] ?? ''}'.toLowerCase().endsWith('.apk') && trustedApkUrl('${a['browser_download_url'] ?? ''}')) {
            apk = a;
            break;
          }
        }
      }
      if (apk == null) continue;
      if (bestV != null && compare(v, bestV) <= 0) continue;
      var notes = '${r['body'] ?? ''}'.trim();
      if (notes.length > maxNotes) notes = '${notes.substring(0, maxNotes).trimRight()}…';
      bestV = v;
      best = AppRelease(
        version: v.join('.'),
        apkUrl: '${apk['browser_download_url']}',
        apkName: '${apk['name']}',
        size: (apk['size'] as num?)?.toInt() ?? 0,
        notes: notes,
        prerelease: r['prerelease'] == true,
        digest: '${apk['digest'] ?? ''}',
      );
    }
    return best;
  }
}

enum UpdateCheck { found, upToDate, failed }

enum UpdateStart { started, needPermission, failed, cancelled }

class UpdateState {
  /// Версия новее установленной (или null).
  final AppRelease? available;
  final bool checking;

  /// Ход скачивания 0..1; null — не скачиваем.
  final double? progress;

  /// «Позже»: баннер по этой версии скрыт до [snoozeUntil].
  final String? snoozeVersion;
  final DateTime? snoozeUntil;
  const UpdateState({this.available, this.checking = false, this.progress, this.snoozeVersion, this.snoozeUntil});

  bool get snoozed => available != null && snoozeVersion == available!.version && snoozeUntil != null && DateTime.now().isBefore(snoozeUntil!);

  UpdateState copyWith({
    AppRelease? available,
    bool clearAvailable = false,
    bool? checking,
    double? progress,
    bool clearProgress = false,
    String? snoozeVersion,
    DateTime? snoozeUntil,
    bool clearSnooze = false,
  }) => UpdateState(
    available: clearAvailable ? null : (available ?? this.available),
    checking: checking ?? this.checking,
    progress: clearProgress ? null : (progress ?? this.progress),
    snoozeVersion: clearSnooze ? null : (snoozeVersion ?? this.snoozeVersion),
    snoozeUntil: clearSnooze ? null : (snoozeUntil ?? this.snoozeUntil),
  );
}

class AppUpdateController extends Notifier<UpdateState> {
  static const _channel = MethodChannel('kg.nurcrm.nurmarket/update');
  static const _kChecked = 'update.checkedAt';
  static const _kLatest = 'update.latest';
  static const _kSnooze = 'update.snooze';

  /// Только Android: на других системах (разработка на ПК, iOS) функция скрыта.
  static bool get supported => !kIsWeb && Platform.isAndroid;

  static String get currentVersion => AppBuild.version;

  CancelToken? _cancel;

  @override
  UpdateState build() {
    if (!supported) return const UpdateState();
    ref.onDispose(() => _cancel?.cancel());
    Future.microtask(_start);
    return const UpdateState();
  }

  Dio _dio({bool download = false}) => Dio(
    BaseOptions(
      connectTimeout: const Duration(seconds: 10),
      receiveTimeout: Duration(seconds: download ? 60 : 20),
      sendTimeout: const Duration(seconds: 10),
      headers: {'User-Agent': 'NurMarketMobile', if (!download) 'Accept': 'application/vnd.github+json'},
    ),
  );

  Future<Directory> _dir() async {
    final d = Directory('${(await getTemporaryDirectory()).path}${Platform.pathSeparator}updates');
    if (!d.existsSync()) d.createSync(recursive: true);
    return d;
  }

  /// Запуск: убрать старые APK, поднять сохранённое «есть новая версия», раз в сутки спросить GitHub.
  Future<void> _start() async {
    try {
      await _cleanup();
      final db = ref.read(databaseProvider);
      final latest = AppRelease.fromJson(_decode(await db.getSetting(_kLatest)));
      final snooze = (await db.getSetting(_kSnooze) ?? '').split('|');
      var next = state;
      // 2026-11-02 (М3-07): сохранённый тестовый выпуск обычному пользователю не показываем
      final okForMe = latest != null && (!latest.prerelease || await _testerMode());
      if (latest != null && okForMe && UpdateRules.isNewer(latest.version, currentVersion)) next = next.copyWith(available: latest);
      if (snooze.length == 2) next = next.copyWith(snoozeVersion: snooze[0], snoozeUntil: DateTime.tryParse(snooze[1]));
      state = next;
      final at = DateTime.tryParse(await db.getSetting(_kChecked) ?? '');
      if (at == null || DateTime.now().difference(at).abs() > const Duration(hours: 24)) await check();
    } catch (_) {
      // обновление — не главное: любые сбои без шума
    }
  }

  static Object? _decode(String? s) {
    try {
      return s == null || s.isEmpty ? null : jsonDecode(s);
    } catch (_) {
      return null;
    }
  }

  /// Удалить недокачанное и APK уже установленных версий (после обновления файл больше не нужен).
  Future<void> _cleanup() async {
    try {
      final d = await _dir();
      for (final f in d.listSync().whereType<File>()) {
        final name = f.uri.pathSegments.last;
        final m = RegExp(r'(\d+\.\d+\.\d+)').firstMatch(name);
        final old = !name.toLowerCase().endsWith('.apk') || m == null || !UpdateRules.isNewer(m[1]!, currentVersion);
        if (old) f.deleteSync();
      }
    } catch (_) {}
  }

  /// Спросить GitHub. [manual] — кнопка «Проверить обновления»: «Позже» сбрасывается. Автопроверка при сбое молчит.
  Future<UpdateCheck> check({bool manual = false}) async {
    if (!supported || state.checking) return UpdateCheck.failed;
    state = state.copyWith(checking: true);
    try {
      final resp = await _dio().get<String>(UpdateRules.releasesUrl, options: Options(responseType: ResponseType.plain)).timeout(const Duration(seconds: 30));
      final db = ref.read(databaseProvider);
      final releases = jsonDecode(resp.data ?? '[]');
      final latest = UpdateRules.pickLatest(releases, allowPrerelease: await _testerMode(), blocked: await _blockedFrom(releases));
      final newer = latest != null && UpdateRules.isNewer(latest.version, currentVersion);
      await db.setSetting(_kChecked, DateTime.now().toIso8601String());
      await db.setSetting(_kLatest, newer ? jsonEncode(latest.toJson()) : '');
      if (manual) await db.setSetting(_kSnooze, '');
      state = state.copyWith(checking: false, available: newer ? latest : null, clearAvailable: !newer, clearSnooze: manual);
      return newer ? UpdateCheck.found : UpdateCheck.upToDate;
    } catch (_) {
      state = state.copyWith(checking: false);
      return UpdateCheck.failed;
    }
  }

  /// 2026-11-02 (М3-07): режим тестировщика (тестовая сборка или верный код) — только ему предлагаются тестовые (prerelease) выпуски.
  Future<bool> _testerMode() async {
    try {
      return await TesterGate.isAllowed(ref.read(databaseProvider));
    } catch (_) {
      return false;
    }
  }

  /// Отозванные версии из `blocked-versions.json` (ассет любого выпуска); нет файла или сбой сети — пусто (стоп-кран не мешает обновлению).
  Future<Set<String>> _blockedFrom(Object? releases) async {
    final url = UpdateRules.blockedAssetUrl(releases);
    if (url == null) return {};
    try {
      final r = await _dio().get<String>(url, options: Options(responseType: ResponseType.plain)).timeout(const Duration(seconds: 20));
      return UpdateRules.parseBlocked(r.data);
    } catch (_) {
      return {};
    }
  }

  /// «Позже»: не напоминать об этой версии сутки.
  Future<void> snooze() async {
    final v = state.available?.version;
    if (v == null) return;
    final until = DateTime.now().add(const Duration(hours: 24));
    state = state.copyWith(snoozeVersion: v, snoozeUntil: until);
    try {
      await ref.read(databaseProvider).setSetting(_kSnooze, '$v|${until.toIso8601String()}');
    } catch (_) {}
  }

  Future<bool> canInstall() async {
    try {
      return await _channel.invokeMethod<bool>('canInstall') ?? false;
    } catch (_) {
      return false;
    }
  }

  Future<void> openInstallSettings() async {
    try {
      await _channel.invokeMethod<void>('openInstallSettings');
    } catch (_) {}
  }

  void cancelDownload() => _cancel?.cancel();

  /// 2026-11-01 (М2-08): похоже, что телефон не в Wi‑Fi (мобильная сеть) — перед скачиванием ~117 МБ спросить. Без новых пакетов: по сетевым
  /// интерфейсам (`wlan*`/`eth*` с рабочим адресом = Wi‑Fi или кабель); не удалось определить — считаем, что Wi‑Fi (не мешаем).
  Future<bool> onMobileData() async {
    try {
      final ifs = await NetworkInterface.list(includeLoopback: false, includeLinkLocal: false);
      if (ifs.isEmpty) return false;
      final wifi = ifs.any((i) => (i.name.startsWith('wlan') || i.name.startsWith('eth') || i.name.startsWith('en')) && i.addresses.isNotEmpty);
      final mobile = ifs.any((i) => i.name.startsWith('rmnet') || i.name.startsWith('ccmni') || i.name.startsWith('pdp') || i.name.startsWith('v4-rmnet'));
      return !wifi && mobile;
    } catch (_) {
      return false;
    }
  }

  /// «Обновить»: разрешение на установку → скачивание с ходом → системная установка. Установку подтверждает сам Android.
  Future<UpdateStart> start() async {
    final rel = state.available;
    if (!supported || rel == null || state.progress != null) return UpdateStart.failed;
    if (!await canInstall()) return UpdateStart.needPermission;
    // 2026-11-02 (М3-07): стоп-кран ещё раз прямо перед скачиванием — выпуск могли отозвать после проверки. Нет связи с GitHub — идём дальше.
    try {
      final resp = await _dio().get<String>(UpdateRules.releasesUrl, options: Options(responseType: ResponseType.plain)).timeout(const Duration(seconds: 20));
      final releases = jsonDecode(resp.data ?? '[]');
      final fresh = UpdateRules.pickLatest(releases, allowPrerelease: await _testerMode(), blocked: await _blockedFrom(releases));
      if (fresh == null || UpdateRules.isNewer(rel.version, fresh.version)) {
        state = state.copyWith(clearAvailable: true);
        try {
          await ref.read(databaseProvider).setSetting(_kLatest, '');
        } catch (_) {}
        return UpdateStart.failed;
      }
    } catch (_) {
      // проверка стоп-крана не удалась — решает само скачивание
    }
    state = state.copyWith(progress: 0);
    final cancel = _cancel = CancelToken();
    try {
      final dir = await _dir();
      for (final f in dir.listSync().whereType<File>()) {
        try {
          f.deleteSync();
        } catch (_) {}
      }
      // 2026-11-02 (М3-07): имя файла своё, из версии, а не из ассета
      final target = File('${dir.path}${Platform.pathSeparator}update-${rel.version}.apk');
      final part = '${target.path}.part';
      var shown = -1;
      await _dio(download: true).download(
        rel.apkUrl,
        part,
        cancelToken: cancel,
        deleteOnError: true,
        onReceiveProgress: (got, total) {
          final all = total > 0 ? total : rel.size;
          if (all <= 0) return;
          final pct = (got * 100 / all).floor().clamp(0, 100);
          if (pct == shown) return;
          shown = pct;
          state = state.copyWith(progress: pct / 100);
        },
      );
      final len = File(part).lengthSync();
      // 2026-11-01 (М2-08): размер сверяется ОБЯЗАТЕЛЬНО (GitHub его присылает); нет размера в ответе — файл не ставим
      if (len < UpdateRules.minApkBytes || rel.size <= 0 || len != rel.size || !target.path.toLowerCase().endsWith('.apk')) {
        File(part).deleteSync();
        throw StateError('bad apk size $len');
      }
      // и SHA-256, если GitHub прислал `digest` (sha256:<hex>)
      if (UpdateRules.digestHex(rel.digest) != null) {
        final got = (await sha256.bind(File(part).openRead()).first).toString();
        if (got != UpdateRules.digestHex(rel.digest)) {
          File(part).deleteSync();
          throw StateError('bad apk sha256');
        }
      }
      File(part).renameSync(target.path);
      state = state.copyWith(progress: 1);
      await _channel.invokeMethod<void>('install', {'path': target.path});
      state = state.copyWith(clearProgress: true);
      return UpdateStart.started;
    } on DioException catch (e) {
      state = state.copyWith(clearProgress: true);
      return CancelToken.isCancel(e) ? UpdateStart.cancelled : UpdateStart.failed;
    } catch (_) {
      state = state.copyWith(clearProgress: true);
      return UpdateStart.failed;
    } finally {
      _cancel = null;
    }
  }
}

final appUpdateProvider = NotifierProvider<AppUpdateController, UpdateState>(AppUpdateController.new);
