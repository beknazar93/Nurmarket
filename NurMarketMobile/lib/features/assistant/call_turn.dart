/// Реплика звонка: [text] — как сказано (с кодами [Д1] — так она уходит ИИ); table — таблица с цифрами по вопросу.
/// 2026-10-26 (голос-управление, В4): [display] — готовый текст для экрана (таблица с именами, подставленными на телефоне);
/// null — экран сам заменит коды ([Д1]) в [text]. Имена в [text] не попадают: чат потом уходит в Google целиком.
class CallTurn {
  final bool user;
  final String text;
  final bool table;
  final String? display;
  const CallTurn(this.user, this.text, {this.table = false, this.display});
}
