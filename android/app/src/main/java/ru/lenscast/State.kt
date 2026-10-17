package ru.lenscast

import android.content.Context
import androidx.compose.runtime.mutableStateOf
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object State {
 val running = mutableStateOf(false)
 val status = mutableStateOf("Готов к подключению")
 val code = mutableStateOf("")
 val mode = mutableStateOf("Экран")
 val telemetry = mutableStateOf("Состояние телефона появится после запуска")
 @Synchronized fun log(c: Context, text: String, error: Throwable? = null) {
  val f = c.filesDir.resolve("журнал.txt")
  if (f.length() > 2_000_000) f.writeText("")
  f.appendText(SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.ROOT).format(Date()) + " " + text + (error?.let { "\n" + it.stackTraceToString() } ?: "") + "\n")
 }
}
