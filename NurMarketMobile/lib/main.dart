import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app.dart';
import 'core/catalog/global_products.dart';
import 'core/db/database.dart';
import 'core/providers.dart';
import 'core/util/audit.dart';
import 'core/util/photo_cache.dart';

/// NurMarket на телефоне (Flutter). ТЗ: docs/ТЗ для мобильного приложения (Flutter).md.
Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // 2026-10-29 (аудит мобилки, п. 3.9): падения нигде не собирались — владелец о них не знал. Теперь ошибка Flutter и
  // необработанная ошибка Dart пишутся в журнал приложения («Журнал действий», вид «Сбой»); приложение продолжает работать.
  final defaultOnError = FlutterError.onError;
  FlutterError.onError = (details) {
    Audit.log('error', _short('${details.exceptionAsString()} · ${details.stack?.toString().split('\n').take(3).join(' | ') ?? ''}'));
    defaultOnError?.call(details);
  };
  PlatformDispatcher.instance.onError = (error, stack) {
    Audit.log('error', _short('$error · ${stack.toString().split('\n').take(3).join(' | ')}'));
    return true;
  };
  // 2026-10-30 (оптимизация): кэш декодированных картинок в памяти 60 МБ вместо 100 МБ по умолчанию — на телефонах с 2–3 ГБ ОЗУ
  PaintingBinding.instance.imageCache.maximumSizeBytes = 60 << 20;
  final db = AppDatabase();
  final container = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db)]);
  // Язык и вход читаются параллельно — заставка висит меньше (13.10, «открытие очень долгое»).
  final (_, signedIn) = await (container.read(prefsProvider.notifier).load(), container.read(sessionProvider.notifier).restore()).wait;
  // 2026-10-27: справочник готовых наименований NurCRM открывается заранее, в фоне (первый запуск копирует его из assets)
  container.read(globalProductsProvider).warmUp();
  // 2026-10-30 (оптимизация, п. 12): дисковый кэш фото не больше 200 МБ — лишнее удаляется в фоне (не чаще раза в 6 часов)
  PhotoCache.trim();
  runApp(
    UncontrolledProviderScope(
      container: container,
      child: NurMarketApp(signedIn: signedIn),
    ),
  );
}

/// Запись о сбое в журнале — не длиннее 400 знаков (стек обрезается).
String _short(String s) => s.length <= 400 ? s : '${s.substring(0, 400)}…';
