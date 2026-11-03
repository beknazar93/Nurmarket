import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/update/app_update_service.dart';

/// 2026-11-01 (обновление через GitHub): разбор тега, сравнение версий, выбор выпуска — без сети.
Map<String, dynamic> _rel(String tag, {bool pre = true, bool draft = false, String? apk, String body = ''}) => {
  'tag_name': tag,
  'prerelease': pre,
  'draft': draft,
  'body': body,
  'assets': [
    if (apk != null) {'name': apk, 'size': 50000000, 'browser_download_url': 'https://github.com/${UpdateRules.repo}/releases/download/$tag/$apk'},
  ],
};

void main() {
  test('тег и версии', () {
    expect(UpdateRules.parseVersion('mobile-1.18.14'), [1, 18, 14]);
    expect(UpdateRules.parseVersion('v1.2'), [1, 2, 0]);
    expect(UpdateRules.parseVersion('mobile-1.18.14-test1'), isNull);
    expect(UpdateRules.isNewer('1.18.14', '1.18.13'), isTrue);
    expect(UpdateRules.isNewer('1.18.13', '1.18.13'), isFalse);
    expect(UpdateRules.isNewer('1.9.0', '1.18.0'), isFalse);
    expect(UpdateRules.isNewer('1.19.0', '1.18.99'), isTrue);
  });

  test('берётся самый новый выпуск с APK, черновики и чужие файлы пропускаются', () {
    final r = UpdateRules.pickLatest([
      _rel('mobile-1.18.13', apk: 'nurmarket-mobile-1.18.13.apk'),
      _rel('mobile-1.18.15', draft: true, apk: 'nurmarket-mobile-1.18.15.apk'),
      _rel('mobile-1.18.14', apk: 'nurmarket-mobile-1.18.14.apk', body: 'x' * 3000),
      _rel('mobile-1.18.16'), // без APK
    ], allowPrerelease: true);
    expect(r?.version, '1.18.14');
    expect(r?.notes.length, lessThanOrEqualTo(UpdateRules.maxNotes + 1));
    expect(UpdateRules.pickLatest('мусор'), isNull);
  });

  test('М3-07: обычному пользователю — только стабильные выпуски; тестовые (prerelease) — режиму тестировщика', () {
    final list = [
      _rel('mobile-1.18.16', apk: 'a.apk'), // prerelease
      _rel('mobile-1.18.15', pre: false, apk: 'b.apk'),
    ];
    expect(UpdateRules.pickLatest(list)?.version, '1.18.15');
    expect(UpdateRules.pickLatest(list, allowPrerelease: true)?.version, '1.18.16');
    // стабильных выпусков нет — обычному пользователю обновлений нет
    expect(UpdateRules.pickLatest([_rel('mobile-1.18.16', apk: 'a.apk')]), isNull);
  });

  test('М3-07: «стоп-кран» — отозванная версия не предлагается (файл blocked-versions.json или пометка в описании)', () {
    final list = [
      _rel('mobile-1.18.17', pre: false, apk: 'c.apk'),
      _rel('mobile-1.18.16', pre: false, apk: 'b.apk'),
      _rel('mobile-1.18.15', pre: false, apk: 'a.apk', body: 'Старый выпуск'),
    ];
    expect(UpdateRules.pickLatest(list, blocked: {'1.18.17'})?.version, '1.18.16');
    expect(UpdateRules.pickLatest(list, blocked: {'1.18.17', '1.18.16'})?.version, '1.18.15');
    final withNote = [_rel('mobile-1.18.17', pre: false, apk: 'c.apk', body: 'Ошибка в оплате [ОТОЗВАН]'), _rel('mobile-1.18.16', pre: false, apk: 'b.apk')];
    expect(UpdateRules.pickLatest(withNote)?.version, '1.18.16');
    expect(UpdateRules.parseBlocked('{"blocked": ["mobile-1.18.17", "1.18.16", "мусор"]}'), {'1.18.17', '1.18.16'});
    expect(UpdateRules.parseBlocked('["1.18.15"]'), {'1.18.15'});
    expect(UpdateRules.parseBlocked('не json'), isEmpty);
  });

  test('М3-07: ссылка разбирается как Uri — чужой хост, .. в пути и http не проходят', () {
    final base = 'https://github.com/${UpdateRules.repo}/releases/download/mobile-1.0.0';
    expect(UpdateRules.trustedApkUrl('$base/a.apk'), isTrue);
    expect(UpdateRules.trustedApkUrl('https://github.com.evil.example/${UpdateRules.repo}/releases/download/t/a.apk'), isFalse);
    expect(UpdateRules.trustedApkUrl('https://github.com/${UpdateRules.repo}/releases/download/../../../evil/a.apk'), isFalse);
    expect(UpdateRules.trustedApkUrl('https://github.com/other/repo/releases/download/t/a.apk'), isFalse);
  });

  test('APK только из нашего репозитория', () {
    expect(UpdateRules.trustedApkUrl('https://github.com/${UpdateRules.repo}/releases/download/mobile-1.0.0/a.apk'), isTrue);
    expect(UpdateRules.trustedApkUrl('https://evil.example/a.apk'), isFalse);
    expect(UpdateRules.trustedApkUrl('http://github.com/${UpdateRules.repo}/a.apk'), isFalse);
  });
}
