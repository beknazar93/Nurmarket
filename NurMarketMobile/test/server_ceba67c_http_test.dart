// 2026-11-05 (сервер ceba67c): заголовок X-NurMarket-Client и запрос признаков сервера — настоящий клиент против локального HttpServer.
// Отдельный файл без testWidgets: с биндингом виджетов любой HttpClient получает 400.
import 'dart:convert';
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/server_capabilities.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/sale/cart.dart';

void main() {
  late HttpServer server;
  late ApiClient api;
  late AppDatabase db;
  var capsStatus = 200;
  var capsBody = '{"server_version":"ceba67c","features":["weight_line_total_floor","checkout_branch"]}';
  var company = '{"scale_barcode_amount_unit":"tiyin"}';
  final headers = <String, String?>{};

  setUp(() async {
    capsStatus = 200;
    capsBody = '{"server_version":"ceba67c","features":["weight_line_total_floor","checkout_branch"]}';
    company = '{"scale_barcode_amount_unit":"tiyin"}';
    headers.clear();
    server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((req) async {
      headers[req.uri.path] = req.headers.value('x-nurmarket-client');
      req.response.headers.contentType = ContentType.json;
      if (req.uri.path.endsWith('/capabilities/')) {
        req.response
          ..statusCode = capsStatus
          ..write(capsBody);
      } else if (req.uri.path.endsWith('/company/')) {
        req.response
          ..statusCode = 200
          ..write(company);
      } else {
        req.response
          ..statusCode = 200
          ..write('{}');
      }
      await req.response.close();
    });
    api = ApiClient(SessionStore(), baseUrl: 'http://127.0.0.1:${server.port}');
    db = AppDatabase(NativeDatabase.memory());
    ApiClient.clientTag = 'mobile/1.18.18';
    ServerCapabilities.debugSet(null);
  });
  tearDown(() async {
    await server.close(force: true);
    await db.close();
    ServerCapabilities.debugSet(null);
  });

  test('каждый запрос несёт X-NurMarket-Client: mobile/<версия>', () async {
    await api.get('api/x/');
    expect(headers['/api/x/'], 'mobile/1.18.18');
  });

  test('refresh: признаки получены, округление весовых включено, шаг весов — по компании', () async {
    expect(await ServerCapabilities.refresh(api, db, 'u|1'), isTrue);
    expect(ServerCapabilities.has('checkout_branch'), isTrue);
    expect(ServerRounding.weightLineFloor, isTrue);
    expect(ServerRounding.weightStepCents, 1);
    company = '{"scale_barcode_amount_unit":"som"}';
    expect(await ServerCapabilities.refresh(api, db, 'u|1'), isTrue);
    expect(ServerRounding.weightStepCents, 100);
  });

  test('сбой запроса (401/500) не запоминается как «признаков нет»: прошлый список того же аккаунта остаётся', () async {
    expect(await ServerCapabilities.refresh(api, db, 'u|1'), isTrue);
    capsStatus = 500;
    capsBody = '{"detail":"oops"}';
    expect(await ServerCapabilities.refresh(api, db, 'u|1'), isFalse);
    expect(ServerCapabilities.has('checkout_branch'), isTrue);
    expect(ServerRounding.weightLineFloor, isTrue);
  });

  test('кэш переживает перезапуск только для того же аккаунта; чужой аккаунт признаков не получает', () async {
    expect(await ServerCapabilities.refresh(api, db, 'u|1'), isTrue);
    ServerCapabilities.debugSet(null); // «перезапуск»
    await ServerCapabilities.loadCached(db, 'u|2');
    expect(ServerCapabilities.known, isFalse);
    await ServerCapabilities.loadCached(db, 'u|1');
    expect(ServerCapabilities.has('weight_line_total_floor'), isTrue);
    expect(ServerRounding.weightLineFloor, isTrue);
  });

  test('смена аккаунта: список прошлого не действует, пока не получен свой; выход всё сбрасывает', () async {
    expect(await ServerCapabilities.refresh(api, db, 'u|1'), isTrue);
    capsBody = '{"features":[]}';
    expect(await ServerCapabilities.refresh(api, db, 'u|2'), isTrue);
    expect(ServerCapabilities.has('checkout_branch'), isFalse);
    expect(ServerRounding.weightLineFloor, isFalse);
    await ServerCapabilities.reset(db);
    expect(ServerCapabilities.known, isFalse);
  });

  test('ответ не того вида — сбой, а не пустой список', () async {
    capsBody = jsonEncode('просто строка');
    expect(await ServerCapabilities.refresh(api, db, 'u|1'), isFalse);
    expect(ServerCapabilities.known, isFalse);
  });
}
