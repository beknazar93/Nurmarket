// 2026-11-05 (жалобы владельца: «ИИ в звонке сам уходит на узбекский/турецкий» и «по команде „кыргызча жооп бер“ — отвечай только на кыргызском»).
// Язык ИИ (звонок, видеозвонок, Нур Советник голосом и текстом) НЕ угадывается по звуку. Он меняется только (а) настройкой языка приложения
// или (б) явной командой пользователя, которую узнаёт эта программа сама (не модель) по расшифровке речи или тексту чата.
// Без пакета «Язык» допустимы только ru и ky (LanguagePackGate); с пакетом — ещё en/tr/uz по команде.
// Образец на ПК: NurMarketKassa.Infrastructure/Services/AiVoiceLanguage.cs (правило языка, поправка, поиск чужих букв и слов).
// В speechConfig код языка не передаётся: native-audio модели Live сами выбирают язык, а неверное поле рвёт соединение.
import '../db/database.dart';
import '../language_pack.dart';

/// Команда смены языка, узнанная в речи/тексте: [code] — язык; [interfaceToo] — менять и язык интерфейса (а не только язык ИИ).
class LangCommand {
  final String code;
  final bool interfaceToo;
  const LangCommand(this.code, this.interfaceToo);
  @override
  String toString() => 'LangCommand($code, interface: $interfaceToo)';
}

class AiLanguage {
  AiLanguage._();

  static const _key = 'ai.lang';
  static String? _explicit;

  /// Прочитать сохранённый язык ИИ (пусто — следует языку интерфейса).
  static Future<void> load(AppDatabase db) async {
    try {
      final v = (await db.getSetting(_key) ?? '').trim();
      _explicit = const {'ru', 'ky', 'en', 'tr', 'uz'}.contains(v) ? v : null;
    } catch (_) {
      _explicit = null;
    }
  }

  /// Запомнить язык ИИ на устройстве (null — вернуться к языку интерфейса).
  static Future<void> save(AppDatabase db, String? code) async {
    _explicit = code;
    try {
      await db.setSetting(_key, code ?? '');
    } catch (_) {}
  }

  /// Язык ИИ сейчас: сохранённый (иначе язык интерфейса [ui]); платный без пакета «Язык» → русский.
  static String of(String ui) => LanguagePackGate.effective(_explicit ?? ui);

  /// Только для тестов.
  static void debugSet(String? code) => _explicit = code;

  static String name(String code) => switch (code) {
    'ky' => 'Kyrgyz',
    'en' => 'English',
    'tr' => 'Turkish',
    'uz' => 'Uzbek',
    _ => 'Russian',
  };

  /// Жёсткое правило языка — первой строкой инструкции звонка/чата (на языке ИИ + короткий английский дубль для модели).
  static String rule(String code) {
    final n = name(code);
    final english =
        'LANGUAGE RULE: speak and answer ONLY in $n. Never switch to Uzbek, Turkish, Kazakh, English or any other language (unless it is $n), '
        'even if the speech is unclear, accented or sounds like one of them; if you did not understand, ask again in $n. '
        "Only the program setting or the user's explicit request to change the language (which the program passes to you) changes the language.";
    final native = switch (code) {
      'ky' =>
        'ТИЛ ЭРЕЖЕСИ (катуу): сүйлө жана жооп бер ЖАЛГЫЗ кыргызча. Эч качан өзбекче, түркчө, казакча, англисче жана башка тилге өтпө — сүйлөө түшүнүксүз, акцент менен же ошого окшош угулса да; '
            'түшүнбөсөң, кыргызча кайра сура. Өзбекчеге же түркчөгө окшош угулган сөздү кыргызча деп эсептей бер. Тилди программанын жөндөөсү же колдонуучунун ачык суранычы гана өзгөртөт.',
      'en' => 'LANGUAGE (strict): speak and answer ONLY in English. Never switch to another language, even if the speech is unclear or accented; ask again in English.',
      'tr' => 'DİL KURALI (kesin): yalnızca Türkçe konuş ve yanıtla. Konuşma belirsiz, aksanlı veya başka dile benzese bile başka dile geçme; anlamazsan Türkçe tekrar sor.',
      'uz' => "TIL QOIDASI (qat'iy): faqat o'zbekcha gapiring va javob bering. Nutq noaniq yoki aksentli bo'lsa ham boshqa tilga o'tmang; tushunmasangiz o'zbekcha qayta so'rang.",
      _ =>
        'ЖЁСТКОЕ ПРАВИЛО ЯЗЫКА: говори и отвечай ТОЛЬКО по-русски. Никогда не переходи на узбекский, турецкий, казахский, английский и другие языки, '
            'даже если речь неразборчива, с акцентом или похожа на них; не поняв — переспроси по-русски. Речь, похожую на узбекскую или турецкую, считай русской или кыргызской. '
            'Язык меняет только настройка программы или явная просьба пользователя (её передаёт программа).',
    };
    return '$native\n$english';
  }

