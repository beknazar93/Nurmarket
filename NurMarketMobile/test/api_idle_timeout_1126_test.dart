import 'package:dio/io.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';

/// 2026-11-26 (быстродействие): dio по умолчанию держит простаивающее соединение 3 с — каждый такт синхронизации заново проходил TCP+TLS.
/// ApiClient теперь держит соединение [ApiClient.connectionIdleTimeout]; проверяем, что адаптер именно с этим значением.
void main() {
  test('ApiClient держит простаивающее соединение дольше 3 секунд (65 с), а не как dio по умолчанию', () {
    final api = ApiClient(SessionStore());
    final adapter = api.adapterForTest;
    expect(adapter, isA<IOHttpClientAdapter>());
    final client = (adapter as IOHttpClientAdapter).createHttpClient!();
    addTearDown(() => client.close(force: true));
    expect(client.idleTimeout, ApiClient.connectionIdleTimeout);
    expect(ApiClient.connectionIdleTimeout, greaterThan(const Duration(seconds: 60)), reason: 'лента изменений спрашивается раз в минуту');
    expect(ApiClient.connectionIdleTimeout, lessThan(const Duration(seconds: 70)), reason: 'раньше, чем сервер за Cloudflare закроет соединение сам');
  });
}
