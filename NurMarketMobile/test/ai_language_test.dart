// 2026-11-05: язык ИИ — команды смены языка (распознаватель), правило языка в инструкции, языковой пакет. Без сети и без базы.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/ai_language.dart';
import 'package:nurmarket/core/language_pack.dart';

void main() {
  tearDown(() {
    LanguagePackGate.debugSet(false);
    AiLanguage.debugSet(null);
  });

  group('команда смены языка — распознаётся', () {
    // (фраза, язык, менять ли и интерфейс)
    const cases = <(String, String, bool)>[
      // кыргызский — только язык ИИ
      ('кыргызча жооп бер', 'ky', false),
      ('Кыргызча жооп бер.', 'ky', false),
      ('кыргызча сүйлө', 'ky', false),
      ('кыргызча сүйлөшөлү', 'ky', false),
      ('kyrgyzcha joop ber', 'ky', false),
      ('qirgizcha javob ber', 'ky', false),
      ('kirgizcha suylo', 'ky', false),
      ('отвечай по-кыргызски', 'ky', false),
      ('отвечай на кыргызском', 'ky', false),
      ('говори по-кыргызски', 'ky', false),
      ('говори на кыргызском языке', 'ky', false),
      ('ответь на кыргызском', 'ky', false),
      // кыргызский — интерфейс и ИИ
      ('переведи на кыргызский', 'ky', true),
      ('Переведи на кыргызский язык', 'ky', true),
      ('переведи на кыргызски', 'ky', true),
      ('переходи на кыргызский', 'ky', true),
      ('перейди на кыргызский', 'ky', true),
      ('переключи на кыргызский', 'ky', true),
      ('смени язык на кыргызский', 'ky', true),
      ('поменяй язык на кыргызский', 'ky', true),
      ('включи кыргызский', 'ky', true),
      ('давай по-кыргызски', 'ky', true),
      ('кыргызчага өт', 'ky', true),
      ('кыргызчага которул', 'ky', true),
      ('переведи интерфейс на кыргызский', 'ky', true),
      ('переведи программу на кыргызский', 'ky', true),
      // русский
      ('говори по-русски', 'ru', false),
      ('отвечай по-русски', 'ru', false),
      ('отвечай на русском', 'ru', false),
      ('русча жооп бер', 'ru', false),
      ('орусча сүйлө', 'ru', false),
      ('переходи на русский', 'ru', true),
      ('переведи на русский', 'ru', true),
      ('давай по-русски', 'ru', true),
      ('орусчага өт', 'ru', true),
      ('переключи на русский язык', 'ru', true),
      // другие языки (применяются только при пакете — это решает вызывающий)
      ('переведи на английский', 'en', true),
      ('отвечай по-английски', 'en', false),
      ('speak english', 'en', false),
      ('говори по-турецки', 'tr', false),
      ('turkce konus', 'tr', false),
      ('отвечай по-узбекски', 'uz', false),
      ('uzbekcha gapir', 'uz', false),
    ];
    for (final (text, code, iface) in cases) {
      test('«$text»', () {
        final c = AiLanguage.parse(text);
        expect(c, isNotNull, reason: 'должна быть командой: $text');
        expect(c!.code, code);
        expect(c.interfaceToo, iface);
      });
    }
    test('не меньше 40 фраз', () => expect(cases.length >= 40, isTrue));
  });

  group('обычные фразы — НЕ команда', () {
    const phrases = [
      'кыргызский рынок',
      'продай по-русски',
      'я говорил по-русски',
      'говорят по-кыргызски',
      'как будет привет по-кыргызски',
      'покажи продажи',
      'курс русского рубля',
      'переведи привет на кыргызский',
      'с русского на кыргызский переведи',
      'Кыргызстан переходи',
      'открой склад',
      'сколько продали сегодня',
      'отвечай клиенту',
      'русский',
      'выручка за русский день',
      'вчера мы долго говорили про кыргызский рынок и цены на товары в магазине сегодня',
      '',
      '   ',
    ];
    for (final p in phrases) {
      test('«$p»', () => expect(AiLanguage.parse(p), isNull));
    }
  });

  group('правило языка и чужая речь', () {
    test('правило есть для ru и ky и называет язык', () {
      expect(AiLanguage.rule('ru'), contains('ТОЛЬКО по-русски'));
      expect(AiLanguage.rule('ru'), contains('speak and answer ONLY in Russian'));
      expect(AiLanguage.rule('ky'), contains('кыргызча'));
      expect(AiLanguage.rule('ky'), contains('speak and answer ONLY in Kyrgyz'));
      expect(AiLanguage.rule('ky'), contains('Uzbek, Turkish'));
    });

    test('чужая речь: узбекские/турецкие буквы и слова — при ru/ky; не при en/tr/uz', () {
      expect(AiLanguage.looksForeign('Bugün satış yaxshi', 'ru'), isTrue);
      expect(AiLanguage.looksForeign('Бугун сотув қиламиз', 'ky'), isTrue);
      expect(AiLanguage.looksForeign('Сегодня продано на 12 тысяч сом', 'ru'), isFalse);
      expect(AiLanguage.looksForeign('Бүгүн 12 миң сом сатылды', 'ky'), isFalse);
      expect(AiLanguage.looksForeign('Bugün satış', 'tr'), isFalse);
    });

    test('сообщение о смене языка называет язык', () {
      expect(AiLanguage.switchMessage('ky'), contains('Kyrgyz'));
      expect(AiLanguage.correction('ru'), contains('Russian'));
    });
  });

  group('языковой пакет', () {
    test('без пакета доступны только ru и ky', () {
      LanguagePackGate.debugSet(false);
      expect(LanguagePackGate.isAvailable('ru'), isTrue);
      expect(LanguagePackGate.isAvailable('ky'), isTrue);
      for (final c in ['en', 'tr', 'uz']) {
        expect(LanguagePackGate.isAvailable(c), isFalse);
        expect(LanguagePackGate.effective(c), 'ru');
      }
    });

    test('с пакетом доступны все', () {
      LanguagePackGate.debugSet(true);
      for (final c in ['ru', 'ky', 'en', 'tr', 'uz']) {
        expect(LanguagePackGate.effective(c), c);
      }
    });

    test('язык ИИ без пакета — только ru/ky; платный в настройке сбрасывается на русский', () {
      LanguagePackGate.debugSet(false);
      AiLanguage.debugSet(null);
      expect(AiLanguage.of('uz'), 'ru'); // язык интерфейса uz без пакета → русский
      expect(AiLanguage.of('ky'), 'ky');
      AiLanguage.debugSet('en');
      expect(AiLanguage.of('ky'), 'ru');
      LanguagePackGate.debugSet(true);
      expect(AiLanguage.of('ky'), 'en');
    });

    test('ответ сервера: kassa-language active', () {
      final now = DateTime(2026, 11, 5);
      expect(LanguagePackGate.parseGranted([{'code': 'kassa-language', 'active': true, 'until': null}], now: now), isTrue);
      expect(LanguagePackGate.parseGranted([{'code': 'kassa-language', 'active': false}], now: now), isFalse);
      expect(LanguagePackGate.parseGranted([{'code': 'kassa-language', 'active': true, 'until': '2026-11-01'}], now: now), isFalse);
      expect(LanguagePackGate.parseGranted([{'code': 'kassa-language', 'active': true, 'until': '2026-12-01'}], now: now), isTrue);
      expect(LanguagePackGate.parseGranted([{'code': 'kassa-loyalty', 'active': true}], now: now), isFalse);
      expect(LanguagePackGate.parseGranted(<dynamic>[], now: now), isFalse);
    });
  });
}