  /// Короткая поправка для сеанса, если ИИ уже ушёл на чужой язык (шлётся один раз, без ответа вслух).
  static String correction(String code) =>
      '[Программа] Отвечай только на языке ИИ: ${name(code)}. Не переходи на другие языки. Reply only in ${name(code)}.';

  /// Служебная реплика в идущий сеанс после явной команды смены языка: новая инструкция без обрыва звонка + короткое подтверждение голосом.
  static String switchMessage(String code) {
    final n = name(code);
    return '[Программа] Пользователь попросил сменить язык. С этой минуты говори и отвечай ТОЛЬКО на этом языке: $n. '
        'Never switch to Uzbek, Turkish, Kazakh or any other language unless it is $n. Коротко подтверди одной фразой на $n: «${confirmPhrase(code)}».';
  }

  static String confirmPhrase(String code) => switch (code) {
    'ky' => 'Жарайт, кыргызча сүйлөйм',
    'en' => 'Okay, I will speak English',
    'tr' => 'Tamam, Türkçe konuşuyorum',
    'uz' => "Xo'p, o'zbekcha gapiraman",
    _ => 'Хорошо, говорю по-русски',
  };

  static String notConnectedText(String code) => switch (code) {
    'ky' => 'Тил топтому туташтырылган эмес — орусча жана кыргызча гана бар.',
    'uz' => "Til paketi ulanmagan — faqat rus va qirg'iz tillari mavjud.",
    'en' => 'The language pack is not connected — only Russian and Kyrgyz are available.',
    'tr' => 'Dil paketi bağlı değil — yalnızca Rusça ve Kırgızca kullanılabilir.',
    _ => 'Языковой пакет не подключён — доступны русский и кыргызский.',
  };

  // Буквы, которых нет ни в русском, ни в кыргызском: узбекские/казахские кириллические и турецко-узбекская латиница.
  static final _foreignLetters = RegExp('[ўғқҳşğıİŞĞçÇöÖüÜ]');
  static final _foreignWords = RegExp(
    r'\b(va|bilan|uchun|yaxshi|rahmat|salom|ham|lekin|savdo|bugun|kecha|mijoz|mahsulot|ve|için|evet|hayır|tamam|merhaba|teşekkür|değil|bugün|satış|müşteri|ürün|nasıl|çok)\b',
    caseSensitive: false,
  );

