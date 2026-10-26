// 2026-10-26 (видеозвонок, В2 + голос-управление, В4): видео-часть экрана «Разговор» по макету Mobile-Call-Video — предпросмотр с
// подписями «● Камера» и «ИИ видит / Пауза — ИИ не видит», кнопки «Другая камера» и «Фонарик» (48 dp), круг состояния 56 dp
// в углу кадра, быстрые кнопки «Что это? / Накладная / Полка / Ценник», карточки результата (Товар, Похоже на, Накладная,
// Полка, Ценник). Подписи поверх кадра — всегда тёмная подложка и белый текст (в обеих темах). Ничего не проводится.
import 'package:camera/camera.dart' show CameraLensDirection, CameraPreview;
import 'package:flutter/material.dart';

import '../../core/ai/live_video.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'call_controller.dart';
import 'call_video_bridge.dart';

const _camLabel = Color(0x9E000000); // rgba(0,0,0,.62)

class _CamPill extends StatelessWidget {
  final String text;
  final Color? dot;
  const _CamPill(this.text, {this.dot});

  @override
  Widget build(BuildContext context) => Container(
    constraints: const BoxConstraints(minHeight: 28),
    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
    decoration: BoxDecoration(color: _camLabel, borderRadius: BorderRadius.circular(14)),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (dot != null) ...[Container(width: 8, height: 8, decoration: BoxDecoration(shape: BoxShape.circle, color: dot)), const SizedBox(width: 6)],
        Flexible(child: Text(text, style: const TextStyle(color: Colors.white, fontSize: 13, fontWeight: FontWeight.w700, height: 1.25))),
      ],
    ),
  );
}

class _RoundCamBtn extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool on;
  final VoidCallback onTap;
  const _RoundCamBtn({required this.icon, required this.label, required this.onTap, this.on = false});

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    label: label,
    excludeSemantics: true,
    child: GestureDetector(
      onTap: onTap,
      child: Container(
        width: 48,
        height: 48,
        decoration: BoxDecoration(shape: BoxShape.circle, color: on ? T.accent : _camLabel),
        child: Icon(icon, size: 22, color: on ? T.navy : Colors.white),
      ),
    ),
  );
}

