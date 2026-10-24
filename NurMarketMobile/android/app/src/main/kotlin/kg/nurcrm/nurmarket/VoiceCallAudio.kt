package kg.nurcrm.nurmarket

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.WindowManager
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.embedding.engine.plugins.activity.ActivityAware
import io.flutter.embedding.engine.plugins.activity.ActivityPluginBinding
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodChannel
import io.flutter.plugin.common.PluginRegistry
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

// 2026-10-22 (звонок ИИ мобилки): владелец — «и голосовой звонок в ИИ в мобилке тоже добавь!». Звук звонка с Нур Советником
// без новых зависимостей: микрофон — AudioRecord (16 кГц, 16 бит, моно — формат Gemini Live), источник VOICE_COMMUNICATION
// плюс подавление эха и шума телефона (ИИ не слышит сам себя из динамика, его можно перебить голосом); ответ — AudioTrack
// (24 кГц, 16 бит, моно — формат ответа Gemini Live) с назначением «голосовая связь». Громкая связь, экран не гаснет во
// время звонка, фокус звука: входящий звонок или другое приложение со звуком — Dart получает «focusLoss» и завершает звонок.
// Каналы: kg.nurcrm.nurmarket/voice (команды) и kg.nurcrm.nurmarket/voice/events (куски PCM с микрофона и события).
class VoiceCallAudio : FlutterPlugin, ActivityAware, PluginRegistry.RequestPermissionsResultListener {
    companion object {
        private const val CHANNEL = "kg.nurcrm.nurmarket/voice"
        private const val EVENTS = "kg.nurcrm.nurmarket/voice/events"
        private const val REQ_MIC = 7201
        private const val MIC_RATE = 16000
        private const val OUT_RATE = 24000
    }

    private val main = Handler(Looper.getMainLooper())
    private var context: Context? = null
    private var activity: Activity? = null
    private var binding: ActivityPluginBinding? = null
    private var methods: MethodChannel? = null
    private var events: EventChannel? = null
    private var sink: EventChannel.EventSink? = null
    private var permissionResult: MethodChannel.Result? = null

    @Volatile private var running = false
    @Volatile private var muted = false
    private var record: AudioRecord? = null
    private var track: AudioTrack? = null
    private var echo: AcousticEchoCanceler? = null
    private var noise: NoiseSuppressor? = null
    private var micThread: Thread? = null
    private var outThread: Thread? = null
    private val outQueue = LinkedBlockingQueue<ByteArray>()
    private var focus: AudioFocusRequest? = null
    private var oldMode = AudioManager.MODE_NORMAL

