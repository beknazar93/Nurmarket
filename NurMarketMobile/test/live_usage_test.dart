// 2026-10-26 (В4): замер расхода токенов Live API (usageMetadata), порядок моделей для видеозвонка и повторное подключение с
// ключом возобновления — на фейках, без сети.
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/live_call.dart';

import 'support/call_rig.dart';

Future<void> _settle() => Future<void>.delayed(const Duration(milliseconds: 20));

void main() {
  setUp(LiveCall.resetForTest);

  group('usageMetadata', () {
    test('разбор: общие числа и разбивка по модальностям TEXT / AUDIO / IMAGE / VIDEO', () {
      final m = LiveMessage.parse(
        jsonEncode({
          'usageMetadata': {
            'promptTokenCount': 6800,
            'responseTokenCount': 120,
            'totalTokenCount': 6920,
            'promptTokensDetails': [
              {'modality': 'TEXT', 'tokenCount': 6500},
              {'modality': 'AUDIO', 'tokenCount': 200},
              {'modality': 'VIDEO', 'tokenCount': 100},
            ],
            'responseTokensDetails': [
              {'modality': 'AUDIO', 'tokenCount': 120},
            ],
          },
        }),
      )!;
      expect(m.usage!.total, 6920);
      expect(m.usage!.prompt, 6800);
      expect(m.usage!.response, 120);
      expect(m.usage!.promptDetails, {'TEXT': 6500, 'AUDIO': 200, 'VIDEO': 100});
      expect(m.usage!.responseDetails, {'AUDIO': 120});
      // нет usageMetadata / неполный — без падения
      expect(LiveMessage.parse('{"setupComplete":{}}')!.usage, isNull);
      expect(LiveMessage.parse('{"usageMetadata":{"totalTokenCount":5}}')!.usage!.total, 5);
      expect(LiveMessage.parse('{"usageMetadata":{"promptTokensDetails":"x"}}')!.usage!.promptDetails, isEmpty);
    });

    test('накопление: всего, на старте, пик, видео, звук; итоговая строка без ключа и речи', () {
      final t = LiveUsageTotals();
      t.add(const LiveUsage(total: 7000, promptDetails: {'TEXT': 7000}));
      t.add(const LiveUsage(total: 900, promptDetails: {'AUDIO': 300, 'IMAGE': 200}, responseDetails: {'AUDIO': 400}));
      t.add(const LiveUsage(total: 500, promptDetails: {'VIDEO': 150}));
      expect(t.total, 8400);
      expect(t.start, 7000);
      expect(t.peak, 7000);
      expect(t.video, 350);
      expect(t.audio, 700);
      expect(t.line, 'токены: всего 8400, на старте 7000, пик 7000, видео 350, звук 700, текст 7000 (сообщений 3)');
    });

    test('звонок: строка итога уходит при конце звонка', () async {
      final sockets = <FakeSocket>[];
      final call = LiveCall(
        apiKey: 'test-key',
        instruction: 'i',
        audio: FakeAudio(),
        connect: (u, h) async => FakeSocket().also(sockets.add),
      );
      String? line;
      call.onUsageLine = (l) => line = l;
      final started = call.start();
      await _settle();
      sockets.first.push({'setupComplete': {}});
      await started;
      sockets.first.push({
        'usageMetadata': {'totalTokenCount': 100, 'promptTokensDetails': [{'modality': 'TEXT', 'tokenCount': 100}]},
      });
      await _settle();
      call.end();
      expect(line, contains('всего 100'));
      expect(line, isNot(contains('test-key'))); // ключ в строку не попадает
    });
  });

  group('порядок моделей', () {
    test('голосовой звонок — как раньше; видеозвонок — native audio первой, остальные следом', () {
      expect(LiveCall.orderFor(video: false), LiveCall.models);
      final v = LiveCall.orderFor(video: true);
      expect(v.first, LiveCall.videoFirst);
      expect(v.toSet(), LiveCall.models.toSet());
      expect(v.length, LiveCall.models.length);
    });

    test('видеозвонок: первый setup идёт на native audio; не принимает видео — та же модель голосом (прежняя логика)', () async {
      final sockets = <FakeSocket>[];
      final call = LiveCall(apiKey: 'k', instruction: 'i', audio: FakeAudio(), video: true, connect: (u, h) async => FakeSocket().also(sockets.add));
      final started = call.start();
      await _settle();
      expect((sockets.first.sent.first['setup'] as Map)['model'], 'models/${LiveCall.videoFirst}');
      sockets.first.push({'setupComplete': {}});
      await started;
      expect(call.model, LiveCall.videoFirst);
      call.end();
    });

    test('запомненная модель голосового звонка не вытесняет native audio в видеозвонке, и наоборот', () async {
      final sockets = <FakeSocket>[];
      final voice = LiveCall(apiKey: 'k', instruction: 'i', audio: FakeAudio(), connect: (u, h) async => FakeSocket().also(sockets.add));
      final s1 = voice.start();
      await _settle();
      sockets.last.push({'setupComplete': {}});
      await s1;
      voice.end();
      expect(voice.model, LiveCall.models.first);
      final video = LiveCall(apiKey: 'k', instruction: 'i', audio: FakeAudio(), video: true, connect: (u, h) async => FakeSocket().also(sockets.add));
      final s2 = video.start();
      await _settle();
      expect((sockets.last.sent.first['setup'] as Map)['model'], 'models/${LiveCall.videoFirst}');
      sockets.last.push({'setupComplete': {}});
      await s2;
      video.end();
    });
  });

  group('повторное подключение', () {
    test('обрыв: одно переподключение с ключом возобновления, та же модель первой, без перебора моделей', () async {
      final sockets = <FakeSocket>[];
      final call = LiveCall(apiKey: 'k', instruction: 'i', audio: FakeAudio(), connect: (u, h) async => FakeSocket().also(sockets.add));
      final started = call.start();
      await _settle();
      sockets.first.push({'setupComplete': {}});
      await started;
      sockets.first.push({
        'sessionResumptionUpdate': {'newHandle': 'h1', 'resumable': true},
      });
      await _settle();
      await sockets.first.close();
      await _settle();
      expect(sockets.length, 2); // одно переподключение, не серия
      final setup = sockets[1].sent.first['setup'] as Map;
      expect(setup['model'], 'models/${call.model}');
      expect((setup['sessionResumption'] as Map)['handle'], 'h1');
      sockets[1].push({'setupComplete': {}});
      await _settle();
      expect(call.ended, isFalse);
      call.end();
    });
  });
}

extension<T> on T {
  T also(void Function(T) f) {
    f(this);
    return this;
  }
}