/// Предпросмотр камеры. [height] — высота (в вертикальном виде 3:4, но не выше 46 % экрана).
class CallCamView extends StatelessWidget {
  final AssistantCallController c;
  final double? height;
  final String lang;
  final Widget orb;
  const CallCamView({super.key, required this.c, required this.lang, required this.orb, this.height});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final v = c.video!;
    final st = v.state.value;
    final phase = st.phase;
    final live = phase == VideoPhase.live || phase == VideoPhase.pausedSilence || phase == VideoPhase.pausedSystem || phase == VideoPhase.shooting || phase == VideoPhase.switching;
    final sees = v.aiSees;
    final frames = c.frames;
    final ctl = frames?.controller;
    final last = frames?.lastFrame;
    Widget picture;
    if ((phase == VideoPhase.switching || phase == VideoPhase.shooting && ctl == null) && last != null) {
      picture = ColorFiltered(colorFilter: const ColorFilter.mode(Color(0x66000000), BlendMode.darken), child: Image.memory(last, fit: BoxFit.cover, gaplessPlayback: true));
    } else if (ctl != null && ctl.value.isInitialized && live) {
      picture = FittedBox(fit: BoxFit.cover, child: SizedBox(width: ctl.value.previewSize?.height ?? 480, height: ctl.value.previewSize?.width ?? 640, child: CameraPreview(ctl)));
    } else {
      picture = const DecoratedBox(decoration: BoxDecoration(gradient: LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0xFF3B3128), Color(0xFF2A231C)])));
    }
    final center = switch (phase) {
      VideoPhase.starting => (Icons.hourglass_empty_rounded, l.vcStarting, true),
      VideoPhase.shooting => (null, videoMsg('shooting', lang), false),
      VideoPhase.switching => (null, l.vcSwitchCamera, true),
      _ => c.videoBusy == VideoAsk.product || c.videoBusy != null ? (Icons.search_rounded, l.vcSearching, false) : null,
    };
    final bottomHint = switch (phase) {
      VideoPhase.pausedSilence => videoMsg('pausedSilence', lang),
      _ => c.economy ? l.vcEconomyHint : null,
    };
    return ClipRRect(
      borderRadius: BorderRadius.circular(T.radiusCard),
      child: SizedBox(
        height: height,
        child: Stack(
          fit: StackFit.expand,
          children: [
            picture,
            if (phase == VideoPhase.shooting)
              IgnorePointer(
                child: TweenAnimationBuilder<double>(
                  tween: Tween(begin: .9, end: 0),
                  duration: const Duration(milliseconds: 180),
                  builder: (_, o, _) => ColoredBox(color: Colors.white.withValues(alpha: o)),
                ),
              ),
            Positioned(left: 12, top: 12, child: _CamPill(l.vcRecording, dot: const Color(0xFFFB2C36))),
            Positioned(
              left: 12,
              top: 48,
              child: sees && phase == VideoPhase.live
                  ? _CamPill(l.vcAiSees, dot: const Color(0xFF4ADE80))
                  : _CamPill(videoMsg('paused', lang), dot: const Color(0xFFAAAAAA)),
            ),
            if (live) ...[
              if (v.canSwitch) Positioned(right: 12, top: 12, child: _RoundCamBtn(icon: Icons.flip_camera_android_rounded, label: l.vcSwitchCamera, onTap: c.switchCamera)),
              if (v.lens == CameraLensDirection.back) Positioned(right: 12, top: v.canSwitch ? 68 : 12, child: _RoundCamBtn(icon: v.torch ? Icons.flashlight_on_rounded : Icons.flashlight_off_rounded, label: l.vcTorch, on: v.torch, onTap: c.toggleTorch)),
            ],
            Positioned(right: 12, bottom: 12, child: SizedBox(width: 56, height: 56, child: orb)),
            if (center != null)
              Positioned.fill(
                child: Center(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 24),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (center.$3) const SizedBox(width: 28, height: 28, child: CircularProgressIndicator(strokeWidth: 3, color: Colors.white)),
                        if (center.$3) const SizedBox(height: 10),
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                          decoration: BoxDecoration(color: _camLabel, borderRadius: BorderRadius.circular(12)),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              if (center.$1 != null) ...[Icon(center.$1, size: 16, color: Colors.white), const SizedBox(width: 6)],
                              Flexible(child: Text(center.$2, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white, fontSize: 13, fontWeight: FontWeight.w700, height: 1.3))),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            if (bottomHint != null)
              Positioned(
                left: 12,
                right: 72,
                bottom: 12,
                child: Align(alignment: Alignment.bottomLeft, child: _CamPill(bottomHint)),
              ),
          ],
        ),
      ),
    );
  }
}

/// Быстрые кнопки под предпросмотром (48 dp, ряд прокручивается; «Полка» — только с правом «Склад»).
class QuickAskButtons extends StatelessWidget {
  final AssistantCallController c;
  final bool grid;
  const QuickAskButtons({super.key, required this.c, this.grid = false});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final v = c.video;
    final phase = v?.state.value.phase;
    final off = c.videoBusy != null || phase == VideoPhase.starting || phase == VideoPhase.shooting || phase == VideoPhase.switching;
    Widget btn(VideoAsk ask, IconData icon, String text) {
      final active = c.videoBusy == ask;
      return OutlinedButton.icon(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size(0, 48),
          padding: const EdgeInsets.symmetric(horizontal: 14),
          backgroundColor: active ? pal.action : null,
          foregroundColor: active ? pal.onAction : pal.text,
          side: BorderSide(color: active ? pal.action : pal.border, width: 1.5),
          textStyle: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700),
        ),
        onPressed: off ? null : () => c.askVideo(ask),
        icon: active ? SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2.5, color: pal.onAction)) : Icon(icon, size: 18),
        label: Text(ask == VideoAsk.product && active ? l.vcSearching : text, maxLines: 1, overflow: TextOverflow.ellipsis),
      );
    }

    final items = [
      btn(VideoAsk.product, Icons.search_rounded, l.vcWhatIsIt),
      btn(VideoAsk.invoice, Icons.description_outlined, l.vcInvoice),
      if (c.canStock) btn(VideoAsk.shelf, Icons.inventory_2_outlined, l.vcShelf),
      btn(VideoAsk.label, Icons.payments_outlined, l.vcPriceTag),
    ];
    if (grid) {
      return GridView.count(
        shrinkWrap: true,
        physics: const NeverScrollableScrollPhysics(),
        crossAxisCount: 2,
        mainAxisSpacing: 8,
        crossAxisSpacing: 8,
        childAspectRatio: 2.6,
        children: items,
      );
    }
    return SizedBox(
      height: 48,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(children: [for (final b in items) Padding(padding: const EdgeInsets.only(right: 8), child: b)]),
      ),
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Карточка результата
// ---------------------------------------------------------------------------------------------------------------------

