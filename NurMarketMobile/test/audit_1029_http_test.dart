// 2026-10-29 (аудит мобилки, п. 5 и «Мелкое»): страница входа Wi-Fi (HTML вместо JSON) и 5xx — настоящий клиент против локального
// HttpServer. Отдельный файл без testWidgets: с биндингом виджетов любой HttpClient получает 400.
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';

void main() {
  group('связь и страница входа Wi-Fi (пп. 5, мелкое)', () {
    late HttpServer server;
    late String mode;
    late ApiClient api;

    setUp(() async {
      mode = 'json';
      server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      server.listen((req) async {
        if (mode == 'html') {
          req.response
            ..statusCode = 200
            ..headers.contentType = ContentType.html
            ..write('<!DOCTYPE html><html><body>Wi-Fi login</body></html>');
        } else if (mode == '502') {
          req.response
            ..statusCode = 502
            ..write('bad gateway');
        } else {
          req.response
            ..statusCode = 200
            ..headers.contentType = ContentType.json
            ..write('{"ok":true}');
        }
        await req.response.close();
      });
      api = ApiClient(SessionStore(), baseUrl: 'http://127.0.0.1:${server.port}');
    });
    tearDown(() => server.close(force: true));

    test('HTML вместо JSON → понятная ошибка, связь «нет»; ответ сервера возвращает связь', () async {
      mode = 'html';
      await expectLater(api.get('api/x'), throwsA(isA<ApiException>().having((e) => e.message, 'message', contains('Wi-Fi'))));
      expect(api.link.value.offline, isTrue);
      mode = 'json';
      expect(await api.get('api/x'), {'ok': true});
      expect(api.link.value.offline, isFalse);
      expect(api.link.value.lastOk, isNotNull);
    });

    test('5xx — не «нет связи»: сообщение про сервер, плашка связи не загорается', () async {
      mode = '502';
      await expectLater(api.get('api/x'), throwsA(isA<ApiException>().having((e) => e.message, 'message', allOf(contains('502'), isNot(contains('Нет связи'))))));
      expect(api.link.value.offline, isFalse);
    });

    test('сервер недоступен — связи нет', () async {
      final dead = ApiClient(SessionStore(), baseUrl: 'http://127.0.0.1:1');
      await expectLater(dead.get('api/x'), throwsA(isA<ApiException>()));
      expect(dead.link.value.offline, isTrue);
    });
  });
}
