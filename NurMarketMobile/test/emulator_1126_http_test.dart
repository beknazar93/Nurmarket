// 2026-11-26 (проверка мобилки 1.18.23 в эмуляторе): вход со слотами устройств (device_key, replace_device, коды device_key_required / device_slot_taken)
// и возврат по PIN (403 pin_required → users/auth/pin/verify/ → X-Confirm-Token). Настоящий клиент против локального HttpServer.
// Отдельный файл без testWidgets: с биндингом виджетов любой HttpClient получает 400.
import 'dart:convert';
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/checkout_device.dart';
import 'package:nurmarket/core/api/confirm_pin.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/l10n/app_localizations_ru.dart';

class _Hit {
  final String path;
  final Map<String, dynamic> body;
  final String? confirm;
  _Hit(this.path, this.body, this.confirm);
}

void main() {
  late HttpServer server;
  late ApiClient api;
  late AppDatabase db;
  final hits = <_Hit>[];
  // что отвечает вход: null — 200 с токенами
  late Future<void> Function(HttpRequest req, Map<String, dynamic> body) onLogin;
  final ru = AppLocalizationsRu();

  setUp(() async {
    hits.clear();
    onLogin = (req, body) async {
      req.response
        ..statusCode = 200
        ..write('{"access":"a1","refresh":"r1"}');
    };
    server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((req) async {
      final raw = await utf8.decoder.bind(req).join();
      final body = raw.isEmpty ? <String, dynamic>{} : (jsonDecode(raw) as Map).cast<String, dynamic>();
      hits.add(_Hit(req.uri.path, body, req.headers.value('x-confirm-token')));
      req.response.headers.contentType = ContentType.json;
      final path = req.uri.path;
      if (path.endsWith('/auth/login/')) {
        await onLogin(req, body);
      } else if (path.endsWith('/auth/pin/verify/')) {
        if (body['pin'] == '1234') {
          req.response
            ..statusCode = 200
            ..write('{"token":"confirm-xyz","expires_in":300}');
        } else {
          req.response
            ..statusCode = 400
            ..write('{"code":"pin_invalid","attempts_left":4}');
        }
      } else if (path.endsWith('/return/')) {
        if (req.headers.value('x-confirm-token') == 'confirm-xyz') {
          req.response
            ..statusCode = 200
            ..write('{"refund":{"id":"r1","amount":"150.00","cash":"150.00"}}');
        } else {
          req.response
            ..statusCode = 403
            ..write('{"code":"pin_required","detail":"Требуется подтверждение PIN."}');
        }
      } else {
        req.response
          ..statusCode = 200
          ..write('{}');
      }
      await req.response.close();
    });
    api = ApiClient(SessionStore(), baseUrl: 'http://127.0.0.1:${server.port}');
    db = AppDatabase(NativeDatabase.memory());
  });
  tearDown(() async {
    await server.close(force: true);
    await db.close();
  });

  group('вход: слоты устройств', () {
    test('с device_key уходят client_type=mobile, device_key (8–64 знака) и device_name; replace_device без согласия не шлётся', () async {
      final key = await CheckoutDevice.key(db);
      await api.login(' mini@nur.kg ', 'pw', deviceKey: key, deviceName: CheckoutDevice.deviceName);
      final body = hits.single.body;
      expect(body['email'], 'mini@nur.kg');
      expect(body['client_type'], 'mobile');
      expect(body['device_key'], key);
      expect(RegExp(r'^[A-Za-z0-9_-]{8,64}$').hasMatch(body['device_key'] as String), isTrue, reason: 'правило сервера для device_key');
      expect(body['device_name'], startsWith('NurMarket mobile'));
      expect(body.containsKey('replace_device'), isFalse);
    });

    test('device_key стабилен: второй вход шлёт тот же ключ (иначе свой же повторный вход считался бы «другим телефоном»)', () async {
      final a = await CheckoutDevice.key(db);
      await api.login('a@b.kg', 'pw', deviceKey: a);
      final b = await CheckoutDevice.key(db);
      await api.login('a@b.kg', 'pw', deviceKey: b);
      expect(hits[0].body['device_key'], hits[1].body['device_key']);
    });

    test('согласие «Выйти там и войти здесь» — повтор с replace_device=true', () async {
      await api.login('a@b.kg', 'pw', deviceKey: 'mob-abc12345', replaceDevice: true);
      expect(hits.single.body['replace_device'], isTrue);
      expect(hits.single.body['device_key'], 'mob-abc12345');
    });

    test('без device_key (старый путь) тело входа прежнее: только email и password', () async {
      await api.login('a@b.kg', 'pw');
      expect(hits.single.body.keys.toSet(), {'email', 'password'});
    });

    test('400 device_key_required распознаётся — это не «неверный логин или пароль»', () async {
      onLogin = (req, body) async {
        req.response
          ..statusCode = 400
          ..write('{"code":"device_key_required","detail":"На тарифе Мини вход только с одного телефона: приложение должно передать device_key"}');
      };
      try {
        await api.login('mini@nur.kg', 'pw');
        fail('ждали отказ');
      } on ApiException catch (e) {
        expect(e.isDeviceKeyRequired, isTrue);
        expect(e.isDeviceSlotTaken, isFalse);
        expect(e.status, 400);
      }
    });

    test('409 device_slot_taken распознаётся, имя другого устройства доступно из тела', () async {
      onLogin = (req, body) async {
        req.response
          ..statusCode = 409
          ..write('{"code":"device_slot_taken","slot":"mobile","device":{"id":"d1","name":"Телефон Айбека","platform":"android"}}');
      };
      try {
        await api.login('mini@nur.kg', 'pw', deviceKey: 'mob-abc12345');
        fail('ждали отказ');
      } on ApiException catch (e) {
        expect(e.isDeviceSlotTaken, isTrue);
        expect(e.isDeviceKeyRequired, isFalse);
        expect(((e.payload as Map)['device'] as Map)['name'], 'Телефон Айбека');
        expect(api.session, isNull, reason: 'вход не выполнен — токенов нет');
      }
    });

    test('обычный отказ 401 — по-прежнему не слот и не PIN', () async {
      onLogin = (req, body) async {
        req.response
          ..statusCode = 401
          ..write('{"detail":"Неверные учётные данные"}');
      };
      try {
        await api.login('a@b.kg', 'bad', deviceKey: 'mob-abc12345');
        fail('ждали отказ');
      } on ApiException catch (e) {
        expect(e.status, 401);
        expect(e.isDeviceSlotTaken || e.isDeviceKeyRequired || e.isPinRequired, isFalse);
      }
    });
  });

  group('возврат: 403 pin_required', () {
    test('PIN принят: verify без области действия → токен → повтор возврата с X-Confirm-Token', () async {
      final ops = OpsRepository(api, db);
      var asked = 0;
      final resp = await ops.returnSale('sale-1', const [], askPin: () async {
        asked++;
        return ConfirmPin.verify(api, '1234');
      });
      expect(asked, 1);
      expect((resp['refund'] as Map)['amount'], '150.00');
      final paths = hits.map((h) => h.path).toList();
      expect(paths, ['/api/main/pos/sales/sale-1/return/', '/api/users/auth/pin/verify/', '/api/main/pos/sales/sale-1/return/']);
      expect(hits[0].confirm, isNull, reason: 'первая отправка — без токена');
      expect(hits[1].body, {'pin': '1234'}, reason: 'без scope');
      expect(hits[2].confirm, 'confirm-xyz');
    });

    test('отмена PIN — исходная ошибка pin_required, второй возврат не отправляется', () async {
      final ops = OpsRepository(api, db);
      await expectLater(
        ops.returnSale('sale-1', const [], askPin: () async => null),
        throwsA(isA<ApiException>().having((e) => e.isPinRequired, 'isPinRequired', isTrue)),
      );
      expect(hits.where((h) => h.path.endsWith('/return/')).length, 1);
    });

    test('без askPin (ИИ, сервисные пути) — ошибка как раньше, PIN не спрашивается', () async {
      final ops = OpsRepository(api, db);
      await expectLater(ops.returnSale('sale-1', const []), throwsA(isA<ApiException>().having((e) => e.isPinRequired, 'isPinRequired', isTrue)));
      expect(hits.length, 1);
    });

    test('неверный PIN: понятный текст с числом оставшихся попыток, а не «Ошибка сервера 400»', () async {
      try {
        await ConfirmPin.verify(api, '0000');
        fail('ждали отказ');
      } on ApiException catch (e) {
        expect(ConfirmPin.errorText(ru, e), 'PIN неверный. Осталось попыток: 4.');
      }
    });

    test('тексты отказа проверки PIN: заблокирован и не задан', () {
      expect(ConfirmPin.errorText(ru, ApiException('x', status: 429, payload: {'code': 'pin_locked'})), ru.pinLocked);
      expect(ConfirmPin.errorText(ru, ApiException('x', status: 403, payload: {'code': 'pin_not_set'})), ru.pinNotSet);
      expect(ConfirmPin.errorText(ru, ApiException('x', status: 400, payload: {'code': 'pin_invalid'})), ru.pinInvalid);
      expect(ConfirmPin.errorText(ru, ApiException('Сервер недоступен', status: 500)), 'Сервер недоступен');
    });
  });
}