  /// true — в расшифровке ответа ИИ явные признаки узбекского/турецкого, а язык ИИ — русский или кыргызский
  /// (для en/tr/uz слова этих языков законны, не проверяем).
  static bool looksForeign(String text, String code) {
    if (text.trim().isEmpty) return false;
    if (code != 'ru' && code != 'ky') return false;
    return _foreignLetters.hasMatch(text) || _foreignWords.hasMatch(text);
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Распознаватель команды смены языка. Работает по «скелету» слов (кириллица и латиница сводятся к одним буквам),
  // поэтому «кыргызча / kyrgyzcha / qirg'izcha» и неточности распознавания не мешают. Команда срабатывает ТОЛЬКО
  // когда в короткой фразе вместе есть слово языка и глагол-просьба («кыргызский рынок», «продай по-русски» — не команды).
  // ---------------------------------------------------------------------------------------------------------------

  static const _map = <String, String>{
    'а': 'a', 'б': 'b', 'в': 'v', 'г': 'g', 'д': 'd', 'е': 'e', 'ё': 'e', 'ж': 'j', 'з': 'z', 'и': 'i', 'й': 'i', 'к': 'k', 'л': 'l', 'м': 'm', //
    'н': 'n', 'о': 'o', 'п': 'p', 'р': 'r', 'с': 's', 'т': 't', 'у': 'u', 'ф': 'f', 'х': 'h', 'ц': 'c', 'ч': 'c', 'ш': 's', 'щ': 's', //
    'ъ': '', 'ы': 'i', 'ь': '', 'э': 'e', 'ю': 'u', 'я': 'a', 'ң': 'n', 'ө': 'o', 'ү': 'u', 'ә': 'a', 'қ': 'k', 'ғ': 'g', 'ҳ': 'h', 'ў': 'u', //
    'q': 'k', 'w': 'v', 'x': 'h', 'y': 'i', 'ı': 'i', 'ö': 'o', 'ü': 'u', 'ş': 's', 'ğ': 'g', 'ç': 'c', 'ʻ': '', '’': '', "'": '', '`': '', 'ʼ': '',
  };

  /// «Скелет» текста: слова через пробел, буквы сведены к латинскому минимуму (ы/y/и → i, ч/ch/ц → c, ш/sh → s, к/q → k и т.д.).
  static String skeleton(String text) {
    final b = StringBuffer();
    final plain = RegExp(r'[a-z0-9]');
    for (final r in text.toLowerCase().runes) {
      final ch = String.fromCharCode(r);
      final m = _map[ch];
      if (m != null) {
        b.write(m);
      } else if (plain.hasMatch(ch)) {
        b.write(ch);
      } else {
        b.write(' ');
      }
    }
    var s = b.toString().replaceAll('sh', 's').replaceAll('ch', 'c').replaceAll('ts', 'c').replaceAll('zh', 'j');
    s = s.replaceAllMapped(RegExp(r'(.)\1+'), (m) => m[1]!); // удвоенные буквы
    return s.replaceAll(RegExp(r'\s+'), ' ').trim();
  }

  static bool _isKy(String t) => t.contains('kirg') && !RegExp(r'kirg\w*stan').hasMatch(t); // «Кыргызстан» — страна, не язык
  static final _ru = RegExp(r'^(rus|orus|oruc)');
  static final _en = RegExp(r'^(angl|ingl|engl|ingil)');
  static final _tr = RegExp(r'^(turk|turec|turet|turck)');
  static final _uz = RegExp(r'^(uzbek|ozbek|uzbec|uzbk)');

  // Скелет сводит удвоенные буквы («жооп» → jop, «программу» → programu). Глаголы-просьбы в скелете. Русские — только повелительное наклонение целиком («говорил», «говорят» — не команда); кыргызские, узбекские,
  // турецкие и английские — по началу слова (там формы безопаснее). «Говорить»: речь/ответ — меняет язык ИИ; «перевести»: интерфейс (и ИИ).
  static const _speakExact = {'govori', 'govorite', 'otvecai', 'otvecaite', 'otvet', 'otvette', 'razgovarivai', 'razgovarivaite', 'obsaisia', 'obsaitesi'};
  static const _speakStarts = ['suilo', 'sojlo', 'soilo', 'jop', 'speak', 'talk', 'answer', 'reply', 'konus', 'gapir', 'javob'];
  // 2026-11-05: «поменяй» в скелете даёт pomenai (я → a, й → i), а не pomeniai — команда «поменяй язык на кыргызский» не узнавалась.
  static const _switchExact = {
    'perehodi', 'perehodite', 'pereidi', 'pereidite', 'perekluci', 'pereklucite', 'smeni', 'smenite', 'pomenai', 'pomenaite', 'pomeniai', 'pomeniaite', 'postav', 'postavte', 'vkluci', 'vklucite', 'davai', 'davaite', 'ot', //
  };
  static const _switchStarts = ['kotorul', 'kotoru', 'almastir', 'ozgort', 'otun', 'otus', 'switch', 'change', 'tuzul', 'cevir', 'ozgar', 'kocir'];
  // «переведи» — команда только как «переведи на …/переведи интерфейс …» (а не «переведи привет на кыргызский»).
  static const _translateNext = {'na', 'vse', 'eto', 'interfeis', 'programu', 'prilojenie', 'tirkemeni', 'menia', 'nas'};
  static final _interfaceWords = RegExp(r'interfeis|program|prilojen|prilozen|tirkeme|ilova|interface|application|arayuz');

  /// Узнать команду смены языка. null — это не команда.
  static LangCommand? parse(String text) {
    final sk = skeleton(text);
    if (sk.isEmpty) return null;
    final tokens = sk.split(' ');
    if (tokens.length > 10) return null; // длинная фраза — это рассказ, не команда
    final found = <String>{};
    for (final t in tokens) {
      if (_isKy(t)) found.add('ky');
      if (_ru.hasMatch(t)) found.add('ru');
      if (_en.hasMatch(t)) found.add('en');
      if (_tr.hasMatch(t)) found.add('tr');
      if (_uz.hasMatch(t)) found.add('uz');
    }
    if (found.length != 1) return null; // нет языка или два («с русского на кыргызский») — не угадываем
    final code = found.first;
    final speak = tokens.any((t) => _speakExact.contains(t) || _speakStarts.any(t.startsWith)) || _hasJoopBer(tokens);
    var change = tokens.any((t) => _switchExact.contains(t) || _switchStarts.any(t.startsWith));
    for (var i = 0; i < tokens.length; i++) {
      if ((tokens[i] == 'perevedi' || tokens[i] == 'perevedite') && (i + 1 >= tokens.length || _translateNext.contains(tokens[i + 1]) || _interfaceWords.hasMatch(tokens[i + 1]))) {
        change = true;
      }
    }
    if (!speak && !change) return null;
    if (_interfaceWords.hasMatch(sk)) return LangCommand(code, true);
    if (speak && !change) return LangCommand(code, false); // «…жооп бер / сүйлө / говори / отвечай» — только язык ИИ
    if (change && !speak) return LangCommand(code, true); // «переведи / переключи / смени» — интерфейс и ИИ (сомнительное — оба)
    return LangCommand(code, false);
  }

  static bool _hasJoopBer(List<String> t) {
    for (var i = 0; i < t.length - 1; i++) {
      if (t[i].startsWith('jop') && t[i + 1].startsWith('ber')) return true;
    }
    return false;
  }
}
