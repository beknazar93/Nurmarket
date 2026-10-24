package kg.nurcrm.nurmarket

import android.Manifest
import android.app.PendingIntent
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbEndpoint
import android.hardware.usb.UsbInterface
import android.hardware.usb.UsbManager
import android.media.AudioManager
import android.media.ToneGenerator
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.io.IOException
import java.util.UUID
import java.util.concurrent.Executors

// 2026-10-20, владелец: «в мобилке исправь подключение к принтерам». Печати на телефоне не было совсем.
// Канал kg.nurcrm.nurmarket/printer: Bluetooth (классический SPP — так работают почти все чековые ESC/POS-принтеры)
// и USB-OTG (UsbManager, класс принтера 7 или любой интерфейс с выходом bulk). Без новых зависимостей и без androidx.
// Коды ошибок совпадают с PrinterProblem в lib/core/print/printer_service.dart.
class MainActivity : FlutterActivity() {
    private val io = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var permissionResult: MethodChannel.Result? = null

    // 2026-10-22 (мобилка, админ): П6 — одно окно разрешения USB за раз (раньше каждое нажатие «Печать» регистрировало
    // свой приёмник, и после «Разрешить» чек печатался столько раз, сколько нажали).
    private var usbPermissionPending = false

    // 2026-10-22 (мобилка, админ): П6 — «бип» сканера (ToneGenerator, без новых зависимостей); создаётся при первом звуке.
    private var tone: ToneGenerator? = null

