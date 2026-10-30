import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/admin/admin_receive.dart';
import '../../features/admin/analytics.dart';
import '../../features/admin/quotes_store.dart';
import '../../features/assistant/ai_log_screen.dart';
import '../../features/assistant/call_controller.dart';
import '../../features/sale/piece_sale_mode.dart';
import '../../features/staff/staff_providers.dart';
import '../ai/ai_access.dart';
import '../ai/vision.dart';
import '../providers.dart';
import '../tester/tester_store.dart';

/// 2026-10-29 (полная изоляция аккаунтов): вошёл другой аккаунт — провайдеры, которые держат данные в памяти, пересоздаются
/// (репозитории с кэшем, КП и счета, журнал и «отмена» действий ИИ, состояние тестировщика), а звонок с ИИ забывает реплики и
/// таблицы. Не трогаются: сессия, база, роутер, звонок как объект (к нему привязан роутер).
void resetAccountProviders(Ref ref) {
  ref.invalidate(catalogRepoProvider);
  ref.invalidate(shiftRepoProvider);
  ref.invalidate(salesRepoProvider);
  ref.invalidate(stockRepoProvider);
  ref.invalidate(opsRepoProvider);
  ref.invalidate(analyticsRepoProvider);
  ref.invalidate(adminReceiveRepoProvider);
  ref.invalidate(tasksRepoProvider);
  ref.invalidate(quotesStoreProvider);
  ref.invalidate(aiJournalProvider);
  ref.invalidate(journalUndoerProvider);
  ref.invalidate(testerStoreProvider);
  ref.invalidate(testerSyncProvider);
  ref.invalidate(aiAccessRepoProvider);
  ref.invalidate(aiPolicyProvider);
  ref.invalidate(visionProvider);
  ref.invalidate(pieceSaleProvider);
  ref.read(callControllerProvider).resetForAccountSwitch();
}