    override fun onAttachedToEngine(b: FlutterPlugin.FlutterPluginBinding) {
        context = b.applicationContext
        methods = MethodChannel(b.binaryMessenger, CHANNEL).also { ch ->
            ch.setMethodCallHandler { call, result ->
                try {
                    when (call.method) {
                        "micPermission" -> result.success(micState())
                        "requestMic" -> requestMic(result)
                        "openSettings" -> {
                            val ctx = activity ?: context
                            ctx?.startActivity(
                                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + ctx.packageName))
                                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                            )
                            result.success(true)
                        }
                        "start" -> result.success(start(call.argument<Boolean>("speaker") != false))
                        "play" -> {
                            val pcm = call.arguments as? ByteArray
                            if (pcm != null && running) outQueue.offer(pcm)
                            result.success(null)
                        }
                        "flush" -> {
                            flush()
                            result.success(null)
                        }
                        "mute" -> {
                            muted = call.argument<Boolean>("muted") == true
                            result.success(null)
                        }
                        "speaker" -> {
                            setSpeaker(call.argument<Boolean>("on") != false)
                            result.success(null)
                        }
                        "stop" -> {
                            stop()
                            result.success(null)
                        }
                        else -> result.notImplemented()
                    }
                } catch (e: Exception) {
                    result.error("voice", e.message, null)
                }
            }
        }
        events = EventChannel(b.binaryMessenger, EVENTS).also { ch ->
            ch.setStreamHandler(object : EventChannel.StreamHandler {
                override fun onListen(arguments: Any?, s: EventChannel.EventSink?) {
                    sink = s
                }

                override fun onCancel(arguments: Any?) {
                    sink = null
                }
            })
        }
    }

    override fun onDetachedFromEngine(b: FlutterPlugin.FlutterPluginBinding) {
        stop()
        methods?.setMethodCallHandler(null)
        events?.setStreamHandler(null)
        context = null
    }

    override fun onAttachedToActivity(b: ActivityPluginBinding) {
        activity = b.activity
        binding = b
        b.addRequestPermissionsResultListener(this)
    }

    override fun onDetachedFromActivityForConfigChanges() = onDetachedFromActivity()

    override fun onReattachedToActivityForConfigChanges(b: ActivityPluginBinding) = onAttachedToActivity(b)

    override fun onDetachedFromActivity() {
        binding?.removeRequestPermissionsResultListener(this)
        binding = null
        activity = null
    }

    /// granted — можно; denied — можно спросить; blocked — отказано навсегда («Больше не спрашивать»), только настройки.
    private fun micState(): String {
        val ctx = activity ?: context ?: return "denied"
        if (ctx.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) return "granted"
        val prefs = ctx.getSharedPreferences("voice_call", Context.MODE_PRIVATE)
        val asked = prefs.getBoolean("asked", false)
        val a = activity
        return if (asked && a != null && !a.shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO)) "blocked" else "denied"
    }

    private fun requestMic(result: MethodChannel.Result) {
        val a = activity
        if (a == null) {
            result.success(micState())
            return
        }
        if (micState() == "granted") {
            result.success("granted")
            return
        }
        permissionResult?.success(micState())
        permissionResult = result
        a.getSharedPreferences("voice_call", Context.MODE_PRIVATE).edit().putBoolean("asked", true).apply()
        a.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQ_MIC)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray): Boolean {
        if (requestCode != REQ_MIC) return false
        val r = permissionResult
        permissionResult = null
        r?.success(if (grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED) "granted" else micState())
        return true
    }

    private fun audio(): AudioManager? = context?.getSystemService(Context.AUDIO_SERVICE) as? AudioManager

    private fun emit(value: Any) = main.post { sink?.success(value) }

    @SuppressLint("MissingPermission") // разрешение проверено в начале функции
    private fun start(speaker: Boolean): Boolean {
        if (running) return true
        val ctx = context ?: return false
        if (ctx.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return false
        val am = audio()
        oldMode = am?.mode ?: AudioManager.MODE_NORMAL
        val attrs = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
            .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
            .build()
        if (am != null) {
            // Входящий звонок или другое приложение со звуком забирает фокус — Dart завершает разговор.
            val request = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                .setAudioAttributes(attrs)
                .setOnAudioFocusChangeListener { change ->
                    if (change == AudioManager.AUDIOFOCUS_LOSS || change == AudioManager.AUDIOFOCUS_LOSS_TRANSIENT) {
                        emit(mapOf("event" to "focusLoss"))
                    }
                }
                .build()
            focus = request
            am.requestAudioFocus(request)
            am.mode = AudioManager.MODE_IN_COMMUNICATION
        }
        setSpeaker(speaker)

        val micMin = AudioRecord.getMinBufferSize(MIC_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val rec = AudioRecord(
            MediaRecorder.AudioSource.VOICE_COMMUNICATION, MIC_RATE, AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT, maxOf(micMin, MIC_RATE * 2 / 5),
        )
        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            rec.release()
            restoreAudio()
            return false
        }
        if (AcousticEchoCanceler.isAvailable()) echo = AcousticEchoCanceler.create(rec.audioSessionId)?.apply { enabled = true }
        if (NoiseSuppressor.isAvailable()) noise = NoiseSuppressor.create(rec.audioSessionId)?.apply { enabled = true }

        val outMin = AudioTrack.getMinBufferSize(OUT_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val out = AudioTrack.Builder()
            .setAudioAttributes(attrs)
            .setAudioFormat(
                AudioFormat.Builder().setSampleRate(OUT_RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build(),
            )
            .setBufferSizeInBytes(maxOf(outMin, OUT_RATE * 2 / 5))
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        record = rec
        track = out
        outQueue.clear()
        running = true
        muted = false
        rec.startRecording()
        out.play()
        activity?.runOnUiThread { activity?.window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }

        micThread = Thread {
            val buf = ByteArray(MIC_RATE * 2 / 10) // 100 мс
            while (running) {
                val n = rec.read(buf, 0, buf.size)
                if (n > 0 && !muted) emit(buf.copyOf(n))
            }
        }.apply { name = "nur-voice-mic"; start() }
        outThread = Thread {
            while (running) {
                val chunk = try {
                    outQueue.poll(200, TimeUnit.MILLISECONDS)
                } catch (_: InterruptedException) {
                    null
                } ?: continue
                try {
                    out.write(chunk, 0, chunk.size)
                } catch (_: Exception) {
                    // звук прервали (flush/stop) — следующий кусок
                }
            }
        }.apply { name = "nur-voice-out"; start() }
        return true
    }

    /// Перебивание: всё, что ИИ ещё не договорил, — выбросить сразу.
    private fun flush() {
        outQueue.clear()
        track?.let {
            try {
                it.pause()
                it.flush()
                it.play()
            } catch (_: Exception) {
            }
        }
    }

    private fun setSpeaker(on: Boolean) {
        val am = audio() ?: return
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                if (on) {
                    am.availableCommunicationDevices.firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
                        ?.let { am.setCommunicationDevice(it) }
                } else {
                    am.clearCommunicationDevice()
                }
            } else {
                @Suppress("DEPRECATION")
                am.isSpeakerphoneOn = on
            }
        } catch (_: Exception) {
            // устройство не поддерживает выбор — звук остаётся, где был
        }
    }

    private fun restoreAudio() {
        val am = audio() ?: return
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                am.clearCommunicationDevice()
            } else {
                @Suppress("DEPRECATION")
                am.isSpeakerphoneOn = false
            }
            am.mode = oldMode
            focus?.let { am.abandonAudioFocusRequest(it) }
        } catch (_: Exception) {
        }
        focus = null
    }

    private fun stop() {
        if (!running && record == null) return
        running = false
        micThread?.join(500)
        outThread?.join(500)
        micThread = null
        outThread = null
        try {
            record?.stop()
        } catch (_: Exception) {
        }
        record?.release()
        record = null
        echo?.release()
        noise?.release()
        echo = null
        noise = null
        try {
            track?.stop()
        } catch (_: Exception) {
        }
        track?.release()
        track = null
        outQueue.clear()
        restoreAudio()
        activity?.runOnUiThread { activity?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }
    }
}