    companion object {
        private const val CHANNEL = "kg.nurcrm.nurmarket/printer"
        private const val BEEP_CHANNEL = "kg.nurcrm.nurmarket/beep"
        private val SPP: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
        private const val REQ_BT = 7101
        private const val USB_PERMISSION = "kg.nurcrm.nurmarket.USB_PERMISSION"
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        // 2026-10-22 (звонок ИИ мобилки): микрофон и звук голосового звонка с Нур Советником (VoiceCallAudio.kt).
        flutterEngine.plugins.add(VoiceCallAudio())
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, CHANNEL).setMethodCallHandler { call, result ->
            try {
                handle(call, result)
            } catch (e: Exception) {
                result.error("io", e.message, null)
            }
        }
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, BEEP_CHANNEL).setMethodCallHandler { call, result ->
            if (call.method == "beep") {
                beep(call.argument<String>("kind") ?: "ok")
                result.success(null)
            } else {
                result.notImplemented()
            }
        }
    }

    /// 2026-10-22 (мобилка, админ): П6 — короткий «бип» удачного скана, двойной низкий — ошибка (товар не найден).
    /// Поток музыки: звук слышен и в беззвучном режиме звонка, громкость — кнопками телефона.
    private fun beep(kind: String) {
        try {
            val t = tone ?: ToneGenerator(AudioManager.STREAM_MUSIC, 80).also { tone = it }
            if (kind == "error") t.startTone(ToneGenerator.TONE_PROP_NACK, 250) else t.startTone(ToneGenerator.TONE_PROP_BEEP, 120)
        } catch (_: RuntimeException) {
            // нет звукового устройства или занято — без звука
            tone = null
        }
    }

    override fun onDestroy() {
        try {
            tone?.release()
        } catch (_: Exception) {
        }
        tone = null
        super.onDestroy()
    }

    private fun adapter(): BluetoothAdapter? = (getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    private fun has(p: String) = checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED

    /// Android 12+: «Устройства поблизости» (BLUETOOTH_CONNECT, для поиска — ещё BLUETOOTH_SCAN).
    /// До Android 12 сопряжённые устройства доступны без запроса, поиск новых требует геолокацию.
    private fun btPermitted(scan: Boolean): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            has(Manifest.permission.BLUETOOTH_CONNECT) && (!scan || has(Manifest.permission.BLUETOOTH_SCAN))
        } else {
            !scan || has(Manifest.permission.ACCESS_FINE_LOCATION)
        }

    private fun handle(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "btState" -> {
                val a = adapter()
                result.success(mapOf("supported" to (a != null), "enabled" to (a?.isEnabled == true), "permission" to btPermitted(false)))
            }
            "btRequestPermission" -> {
                val scan = call.argument<Boolean>("scan") == true
                if (btPermitted(scan)) {
                    result.success(true)
                    return
                }
                val perms = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                    arrayOf(Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.BLUETOOTH_SCAN)
                } else {
                    arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
                }
                permissionResult?.success(false)
                permissionResult = result
                requestPermissions(perms, REQ_BT)
            }
            "btEnable" -> {
                if (!btPermitted(false)) {
                    result.error("noPermission", null, null)
                    return
                }
                startActivity(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE))
                result.success(null)
            }
            "btSettings" -> {
                startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                result.success(null)
            }
            "btDevices" -> btDevices(call.argument<Boolean>("discover") == true, result)
            "btPrint" -> {
                val address = call.argument<String>("address") ?: ""
                val data = call.argument<ByteArray>("data") ?: ByteArray(0)
                val a = adapter() ?: return result.error("btUnsupported", null, null)
                if (!btPermitted(false)) return result.error("noPermission", null, null)
                if (!a.isEnabled) return result.error("btOff", null, null)
                io.execute {
                    try {
                        btPrint(a, address, data)
                        main.post { result.success(null) }
                    } catch (e: PrinterError) {
                        main.post { result.error(e.code, e.message, if (e.sent) "sent" else null) }
                    } catch (e: Exception) {
                        main.post { result.error("io", e.message, null) }
                    }
                }
            }
            "usbDevices" -> {
                val m = getSystemService(Context.USB_SERVICE) as UsbManager
                result.success(m.deviceList.values.map { d ->
                    mapOf(
                        "device" to d.deviceName,
                        "name" to usbName(d),
                        "printer" to (printerInterface(d) != null),
                    )
                })
            }
            "usbPrint" -> usbPrint(call.argument<String>("device") ?: "", call.argument<ByteArray>("data") ?: ByteArray(0), result)
            else -> result.notImplemented()
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_BT) {
            val r = permissionResult
            permissionResult = null
            r?.success(grantResults.isNotEmpty() && grantResults.all { it == PackageManager.PERMISSION_GRANTED })
        }
    }

    // 2026-10-22 (мобилка, админ): П6 — sent: часть чека уже ушла на принтер, Dart не повторяет (иначе второй чек).
    private class PrinterError(val code: String, message: String? = null, val sent: Boolean = false) : Exception(message)

    // ------------------------------------------------------------------ Bluetooth

    private fun isPrinterClass(d: BluetoothDevice): Boolean = try {
        val c = d.bluetoothClass
        c != null && (c.majorDeviceClass == BluetoothClass.Device.Major.IMAGING || c.majorDeviceClass == BluetoothClass.Device.Major.UNCATEGORIZED)
    } catch (e: SecurityException) {
        false
    }

    private fun deviceMap(d: BluetoothDevice, paired: Boolean): Map<String, Any?> = mapOf(
        "address" to d.address,
        "name" to ((try { d.name } catch (e: SecurityException) { null }) ?: ""),
        "paired" to paired,
        "printer" to isPrinterClass(d),
    )

    private fun btDevices(discover: Boolean, result: MethodChannel.Result) {
        val a = adapter() ?: return result.error("btUnsupported", null, null)
        if (!btPermitted(false)) return result.error("noPermission", null, null)
        if (!a.isEnabled) return result.error("btOff", null, null)
        val found = LinkedHashMap<String, Map<String, Any?>>()
        try {
            for (d in a.bondedDevices ?: emptySet()) found[d.address] = deviceMap(d, true)
        } catch (e: SecurityException) {
            return result.error("noPermission", e.message, null)
        }
        if (!discover || !btPermitted(true)) {
            result.success(found.values.toList())
            return
        }
        // поиск новых устройств ~8 с; принтер сопрягается сам при первой печати (Android спросит PIN)
        var done = false
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                if (i.action != BluetoothDevice.ACTION_FOUND) return
                val d: BluetoothDevice? = if (Build.VERSION.SDK_INT >= 33) {
                    i.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
                } else {
                    @Suppress("DEPRECATION") i.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
                }
                if (d != null && !found.containsKey(d.address)) found[d.address] = deviceMap(d, false)
            }
        }
        val filter = IntentFilter(BluetoothDevice.ACTION_FOUND)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED) else registerReceiver(receiver, filter)
        try {
            a.cancelDiscovery()
            a.startDiscovery()
        } catch (e: SecurityException) {
            unregisterReceiver(receiver)
            return result.error("noPermission", e.message, null)
        }
        main.postDelayed({
            if (done) return@postDelayed
            done = true
            try {
                a.cancelDiscovery()
            } catch (_: SecurityException) {
            }
            try {
                unregisterReceiver(receiver)
            } catch (_: Exception) {
            }
            result.success(found.values.toList())
        }, 8000)
    }

    private fun btPrint(a: BluetoothAdapter, address: String, data: ByteArray) {
        val device = try {
            a.getRemoteDevice(address.uppercase())
        } catch (e: IllegalArgumentException) {
            throw PrinterError("notFound", e.message)
        }
        if (device.type == BluetoothDevice.DEVICE_TYPE_LE) throw PrinterError("bleOnly")
        try {
            a.cancelDiscovery() // поиск сильно тормозит и рвёт подключение
        } catch (_: SecurityException) {
        }
        var socket: BluetoothSocket? = null
        var lastError: Exception? = null
        // 1) обычный SPP; 2) без шифрования — часть китайских принтеров; 3) канал 1 напрямую — старые модули HC-05
        val attempts = listOf<() -> BluetoothSocket>(
            { device.createRfcommSocketToServiceRecord(SPP) },
            { device.createInsecureRfcommSocketToServiceRecord(SPP) },
            { device.javaClass.getMethod("createRfcommSocket", Int::class.javaPrimitiveType).invoke(device, 1) as BluetoothSocket },
        )
        for (make in attempts) {
            try {
                val s = make()
                s.connect()
                socket = s
                break
            } catch (e: SecurityException) {
                throw PrinterError("noPermission", e.message)
            } catch (e: Exception) {
                lastError = e
            }
        }
        val s = socket ?: throw PrinterError("timeout", lastError?.message)
        var sentBytes = 0
        try {
            val out = s.outputStream
            // кусками — у дешёвых принтеров маленький буфер, иначе конец чека теряется
            var i = 0
            while (i < data.size) {
                val n = minOf(512, data.size - i)
                out.write(data, i, n)
                out.flush()
                i += n
                sentBytes = i
                Thread.sleep(20)
            }
            // дать принтеру допечатать перед закрытием сокета (закрытие сразу обрывает чек)
            Thread.sleep(400L + data.size / 20)
        } catch (e: IOException) {
            throw PrinterError("io", e.message, sent = sentBytes > 0)
        } finally {
            try {
                s.close()
            } catch (_: IOException) {
            }
        }
    }

    // ------------------------------------------------------------------ USB

    private fun usbName(d: UsbDevice): String {
        val n = listOfNotNull(d.manufacturerName, d.productName).joinToString(" ").trim()
        return if (n.isNotEmpty()) n else "USB %04X:%04X".format(d.vendorId, d.productId)
    }

    /// Интерфейс принтера (класс 7) или, у китайских принтеров с «своим» классом, любой интерфейс с выходом bulk.
    private fun printerInterface(d: UsbDevice): Pair<UsbInterface, UsbEndpoint>? {
        var fallback: Pair<UsbInterface, UsbEndpoint>? = null
        for (i in 0 until d.interfaceCount) {
            val itf = d.getInterface(i)
            for (e in 0 until itf.endpointCount) {
                val ep = itf.getEndpoint(e)
                if (ep.type == UsbConstants.USB_ENDPOINT_XFER_BULK && ep.direction == UsbConstants.USB_DIR_OUT) {
                    if (itf.interfaceClass == UsbConstants.USB_CLASS_PRINTER) return itf to ep
                    if (fallback == null && itf.interfaceClass != UsbConstants.USB_CLASS_MASS_STORAGE) fallback = itf to ep
                }
            }
        }
        return fallback
    }

    private fun usbPrint(name: String, data: ByteArray, result: MethodChannel.Result) {
        val m = getSystemService(Context.USB_SERVICE) as UsbManager
        // после переподключения кабеля имя /dev/bus/usb/… меняется — тогда берём первый подключённый принтер
        val d = m.deviceList[name] ?: m.deviceList.values.firstOrNull { printerInterface(it) != null }
            ?: return result.error("usbNotFound", null, null)
        if (m.hasPermission(d)) {
            usbWrite(m, d, data, result)
            return
        }
        // 2026-10-22 (мобилка, админ): П6 — окно разрешения уже открыто: второй чек не ставим в очередь к нему.
        if (usbPermissionPending) return result.error("usbDenied", "permission pending", null)
        usbPermissionPending = true
        // окно Android «Разрешить приложению доступ к USB-устройству?»
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                if (i.action != USB_PERMISSION) return
                usbPermissionPending = false
                try {
                    unregisterReceiver(this)
                } catch (_: Exception) {
                }
                if (i.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false) || m.hasPermission(d)) {
                    usbWrite(m, d, data, result)
                } else {
                    result.error("usbDenied", null, null)
                }
            }
        }
        val filter = IntentFilter(USB_PERMISSION)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED) else registerReceiver(receiver, filter)
        val flags = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) PendingIntent.FLAG_MUTABLE else 0
        val intent = Intent(USB_PERMISSION).setPackage(packageName)
        m.requestPermission(d, PendingIntent.getBroadcast(this, 0, intent, flags))
    }

    private fun usbWrite(m: UsbManager, d: UsbDevice, data: ByteArray, result: MethodChannel.Result) {
        val (itf, ep) = printerInterface(d) ?: return result.error("usbNotFound", "no bulk out", null)
        io.execute {
            val conn = m.openDevice(d)
            if (conn == null) {
                main.post { result.error("usbDenied", null, null) }
                return@execute
            }
            try {
                if (!conn.claimInterface(itf, true)) throw PrinterError("io", "claimInterface")
                var i = 0
                while (i < data.size) {
                    val n = minOf(4096, data.size - i)
                    val chunk = data.copyOfRange(i, i + n)
                    val sent = conn.bulkTransfer(ep, chunk, n, 5000)
                    if (sent < 0) throw PrinterError("timeout", null, sent = i > 0)
                    i += sent.coerceAtLeast(1)
                }
                main.post { result.success(null) }
            } catch (e: PrinterError) {
                main.post { result.error(e.code, e.message, if (e.sent) "sent" else null) }
            } catch (e: Exception) {
                main.post { result.error("io", e.message, null) }
            } finally {
                try {
                    conn.releaseInterface(itf)
                } catch (_: Exception) {
                }
                conn.close()
            }
        }
    }
}
