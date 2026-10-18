package ru.lenscast

import android.content.Context
import androidx.compose.runtime.mutableStateOf
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object State {
 val screenRunning = mutableStateOf(false)
 val screenStatus = mutableStateOf("Для показа экрана нужно разрешение Android")
 val gesturesReady = mutableStateOf(false)
 val address = mutableStateOf("")
 val discoveryWarning = mutableStateOf("")
 val wifiReady = mutableStateOf(false)
 val running = mutableStateOf(false)
 val status = mutableStateOf("Готов к подключению")
 val notificationsAllowed = mutableStateOf(false)
 val backgroundAllowed = mutableStateOf(false)
 val code = mutableStateOf("")
 val mode = mutableStateOf("Экран")
 val telemetry = mutableStateOf("Состояние телефона появится после запуска")
 @Synchronized fun log(c: Context, text: String, error: Throwable? = null) {
  if(error==null) android.util.Log.i("Lenscast",text) else android.util.Log.e("Lenscast",text,error)
  runCatching {
  val f = c.filesDir.resolve("журнал.txt")
  if (f.length() > 2_000_000) f.writeText("")
  f.appendText(SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.ROOT).format(Date()) + " " + text + (error?.let { "\n" + it.stackTraceToString() } ?: "") + "\n")
  }
 }
}
