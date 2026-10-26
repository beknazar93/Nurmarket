// 2026-10-26 (голос-управление, В3б): открытие экранов с «ручками» (раздел 3.5 ТЗ) — `opener` для `RepoActionHost.fromReader`.
// Инструмент (checkout, return_sale, receive_from_supplier, close_shift, create_product) просит `openScreenFor(ключ, аргументы)`;
// здесь ключ ручки превращается в маршрут. Экран уже открыт — берём его же (ручка заполнит поля заново), иначе открываем поверх
// текущего. Возврат: чек ищется по номеру (или по id продажи — «отмена оплаты») и открывается тот же `ReturnScreen`, что и
// кнопкой «Возврат» в чеке. Сам переход делает [ScreenOpenerHost] (go_router в приложении, фейк в тестах); он НЕ ждёт закрытия
// экрана — инструмент потом ждёт регистрацию ручки ≤ 2 с.
import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../features/ops/ops_screens.dart' show ReturnScreen;
import '../../api/api_client.dart';
import '../../providers.dart';
import 'action_tools.dart' show OpenResult;
import 'read_tools.dart' show Reader;

/// Что нужно открывателю от оболочки приложения.
abstract class ScreenOpenerHost {
  /// Текущий адрес (путь без параметров достаточно); пусто — неизвестен.
  String get location;

  /// Открыть маршрут поверх текущего (go_router push). Вернуться должен сразу после начала перехода.
  Future<void> push(String location);

  /// Открыть экран вне маршрутов (ReturnScreen — как из чека): Navigator.push(MaterialPageRoute).
  Future<void> pushPage(Widget page);
}

/// Рабочий хост над go_router: путь — `routerDelegate.currentConfiguration`, страницы — корневой навигатор роутера.
class RouterOpenerHost implements ScreenOpenerHost {
  final GoRouter router;
  RouterOpenerHost(this.router);

  @override
  String get location {
    try {
      return router.routerDelegate.currentConfiguration.uri.path;
    } catch (_) {
      return '';
    }
  }

  @override
  Future<void> push(String location) => router.push<Object?>(location);

  @override
  Future<void> pushPage(Widget page) async {
    final nav = router.routerDelegate.navigatorKey.currentState;
    if (nav == null) throw StateError('нет навигатора');
    await nav.push<void>(MaterialPageRoute<void>(builder: (_) => page));
  }
}

/// Маршруты экранов с ручками (ключ ручки → путь).
const aiHandleRoutes = <String, String>{
  'pay': '/pay',
  'close_shift': '/close-shift',
  'receive': '/admin/receive',
  'new_item': '/admin/new-product',
};

String _normNumber(String s) => s.replaceAll(RegExp(r'[#№\s]'), '').replaceFirst(RegExp(r'^0+(?=\d)'), '');

/// Открыватель для `RepoActionHost.fromReader(opener: …)`. Ключи: pay, close_shift, receive, new_item, return.
Future<OpenResult> Function(String handleKey, Map<String, dynamic> args) buildScreenOpener({required ScreenOpenerHost host, required Reader read}) {
  void start(Future<void> f) => unawaited(f.catchError((_) {}));

  Future<OpenResult> route(String path) async {
    if (host.location == path || host.location.startsWith('$path?')) return OpenResult.ok;
    try {
      start(Future.sync(() => host.push(path)));
      return OpenResult.ok;
    } catch (_) {
      return OpenResult.unsupported;
    }
  }

  Future<OpenResult> returnScreen(Map<String, dynamic> args) async {
    final sales = read(salesRepoProvider);
    final id = '${args['sale_id'] ?? ''}'.trim();
    final wanted = _normNumber('${args['check_number'] ?? ''}');
    try {
      String? saleId = id.isEmpty ? null : id;
      if (saleId == null) {
        if (wanted.isEmpty) return OpenResult.notFound;
        // номер чека — в последних продажах (новые первыми); дальше пяти страниц не ищем
        for (var page = 1; page <= 5 && saleId == null; page++) {
          final rows = await sales.sales(page: page);
          if (rows.isEmpty) break;
          for (final m in rows) {
            final n = _normNumber(str(m, 'number') ?? str(m, 'receipt_number') ?? '');
            if (n.isNotEmpty && n == wanted) {
              saleId = str(m, 'id');
              break;
            }
          }
        }
        if (saleId == null || saleId.isEmpty) return OpenResult.notFound;
      }
      final sale = await sales.sale(saleId);
      final number = str(sale, 'number') ?? str(sale, 'receipt_number') ?? '${args['check_number'] ?? ''}';
      final sid = saleId;
      start(Future.sync(() => host.pushPage(ReturnScreen(saleId: sid, number: number, sale: sale))));
      return OpenResult.ok;
    } on ApiException catch (e) {
      // чека нет на сервере (404) — «не нашла»; нет связи или сбой — экран недоступен, говорим честно
      return e.status == 404 ? OpenResult.notFound : OpenResult.unsupported;
    } catch (_) {
      return OpenResult.unsupported;
    }
  }

  return (handleKey, args) async {
    if (handleKey == 'return') return returnScreen(args);
    final path = aiHandleRoutes[handleKey];
    if (path == null) return OpenResult.unsupported;
    return route(path);
  };
}
