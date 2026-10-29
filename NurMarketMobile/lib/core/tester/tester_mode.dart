import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../db/database.dart';
import '../providers.dart';
import '../util/ui.dart';

/// 2026-10-27, владелец: «Логотип NurCRM — пять тапов, чтобы открылся режим тестировщика». Версия сборки и признак «тестовая».
/// ВАЖНО при выпуске: [version] и [build] должны совпадать с `version:` в pubspec.yaml (это проверяет тест
/// tester_mode_test.dart), а [isTestBuild] при обычном (не тестовом) выпуске ставится в false — тогда режим открывается
/// только по коду тестировщика (долгое нажатие на логотип).
class AppBuild {
  static const version = '1.18.8';
  static const build = 9;

  /// Тестовая сборка (prerelease на GitHub): режим тестировщика доступен всем, у кого она стоит.
  static const isTestBuild = true;
}

/// Кому открыт режим тестировщика: тестовая сборка, ИЛИ верный код тестировщика, ИЛИ отладочная сборка (flutter run).
/// Код — тот же, что у канала обновлений кассы (UpdateChannel.cs): в исходниках только SHA-256, регистр не важен.
class TesterGate {
  static const codeSetting = 'tester.code';

  /// SHA-256 кодов тестировщика (UpdateChannel.TesterCodeSha256 и TesterCodeSha256V2 кассы). Сами коды здесь не хранятся.
  static const codeHashes = {
    'e1c603f329be5e74dd11488cbc8570acd6cebaf7b6241964b05b69232f20b18e',
    'f1f5c106873e707eb4f2b7d44529410e2250e9de207c473de9e3df444a4f36f4',
  };

  static String normalize(String? code) => (code ?? '').trim().toUpperCase();

  /// [hashes] — подмена набора хэшей в тестах (настоящий код в тестах не хранится).
  static bool isValidCode(String? code, {Set<String>? hashes}) {
    final c = normalize(code);
    if (c.isEmpty) return false;
    return (hashes ?? codeHashes).contains(sha256.convert(utf8.encode(c)).toString());
  }

  /// [debugMode] и [testBuild] подменяются в тестах (в `flutter test` kDebugMode всегда true).
  static Future<bool> isAllowed(AppDatabase db, {bool debugMode = kDebugMode, bool testBuild = AppBuild.isTestBuild, Set<String>? hashes}) async {
    if (debugMode || testBuild) return true;
    try {
      return isValidCode(await db.getSetting(codeSetting), hashes: hashes);
    } catch (_) {
      return false;
    }
  }

  /// Ввести код. true — код верный и запомнен; false — ничего не меняется.
  static Future<bool> activate(AppDatabase db, String code, {Set<String>? hashes}) async {
    if (!isValidCode(code, hashes: hashes)) return false;
    try {
      await db.setSetting(codeSetting, normalize(code));
      return true;
    } catch (_) {
      return false;
    }
  }
}

/// Нажатия на логотип: [needed] быстрых нажатий в пределах [window] — вход в режим тестировщика (чистая логика, без виджета).
class TapCounter {
  TapCounter({this.needed = 5, this.window = const Duration(seconds: 2)});
  final int needed;
  final Duration window;
  final List<DateTime> _taps = [];

  /// Учесть нажатие в момент [now]. Возвращает сколько нажатий в серии (после [needed] серия сбрасывается).
  int tap(DateTime now) {
    _taps.add(now);
    while (_taps.isNotEmpty && now.difference(_taps.first) > window) {
      _taps.removeAt(0);
    }
    if (_taps.length >= needed) {
      _taps.clear();
      return needed;
    }
    return _taps.length;
  }

  void reset() => _taps.clear();
}

/// Оборачивает логотип: 5 быстрых нажатий открывают «Режим тестировщика» (если он доступен), долгое нажатие — ввод кода.
/// Если режим недоступен — на нажатия ничего не происходит (ни вибрации, ни подсказок: наличие режима не раскрывается).
class TesterTap extends ConsumerStatefulWidget {
  const TesterTap({super.key, required this.child, this.clock, this.onOpen, this.debugMode, this.testBuild, this.hashes});
  final Widget child;

  /// Часы и переход — для тестов.
  final DateTime Function()? clock;
  final void Function(BuildContext context)? onOpen;
  final bool? debugMode;
  final bool? testBuild;
  final Set<String>? hashes;

  @override
  ConsumerState<TesterTap> createState() => _TesterTapState();
}

class _TesterTapState extends ConsumerState<TesterTap> {
  final _counter = TapCounter();
  bool? _allowed;

  Future<bool> _isAllowed() async {
    final db = ref.read(databaseProvider);
    return _allowed = await TesterGate.isAllowed(
      db,
      debugMode: widget.debugMode ?? kDebugMode,
      testBuild: widget.testBuild ?? AppBuild.isTestBuild,
      hashes: widget.hashes,
    );
  }

  Future<void> _onTap() async {
    final n = _counter.tap((widget.clock ?? DateTime.now)());
    if (n < 3) return;
    final ok = _allowed ?? await _isAllowed();
    if (!ok || !mounted) return;
    if (n < _counter.needed) {
      if (n == 3) toast(context, context.l.tsTapMore(_counter.needed - n));
      return;
    }
    // 5-е нажатие: лёгкая вибрация и переход
    try {
      await HapticFeedback.lightImpact();
    } catch (_) {
      // вибрации может не быть — не страшно
    }
    if (!mounted) return;
    (widget.onOpen ?? (c) => c.push('/tester'))(context);
  }

  Future<void> _onLongPress() async {
    // Ввод кода тестировщика (для обычных, не тестовых сборок). Если режим и так открыт — ничего не делаем.
    final ok = await _isAllowed();
    if (ok || !mounted) return;
    final db = ref.read(databaseProvider);
    final code = TextEditingController();
    final l = context.l;
    final entered = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.tsCodeTitle),
        content: TextField(
          controller: code,
          autofocus: true,
          textCapitalization: TextCapitalization.characters,
          decoration: InputDecoration(hintText: l.tsCodeHint),
          onSubmitted: (v) => Navigator.pop(c, v),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: Text(l.cancel)),
          TextButton(onPressed: () => Navigator.pop(c, code.text), child: Text(l.done)),
        ],
      ),
    );
    if (entered == null || !mounted) return;
    final good = await TesterGate.activate(db, entered, hashes: widget.hashes);
    if (!mounted) return;
    _allowed = null;
    toast(context, good ? l.tsCodeOn : l.tsCodeBad, error: !good);
  }

  @override
  Widget build(BuildContext context) =>
      GestureDetector(behavior: HitTestBehavior.opaque, excludeFromSemantics: true, onTap: _onTap, onLongPress: _onLongPress, child: widget.child);
}
