package br.com.brasizza.sunmi_printer_plus

import android.content.Context
import android.graphics.BitmapFactory
import android.os.Build
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import io.flutter.plugin.common.MethodChannel.MethodCallHandler


/** SunmiPrinterPlusPlugin */
class SunmiPrinterPlusPlugin : FlutterPlugin, MethodCallHandler {
    /// The MethodChannel that will the communication between Flutter and native Android
    ///
    /// This local reference serves to register the plugin with the Flutter Engine and unregister it
    /// when the Flutter Engine is detached from the Activity
    private lateinit var channel: MethodChannel
    private lateinit var sunmiPrinter: SunmiPrinterClass
    private lateinit var sunmiInit: SunmiInitClass
    private lateinit var configPrinter: SunmiConfigClass
    private lateinit var printerCommand: SunmiCommandPrinter
    private lateinit var sunmiLCD: SunmiLCDClass
    private lateinit var sunmiDrawer: SunmiDrawerClass
    // 2026-11-05 (ревью волн 0–3, М-8/М-9; правка NurCRM к пакету sunmi_printer_plus 4.2.0, BSD-3):
    //  • SDK Sunmi запускается ЛЕНИВО, при первой команде печати, и только если производитель — SUNMI. Оригинал стартовал SDK
    //    при каждом запуске движка (в том числе в фоновом изоляте WorkManager) на любом телефоне;
    //  • сбои доходят до приложения как PlatformException (result.error), а не строкой «error» / молчаливым «ok».
    private var ready = false
    private val mainHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private lateinit var appContext: Context

    override fun onAttachedToEngine(flutterPluginBinding: FlutterPlugin.FlutterPluginBinding) {
        appContext = flutterPluginBinding.getApplicationContext()
        sunmiInit = SunmiInitClass(appContext)
        channel = MethodChannel(flutterPluginBinding.binaryMessenger, "sunmi_printer_plus")
        channel.setMethodCallHandler(this)
    }

    private fun bind(selectPrinter: com.sunmi.printerx.PrinterSdk.Printer) {
        configPrinter = SunmiConfigClass(selectPrinter)
        sunmiPrinter = SunmiPrinterClass(selectPrinter)
        printerCommand = SunmiCommandPrinter(selectPrinter)
        sunmiLCD = SunmiLCDClass(selectPrinter)
        sunmiDrawer = SunmiDrawerClass(selectPrinter)
        ready = true
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        if (call.method == "getPlatformVersion") {
            result.success("Android ${Build.VERSION.RELEASE}")
            return
        }
        if (!Build.MANUFACTURER.contains("sunmi", ignoreCase = true)) {
            result.error("not_sunmi", "Это не аппарат Sunmi", null)
            return
        }
        if (ready && call.method != "rebindPrinter") {
            dispatch(call, result)
            return
        }
        sunmiInit.initPrinter { selectPrinter ->
            mainHandler.post {
                if (selectPrinter == null) {
                    ready = false
                    if (call.method == "rebindPrinter") result.success(false)
                    else result.error("no_printer", "Принтер Sunmi не найден или не ответил", null)
                } else {
                    val bound = try {
                        bind(selectPrinter)
                        true
                    } catch (e: Exception) {
                        ready = false
                        android.util.Log.e("sunmi_printer_plus", "Принтер Sunmi не подключился", e)
                        false
                    }
                    if (call.method == "rebindPrinter") result.success(bound)
                    else if (bound) dispatch(call, result)
                    else result.error("no_printer", "Принтер Sunmi недоступен", null)
                }
            }
        }
    }

    private fun dispatch(call: MethodCall, result: MethodChannel.Result) {
        try {
            dispatchInner(call, result)
        } catch (e: Exception) {
            android.util.Log.e("sunmi_printer_plus", "Ошибка команды ${call.method}", e)
            result.error("sunmi_error", e.message ?: e.toString(), null)
        }
    }

    private fun dispatchInner(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "getPlatformVersion" -> {
                result.success("Android ${Build.VERSION.RELEASE}")
            }

            "getStatus" -> {
                result.success(configPrinter.getStatus())
            }

            "getVersion" -> {
                result.success(configPrinter.getVersion())
            }

            "getPaper" -> {
                result.success(configPrinter.getPaper())
            }

            "getId" -> {
                result.success(configPrinter.getId())
            }

            "getType" -> result.success(configPrinter.getType())

            "printText" -> {
                val printerArgument: Map<String, Any>? = call.argument("data")
                result.success(sunmiPrinter.printText(printerArgument))
            }

            "printQrcode" -> {
                val qrcodeArgument: Map<String, Any>? = call.argument("data")
                result.success(sunmiPrinter.printQrcode(qrcodeArgument))
            }

            "printBarcode" -> {
                val qrcodeArgument: Map<String, Any>? = call.argument("data")
                result.success(sunmiPrinter.printBarcode(qrcodeArgument))
            }

            "printLine" -> {
                val lineArgument: String? = call.argument("data")
                result.success(sunmiPrinter.printLine(lineArgument))
            }

            "lineWrap" -> {
                val wrapArgument: Int = call.argument("times")?: 1
                result.success(sunmiPrinter.lineWrap(wrapArgument))
            }


            "cutPaper" -> {
                result.success(sunmiPrinter.cutPaper())
            }

            "printImage" -> {
                val bytes: ByteArray? = call.argument("image")
                val alignArgument: String? = call.argument("align")
                if(bytes != null){
                val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    result.success(sunmiPrinter.printImage(bitmap,alignArgument))
                }else{
                    result.success("invalid")
                }
            }


            "addText" -> {
                val printerArgument: Map<String, Any>? = call.argument("data")
                result.success(sunmiPrinter.addText(printerArgument))
            }

            "printEscPos" -> {
                val escArgument: ByteArray = call.argument<List<Int>>("data")!!
                    .map { it.toByte() }
                    .toByteArray()

                result.success(printerCommand.printEscPos(escArgument))
            }

            "printTSPL" -> {
                val tsplArgument: ByteArray = call.argument<String>("data")!!.toByteArray(Charsets.US_ASCII)
                result.success(printerCommand.printTSPL(tsplArgument))
            }


            "printRow" -> {
                val rowArguments: Map<String, Any> = call.argument("data")!!
                result.success(sunmiPrinter.printRow(rowArguments))
            }



            "configLCD" -> {
                val lcdStatus: String? = call.argument("status")
                result.success(sunmiLCD.configLCD(lcdStatus))
            }


            "sendTextLCD" -> {
                val textData: String = call.argument("text")?: ""
                val textSize: Int = call.argument("size")?: 32
                val textFill: Boolean = call.argument("fill")?: false
                result.success(sunmiLCD.sendTextLCD(textData,textSize,textFill))
            }

            "showDigital" -> {
                val digitalData: String = call.argument("digital")?: ""

                result.success(sunmiLCD.showDigital(digitalData))
            }

            "sendImageLCD" -> {
                val bytes: ByteArray? = call.argument("image")
                if(bytes != null) {
                    val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    result.success( sunmiLCD.sendImageLCD(bitmap))
                }
            }

            "openDrawer" -> {
                result.success(sunmiDrawer.openDrawer())
            }

            "isDrawerOpen" -> {
                result.success(sunmiDrawer.isDrawerOpen())
            }


            else -> {
                result.notImplemented()
            }
        }

    }


    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        channel.setMethodCallHandler(null)
    }
}
