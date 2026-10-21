import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app.dart';
import 'core/db/database.dart';
import 'core/providers.dart';

/// NurMarket на телефоне (Flutter). ТЗ: docs/ТЗ для мобильного приложения (Flutter).md.
Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final db = AppDatabase();
  final container = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db)]);
  // Язык и вход читаются параллельно — заставка висит меньше (13.10, «открытие очень долгое»).
  final (_, signedIn) = await (container.read(prefsProvider.notifier).load(), container.read(sessionProvider.notifier).restore()).wait;
  runApp(
    UncontrolledProviderScope(
      container: container,
      child: NurMarketApp(signedIn: signedIn),
    ),
  );
}
