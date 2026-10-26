// 2026-10-26 (видеозвонок, В3 + голос-управление, В4): согласие на камеру в звонке и настройка «Экономия трафика».
// Согласие запоминается по пользователю и версии текста (settings: advisor.video.consent.<версия>.<userId>); поменяется текст —
// версия v2, и окно покажется снова. «Не сейчас» ничего не запоминает. Окно — как на макете Mobile-Call-Video (consent).
import 'package:flutter/material.dart';

import '../../core/db/database.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';

class VideoConsent {
  /// Версия текста согласия. Текст `vcConsent*` поменялся — поднять версию.
  static const version = 'v1';

  static String key(String userId) => 'advisor.video.consent.$version.$userId';

  static Future<bool> has(AppDatabase db, String userId) async {
    try {
      final v = await db.getSetting(key(userId));
      return v != null && v.isNotEmpty;
    } catch (_) {
      return false;
    }
  }

  static Future<void> accept(AppDatabase db, String userId) async {
    try {
      await db.setSetting(key(userId), DateTime.now().toIso8601String());
    } catch (_) {
      // не записалось — окно покажется ещё раз, камера всё равно включится сейчас
    }
  }
}

/// «Экономия трафика»: 1 кадр в 3 с и только когда человек говорит. Выбор запоминается.
class VideoPrefs {
  static const economyKey = 'advisor.video.economy';

  static Future<bool> economy(AppDatabase db) async {
    try {
      return await db.getSetting(economyKey) == '1';
    } catch (_) {
      return false;
    }
  }

  static Future<void> setEconomy(AppDatabase db, bool on) async {
    try {
      await db.setSetting(economyKey, on ? '1' : '0');
    } catch (_) {}
  }
}

/// Окно согласия. `showDialog<bool>`: true — «Включить камеру», false/null — «Не сейчас».
class VideoConsentDialog extends StatelessWidget {
  const VideoConsentDialog({super.key});

  static Future<bool> ask(BuildContext context) async =>
      await showDialog<bool>(context: context, barrierDismissible: true, builder: (_) => const VideoConsentDialog()) ?? false;

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    Widget point(IconData icon, String text, {bool bad = false}) => Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(padding: const EdgeInsets.only(top: 1), child: Icon(icon, size: 20, color: bad ? pal.danger : pal.text)),
          const SizedBox(width: 10),
          Expanded(
            child: Text(text, style: TextStyle(fontSize: 13, height: 1.4, color: pal.text.withValues(alpha: .86))),
          ),
        ],
      ),
    );
    return Dialog(
      backgroundColor: pal.panel,
      insetPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 24),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(16, 20, 16, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l.vcConsentTitle, style: const TextStyle(fontSize: 19, fontWeight: FontWeight.w800, height: 1.2)),
            const SizedBox(height: 12),
            // Семантика одним куском: полный текст согласия (vcConsentBody) читает экранный диктор, значки — для глаз.
            Semantics(
              label: l.vcConsentBody,
              excludeSemantics: true,
              child: Column(
                children: [
                  point(Icons.photo_camera_outlined, l.vcConsentPtSends),
                  point(Icons.lock_outline_rounded, l.vcConsentPtSaved),
                  point(Icons.cloud_outlined, l.vcConsentPtFree),
                  point(Icons.credit_card_off_outlined, l.vcConsentPtWarn, bad: true),
                ],
              ),
            ),
            const SizedBox(height: 8),
            FilledButton.icon(
              style: FilledButton.styleFrom(
                backgroundColor: T.accent,
                foregroundColor: T.navy,
                minimumSize: const Size.fromHeight(T.ctaHeight),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(T.radiusCta)),
              ),
              onPressed: () => Navigator.of(context).pop(true),
              icon: const Icon(Icons.photo_camera_rounded, size: 22),
              label: Text(l.vcConsentAgree, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
            ),
            const SizedBox(height: 8),
            OutlinedButton(
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52)),
              onPressed: () => Navigator.of(context).pop(false),
              child: Text(l.vcConsentLater),
            ),
          ],
        ),
      ),
    );
  }
}
