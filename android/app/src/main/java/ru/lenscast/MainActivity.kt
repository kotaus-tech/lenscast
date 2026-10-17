package ru.lenscast

import android.Manifest
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider

class MainActivity : ComponentActivity() {
 private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
  State.status.value = if (granted) "Уведомления разрешены" else "Разрешите уведомления в настройках, чтобы видеть кнопку «Стоп»."
 }
 override fun onCreate(savedInstanceState: Bundle?) {
  super.onCreate(savedInstanceState)
  setContent {
   MaterialTheme(colorScheme = darkColorScheme(primary=Color(0xFF8F7CF0), background=Color(0xFF0E0E11), surface=Color(0xFF17171C), onSurface=Color(0xFFECECF1))) {
    var settings by remember { mutableStateOf(false) }
    Surface(Modifier.fillMaxSize()) {
     Column(Modifier.statusBarsPadding().navigationBarsPadding().verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement=Arrangement.spacedBy(16.dp)) {
      Row(Modifier.fillMaxWidth(), horizontalArrangement=Arrangement.SpaceBetween) {
       Text("Lenscast", fontSize=30.sp)
       TextButton(onClick={ settings=!settings }) { Text(if(settings) "На главную" else "Настройки") }
      }
      Text("Телефон рядом. Картинка — перед вами.", color=Color(0xFF9A9AA8))
      if(settings) {
       Panel("Подготовка телефона") {
        Text("Разрешения запрашиваются по одному. Камера и микрофон для связи не нужны.")
        Button(onClick={ if(Build.VERSION.SDK_INT>=33) notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS) }, Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Разрешить уведомления") }
        OutlinedButton(onClick={ safeOpen(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }, Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Работа в фоне") }
        Text("${Build.MANUFACTURER}: разрешите автозапуск и закрепите Lenscast в недавних приложениях. Эти настройки зависят от прошивки.")
        OutlinedButton(onClick={ safeOpen(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) }, Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Настройки приложения") }
       }
       Panel("Подключение по USB") { Text("Включите режим разработчика: нажмите 7 раз на номер сборки в сведениях о телефоне. Включите «Отладка по USB», подключите кабель и подтвердите доверие этому ПК.") }
       Panel("Журнал") { Text("Журнал не содержит коды сопряжения и токены."); OutlinedButton(onClick={ shareLog() }) { Text("Сохранить журнал") } }
      } else {
       Text("Выберите режим", fontSize=22.sp)
       listOf("Экран", "Веб-камера").forEach { mode ->
        Card(onClick={ if(!State.running.value) State.mode.value=mode }, colors=CardDefaults.cardColors(containerColor=if(State.mode.value==mode) Color(0xFF292339) else Color(0xFF17171C))) {
         Column(Modifier.fillMaxWidth().padding(24.dp), verticalArrangement=Arrangement.spacedBy(8.dp)) {
          Text((if(State.mode.value==mode) "●  " else "○  ")+mode, fontSize=22.sp)
          Text(if(mode=="Экран") "Смотрите и управляйте телефоном с ПК" else "Камера телефона для приложений Windows", color=Color(0xFF9A9AA8))
         }
        }
       }
       Panel("Подключение") {
        Text(State.status.value)
        if(State.running.value) {
         Text("Код для первого подключения", color=Color(0xFF9A9AA8))
         Text(State.code.value, fontSize=40.sp, letterSpacing=6.sp)
         Text("Код действует 3 минуты. ПК и телефон должны быть в одной сети.")
        }
       }
       Button(onClick={
        val intent=Intent(this@MainActivity, LinkService::class.java)
        if(State.running.value) stopService(intent) else startForegroundService(intent)
       }, Modifier.fillMaxWidth().heightIn(min=56.dp)) { Text(if(State.running.value) "Стоп" else "Старт") }
       Text("Сейчас доступна связь с ПК. Передача изображения появится в следующих блоках.", color=Color(0xFFF0B866))
       Panel("Состояние телефона") { Text(State.telemetry.value) }
      }
     }
    }
   }
  }
 }
 @Composable private fun Panel(title: String, content: @Composable ColumnScope.()->Unit) {
  Card { Column(Modifier.fillMaxWidth().padding(20.dp), verticalArrangement=Arrangement.spacedBy(12.dp)) { Text(title, fontSize=18.sp); content() } }
 }
 private fun safeOpen(intent: Intent) { try { startActivity(intent) } catch(e: Exception) { State.status.value="Настройки недоступны. Откройте их вручную."; State.log(this,"Не удалось открыть настройки",e) } }
 private fun shareLog() {
  State.log(this,"Экспорт журнала")
  try {
   val uri=FileProvider.getUriForFile(this,"ru.lenscast.files",filesDir.resolve("журнал.txt"))
   startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_STREAM,uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),"Сохранить журнал"))
  } catch(e: Exception) { State.status.value="Не удалось сохранить журнал. Повторите попытку."; State.log(this,"Ошибка экспорта",e) }
 }
}