/// Карточка выезжает снизу над рядом кнопок; ✕ закрывает; новая заменяет старую.
class VideoResultCardView extends StatelessWidget {
  final AssistantCallController c;
  final VideoCard card;
  const VideoResultCardView({super.key, required this.c, required this.card});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final title = switch (card) {
      VideoInvoiceCard() => l.vcInvoice,
      VideoShelfCard() => l.vcShelf,
      VideoTagCard() => l.vcPriceTag,
      _ => l.vcCardProduct,
    };
    final body = switch (card) {
      final VideoProductCard p => _product(context, p),
      final VideoInvoiceCard i => _invoice(context, i),
      final VideoShelfCard s => _shelf(context, s),
      final VideoTagCard t => _tag(context, t),
      final VideoErrorCard e => _error(context, e),
    };
    return Material(
      color: pal.panel,
      elevation: 6,
      shadowColor: const Color(0x40191919),
      borderRadius: BorderRadius.circular(T.radiusCard),
      child: Container(
        decoration: BoxDecoration(borderRadius: BorderRadius.circular(T.radiusCard), border: Border.all(color: pal.border)),
        padding: const EdgeInsets.fromLTRB(14, 8, 8, 12),
        child: ConstrainedBox(
          constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * .6),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    Expanded(child: Text(title, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800))),
                    IconButton(onPressed: c.closeVideoCard, tooltip: l.tblClose, icon: const Icon(Icons.close_rounded)),
                  ],
                ),
                body,
              ],
            ),
          ),
        ),
      ),
    );
  }

  static String _n(double v) => v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2).replaceAll('.', ',');

  Widget _product(BuildContext context, VideoProductCard p) {
    final l = context.l;
    final pal = context.pal;
    if (p.notFound) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Container(
            padding: const EdgeInsets.all(12),
            margin: const EdgeInsets.only(right: 6),
            decoration: BoxDecoration(color: pal.warnSoft, borderRadius: BorderRadius.circular(T.radiusCard)),
            child: Row(
              children: [
                Icon(Icons.search_rounded, size: 22, color: pal.warn),
                const SizedBox(width: 10),
                Expanded(child: Text(l.vcNotFound, style: const TextStyle(fontSize: 13, height: 1.3))),
              ],
            ),
          ),
          Align(
            alignment: Alignment.centerRight,
            child: Padding(
              padding: const EdgeInsets.only(top: 6, right: 6),
              child: OutlinedButton(style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)), onPressed: c.lastAsk == null ? null : () => c.askVideo(c.lastAsk!), child: Text(l.retry)),
            ),
          ),
        ],
      );
    }
    if (p.looksLike) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(l.vcLooksLike, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w800)),
          Text(l.vcPickHint, style: TextStyle(fontSize: 12, color: pal.textSoft)),
          const SizedBox(height: 8),
          for (final a in p.alternatives)
            Padding(
              padding: const EdgeInsets.only(bottom: 6, right: 6),
              child: OutlinedButton(
                style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48), padding: const EdgeInsets.symmetric(horizontal: 12), alignment: Alignment.centerLeft, foregroundColor: pal.text),
                onPressed: () => c.chooseLooksLike(a),
                child: Row(
                  children: [
                    Expanded(child: Text(a.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w600))),
                    Text(l.vcPrice(_n(a.price)), style: const TextStyle(fontWeight: FontWeight.w800, fontFeatures: [FontFeature.tabularFigures()])),
                  ],
                ),
              ),
            ),
        ],
      );
    }
    return Padding(
      padding: const EdgeInsets.only(right: 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              ProductThumb(name: p.name, url: p.product?.imageUrl, size: 56, radius: 12),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(p.name, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w700, height: 1.2)),
                    const SizedBox(height: 4),
                    Text(l.vcPrice(_n(p.price)), style: TextStyle(fontSize: 22, fontWeight: FontWeight.w800, color: pal.price, height: 1.1, fontFeatures: const [FontFeature.tabularFigures()])),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              if (p.stock != null) Expanded(child: Text(l.vcStock('${_n(p.stock!)} ${p.unit}'), style: TextStyle(fontSize: 13, color: pal.textSoft))) else const Spacer(),
              if (p.exactBarcode)
                Container(
                  height: 22,
                  padding: const EdgeInsets.symmetric(horizontal: 8),
                  decoration: BoxDecoration(color: pal.successSoft, borderRadius: BorderRadius.circular(11)),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(Icons.check_rounded, size: 14, color: pal.success),
                      const SizedBox(width: 4),
                      Text('${l.vcExact} · ${l.vcByBarcode}', style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: pal.success)),
                    ],
                  ),
                ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _invoice(BuildContext context, VideoInvoiceCard i) {
    final l = context.l;
    final pal = context.pal;
    return Padding(
      padding: const EdgeInsets.only(right: 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(l.vcInvoiceSummary(i.lines, i.known, i.fresh), style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.text.withValues(alpha: .8))),
          const SizedBox(height: 6),
          for (final r in i.preview.take(5))
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 3),
              child: Row(
                children: [
                  Expanded(
                    child: Row(
                      children: [
                        Flexible(child: Text(r.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 13, color: pal.textSoft))),
                        if (!r.known)
                          Container(
                            margin: const EdgeInsets.only(left: 6),
                            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
                            decoration: BoxDecoration(color: T.brandYellow2, borderRadius: BorderRadius.circular(8)),
                            child: Text(l.vcNewTag, style: const TextStyle(fontSize: 10, fontWeight: FontWeight.w700, color: T.navy)),
                          ),
                      ],
                    ),
                  ),
                  Text('${_n(r.qty)} × ${_n(r.cost)}', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, fontFeatures: [FontFeature.tabularFigures()])),
                ],
              ),
            ),
          if (i.preview.length > 5) Text(l.vcMoreLines(i.preview.length - 5), style: TextStyle(fontSize: 12, color: pal.textSoft)),
          if (i.fresh > 0) Padding(padding: const EdgeInsets.only(top: 4), child: Text(l.vcNewItemsHint, style: TextStyle(fontSize: 12, color: pal.textSoft))),
          const SizedBox(height: 8),
          if (c.canProcure && i.ready.isNotEmpty) ...[
            FilledButton.icon(
              style: FilledButton.styleFrom(
                backgroundColor: T.accent,
                foregroundColor: T.navy,
                minimumSize: const Size.fromHeight(T.ctaHeight),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(T.radiusCta)),
              ),
              onPressed: () => c.openReceive(i.ready),
              icon: const Icon(Icons.inventory_2_outlined, size: 20),
              label: Text(l.vcOpenReceive(i.ready.length), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
            ),
            const SizedBox(height: 4),
            Center(child: Text(l.vcReceiveEndsCall, textAlign: TextAlign.center, style: TextStyle(fontSize: 12, color: pal.textSoft))),
          ] else if (!c.canProcure)
            Text(l.vcNoProcure, style: TextStyle(fontSize: 12, color: pal.textSoft)),
        ],
      ),
    );
  }

  Widget _shelf(BuildContext context, VideoShelfCard s) {
    final l = context.l;
    final pal = context.pal;
    return Padding(
      padding: const EdgeInsets.only(right: 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(child: Text(l.vcShelfThing, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: pal.textSoft))),
              Text(l.vcShelfCols, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: pal.textSoft)),
            ],
          ),
          for (final r in s.rows.take(8))
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 4),
              child: Row(
                children: [
                  Expanded(child: Text(r.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 13))),
                  Text.rich(
                    TextSpan(
                      children: [
                        TextSpan(
                          text: '${r.seen}',
                          style: TextStyle(color: r.have == null || r.have == r.seen ? pal.text : (r.seen > r.have! ? pal.warn : pal.danger)),
                        ),
                        TextSpan(text: ' · ${r.have == null ? '—' : _n(r.have!)}'),
                      ],
                    ),
                    style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, fontFeatures: [FontFeature.tabularFigures()]),
                  ),
                ],
              ),
            ),
          const SizedBox(height: 6),
          Container(
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(color: pal.warnSoft, borderRadius: BorderRadius.circular(T.radiusCard)),
            child: Row(
              children: [
                Icon(Icons.warning_amber_rounded, size: 20, color: pal.warn),
                const SizedBox(width: 8),
                Expanded(child: Text(l.vcShelfHint, style: const TextStyle(fontSize: 13, height: 1.3))),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _tag(BuildContext context, VideoTagCard t) {
    final l = context.l;
    final pal = context.pal;
    Widget box(String label, double v) => Expanded(
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
        decoration: BoxDecoration(color: pal.dark ? const Color(0xFF333333) : T.soft, borderRadius: BorderRadius.circular(T.radiusButton), border: Border.all(color: pal.border)),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(label, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: pal.textSoft)),
            const SizedBox(height: 2),
            Text(l.vcPrice(_n(v)), maxLines: 1, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800, fontFeatures: [FontFeature.tabularFigures()])),
          ],
        ),
      ),
    );
    return Padding(
      padding: const EdgeInsets.only(right: 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(t.name, style: TextStyle(fontSize: 12, color: pal.textSoft)),
          const SizedBox(height: 8),
          Row(
            children: [
              box(l.vcOnTag, t.tag),
              const SizedBox(width: 10),
              Container(
                width: 48,
                height: 48,
                decoration: BoxDecoration(shape: BoxShape.circle, color: t.ok ? pal.successSoft : pal.warnSoft),
                child: Icon(t.ok ? Icons.check_rounded : Icons.warning_amber_rounded, size: 28, color: t.ok ? pal.success : pal.warn),
              ),
              const SizedBox(width: 10),
              box(l.vcInProgram, t.price),
            ],
          ),
          const SizedBox(height: 8),
          Text(
            t.ok ? l.vcTagOk : l.vcTagMismatch(_n(t.tag), _n(t.price)),
            style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: t.ok ? pal.success : pal.warn),
          ),
          if (!t.ok) Padding(padding: const EdgeInsets.only(top: 4), child: Text(l.vcTagFixHint, style: TextStyle(fontSize: 12, color: pal.textSoft))),
        ],
      ),
    );
  }

  Widget _error(BuildContext context, VideoErrorCard e) {
    final l = context.l;
    final pal = context.pal;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Container(
          padding: const EdgeInsets.all(12),
          margin: const EdgeInsets.only(right: 6),
          decoration: BoxDecoration(color: pal.dangerSoft, borderRadius: BorderRadius.circular(T.radiusCard)),
          child: Row(
            children: [
              Icon(Icons.error_outline_rounded, size: 22, color: pal.danger),
              const SizedBox(width: 10),
              Expanded(child: Text(e.text, style: const TextStyle(fontSize: 13, height: 1.3))),
            ],
          ),
        ),
        if (e.retry && c.lastAsk != null)
          Align(
            alignment: Alignment.centerRight,
            child: Padding(
              padding: const EdgeInsets.only(top: 6, right: 6),
              child: OutlinedButton(style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)), onPressed: () => c.askVideo(c.lastAsk!), child: Text(l.retry)),
            ),
          ),
      ],
    );
  }
}
