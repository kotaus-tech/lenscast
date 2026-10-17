package ru.lenscast

import android.app.*
import android.content.*
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.*
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.json.JSONObject
import java.io.*
import java.math.BigInteger
import java.net.*
import java.security.*
import java.util.*
import java.util.concurrent.*
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.*
import javax.security.auth.x500.X500Principal

class LinkService : Service() {
 private val pool = Executors.newCachedThreadPool()
 private val alive = AtomicBoolean(false)
 private val occupied = AtomicBoolean(false)
 private val listeners = CopyOnWriteArrayList<ServerSocket>()
 private val clients = CopyOnWriteArrayList<Socket>()
 private var nsd: NsdManager? = null
 private var registration: NsdManager.RegistrationListener? = null
 private val random = SecureRandom()
 private var attempts = 0
 private var blockedUntil = 0L
 private var codeExpires = 0L
 private val prefs by lazy { getSharedPreferences("pairing", MODE_PRIVATE) }
 override fun onBind(intent: Intent?) = null
 override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
  if (intent?.action == "stop") { stopSelf(); return START_NOT_STICKY }
  if (!alive.compareAndSet(false, true)) return START_NOT_STICKY
  val nm = getSystemService(NotificationManager::class.java)
  nm.createNotificationChannel(NotificationChannel("link", "Подключение к ПК", NotificationManager.IMPORTANCE_LOW))
  val stop = PendingIntent.getService(this, 1, Intent(this, LinkService::class.java).setAction("stop"), PendingIntent.FLAG_IMMUTABLE)
  val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
  startForeground(1, Notification.Builder(this, "link").setSmallIcon(android.R.drawable.stat_sys_upload).setContentTitle("Lenscast · связь с ПК").setContentText("Ожидание подключения").setContentIntent(open).setOngoing(true).addAction(Notification.Action.Builder(null, "Стоп", stop).build()).build())
  State.running.value = true
  newCode()
  pool.execute {
   try {
    val context = tls()
    // TLS in LAN. Plain TCP is bound ONLY to loopback, reachable through authorized adb.
    listen(context.serverSocketFactory.createServerSocket(27183), false)
    listen(ServerSocket(27185, 4, InetAddress.getByName("127.0.0.1")), true)
    advertise()
    State.status.value = "Ожидание ПК · связь запущена"
    State.log(this, "Сервер связи запущен")
   } catch (e: Exception) { fail("Не удалось открыть связь. Нажмите «Старт» ещё раз.", e); stopSelf() }
  }
  return START_NOT_STICKY
 }
 private fun newCode() { State.code.value = "%06d".format(Locale.ROOT, random.nextInt(1_000_000)); codeExpires = SystemClock.elapsedRealtime() + 180_000 }
 private fun tls(): SSLContext {
  val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
  if (!ks.containsAlias("lenscast")) {
   val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_RSA, "AndroidKeyStore")
   generator.initialize(KeyGenParameterSpec.Builder("lenscast", KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
    .setKeySize(2048).setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA512)
    .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1)
    .setCertificateSubject(X500Principal("CN=Lenscast"))
    .setCertificateSerialNumber(BigInteger(128, random)).setCertificateNotBefore(Date(0))
    .setCertificateNotAfter(Date(4102444800000L)).build())
   generator.generateKeyPair()
  }
  val km = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(ks, null) }
  return SSLContext.getInstance("TLSv1.2").apply { init(km.keyManagers, null, random) }
 }
 private fun listen(server: ServerSocket, usb: Boolean) {
  listeners.add(server)
  pool.execute {
   while (alive.get()) try {
    val s = server.accept(); s.tcpNoDelay = true; s.soTimeout = 15_000
    clients.add(s)
    pool.execute { handle(s, usb) }
   } catch (e: Exception) { if (alive.get()) State.log(this, "Ошибка входящего соединения", e) }
  }
 }
 private fun read(input: DataInputStream): JSONObject {
  val n = input.readInt(); require(n in 2..65536) { "Недопустимый размер сообщения" }
  return JSONObject(String(ByteArray(n).also { input.readFully(it) }, Charsets.UTF_8))
 }
 private fun send(out: DataOutputStream, obj: JSONObject) { val b = obj.toString().toByteArray(Charsets.UTF_8); out.writeInt(b.size); out.write(b); out.flush() }
 private fun handle(s: Socket, usb: Boolean) {
  var owns = false
  try {
   val input = DataInputStream(s.getInputStream()); val output = DataOutputStream(s.getOutputStream())
   val hello = read(input)
   if (hello.optInt("version") != 1) { send(output, JSONObject().put("type", "error").put("message", "Обновите Lenscast на обоих устройствах.")); return }
   if (!usb && !authenticate(hello, output)) return
   if (!occupied.compareAndSet(false, true)) { send(output, JSONObject().put("type", "error").put("message", "Телефон уже подключён. Отключите другое соединение.")); return }
   owns = true
   val reply = JSONObject().put("type", "ready").put("version", 1).put("name", Build.MODEL).put("mode", State.mode.value)
   if (!usb && hello.optString("token").isEmpty()) {
    val token = ByteArray(32).also { random.nextBytes(it) }.joinToString("") { "%02x".format(it.toInt() and 255) }
    prefs.edit().putString("tokenHash", hash(token)).apply(); reply.put("token", token); newCode()
   }
   send(output, reply)
   State.status.value = if (usb) "ПК подключён · USB" else "ПК подключён · Wi-Fi"
   State.log(this, State.status.value)
   var nextTelemetry = 0L
   while (alive.get() && !s.isClosed) {
    if (SystemClock.elapsedRealtime() >= nextTelemetry) {
     val t = telemetry(usb); send(output, t); State.telemetry.value = "Заряд ${t.optInt("battery")}% · ${t.optDouble("temperature")} °C · ${t.optString("network")}"
     nextTelemetry = SystemClock.elapsedRealtime() + 1000
    }
    if (input.available() == 0) { Thread.sleep(25); continue }
    s.soTimeout = 5000
    // A timeout inside a partial frame closes the connection; never restart framing.
    run {
     val m = read(input)
     when (m.optString("type")) {
      "ping" -> send(output, JSONObject().put("type", "pong").put("sent", m.optLong("sent")).put("phoneUs", SystemClock.elapsedRealtimeNanos()/1000))
      "stop" -> { stopSelf(); return }
      "forget" -> { prefs.edit().remove("tokenHash").apply(); newCode(); return }
     }
    }
   }
  } catch (e: Exception) { if (alive.get()) State.log(this, "Соединение завершено", e) }
  finally { clients.remove(s); runCatching { s.close() }; if (owns) { occupied.set(false); State.status.value = "Связь прервана · ожидание ПК" } }
 }
 @Synchronized private fun authenticate(h: JSONObject, out: DataOutputStream): Boolean {
  val now = SystemClock.elapsedRealtime()
  if (now < blockedUntil) { send(out, JSONObject().put("type", "error").put("message", "Слишком много попыток. Подождите 30 секунд.")); return false }
  val token = h.optString("token")
  val expected = prefs.getString("tokenHash", "") ?: ""
  if (token.isNotEmpty() && expected.isNotEmpty() && MessageDigest.isEqual(hash(token).toByteArray(), expected.toByteArray())) return true
  if (now > codeExpires) newCode()
  val code = h.optString("code")
  if (code.isNotEmpty() && code == State.code.value) { attempts = 0; return true }
  attempts++
  if (attempts >= 5) { blockedUntil = now + 30_000; attempts = 0; newCode() }
  send(out, JSONObject().put("type", "pairing").put("message", "Введите актуальный код с экрана телефона.")); return false
 }
 private fun hash(s: String) = MessageDigest.getInstance("SHA-256").digest(s.toByteArray()).joinToString("") { "%02x".format(it.toInt() and 255) }
 private fun telemetry(usb: Boolean): JSONObject {
  val b = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
  val level = b?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
  val scale = b?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
  val status = b?.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
  @Suppress("DEPRECATION") val wifi = applicationContext.getSystemService(WIFI_SERVICE) as WifiManager
  @Suppress("DEPRECATION") val info = wifi.connectionInfo
  val band = if (usb) "USB" else when (info.frequency) { in 2400..2500 -> "Wi-Fi 2,4 ГГц"; in 4900..5900 -> "Wi-Fi 5 ГГц"; in 5925..7125 -> "Wi-Fi 6 ГГц"; else -> "Wi-Fi · диапазон недоступен" }
  return JSONObject().put("type", "telemetry").put("battery", if (level >= 0) level * 100 / scale.coerceAtLeast(1) else -1)
   .put("charging", status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL)
   .put("temperature", (b?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0)/10.0)
   .put("thermal", getSystemService(PowerManager::class.java).currentThermalStatus)
   .put("network", band).put("rssi", if (usb) 0 else info.rssi).put("fps", 0).put("bitrate", 0).put("dropped", 0)
 }
 private fun advertise() {
  nsd = getSystemService(NsdManager::class.java)
  val callback = object : NsdManager.RegistrationListener {
   override fun onServiceRegistered(info: NsdServiceInfo) { State.log(this@LinkService, "Обнаружение опубликовано") }
   override fun onRegistrationFailed(info: NsdServiceInfo, code: Int) { State.log(this@LinkService, "Обнаружение недоступно: $code") }
   override fun onServiceUnregistered(info: NsdServiceInfo) {}
   override fun onUnregistrationFailed(info: NsdServiceInfo, code: Int) {}
  }
  registration = callback
  nsd?.registerService(NsdServiceInfo().apply { serviceName = "Lenscast ${Build.MODEL}"; serviceType = "_lenscast._tcp."; port = 27183 }, NsdManager.PROTOCOL_DNS_SD, callback)
 }
 private fun fail(message: String, e: Exception) { State.status.value = message; State.log(this, message, e) }
 override fun onDestroy() {
  alive.set(false); listeners.forEach { runCatching { it.close() } }; clients.forEach { runCatching { it.close() } }
  registration?.let { runCatching { nsd?.unregisterService(it) } }; pool.shutdownNow()
  State.running.value = false; State.code.value = ""; State.status.value = "Связь остановлена"
  State.log(this, "Сервер остановлен"); super.onDestroy()
 }
}
