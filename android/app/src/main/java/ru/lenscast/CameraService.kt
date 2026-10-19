package ru.lenscast

import android.app.*
import android.content.Intent
import android.graphics.Rect
import android.hardware.camera2.*
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.*
import android.os.*
import android.util.Range
import android.view.Surface
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Executor

/** Camera-only FGS; worker owns all device/codec/settings state. No audio or recording. */
class CameraService: Service() {
 companion object {
  @Volatile private var current: CameraService?=null
  fun command(message: JSONObject): Boolean {
   val service=current ?: return false
   service.handler.post { if(!service.closed) try { service.change(message) } catch(e: Exception) { service.notice(e) } }
   return true
  }
  fun preferCodec(codec: Int) { current?.let { s -> s.handler.post { if(!s.closed && codec in 1..2 && codec!=s.preferredCodec) { s.preferredCodec=codec; s.restart() } } } }
 }
 private data class Mode(val width: Int,val height: Int,val low: Int,val high: Int) {
  val id get()="${width}x${height}@$low-$high"
  fun json()=JSONObject().put("id",id).put("width",width).put("height",height).put("low",low).put("high",high)
 }
 private val worker=HandlerThread("Lenscast-камера")
 private lateinit var handler: Handler
 private val executor=Executor { handler.post(it) }
 private var closed=false; private var owns=false; private var generation=0
 private var inFlight=0; private var waitingOpen=false
 private var cameras=emptyList<CameraDescriptor>(); private lateinit var selected: CameraDescriptor
 private var modes=emptyList<Mode>(); private lateinit var mode: Mode
 private var settings=JSONObject(); private var rotation=0; private var mirror=false
 private var device: CameraDevice?=null; private var session: CameraCaptureSession?=null
 private var encoder: MediaCodec?=null; private var surface: Surface?=null
 private var wake: PowerManager.WakeLock?=null
 private var preferredCodec=1; private var codecId=1
 private var ready=false; private var captured=false; private var lastFrame=0L
 private var measuredAt=0L; private var capturedFrames=0; private var measuredFps=0.0
 private var exposureActual=0L; private var isoActual=0
 private val prefs by lazy { getSharedPreferences("camera",MODE_PRIVATE) }
 override fun onBind(intent: Intent?)=null
 override fun onCreate() { super.onCreate(); worker.start(); handler=Handler(worker.looper) }
 override fun onStartCommand(intent: Intent?,flags: Int,startId: Int): Int {
  if(intent?.action=="stop") { stopSelf(); return START_NOT_STICKY }
  if(owns) return START_NOT_STICKY
  if(State.mode.value!="Веб-камера" || !State.running.value || !CaptureOwner.claim("camera")) {
   State.cameraStatus.value="Остановите показ экрана и запустите связь в режиме «Веб-камера»."; stopSelf(); return START_NOT_STICKY
  }
  owns=true
  try {
   getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel("camera","Камера телефона",NotificationManager.IMPORTANCE_LOW))
   val open=PendingIntent.getActivity(this,0,Intent(this,MainActivity::class.java),PendingIntent.FLAG_IMMUTABLE)
   val stop=PendingIntent.getService(this,3,Intent(this,CameraService::class.java).setAction("stop"),PendingIntent.FLAG_IMMUTABLE)
   startForeground(3,Notification.Builder(this,"camera").setSmallIcon(android.R.drawable.stat_sys_upload).setContentTitle("Lenscast · камера телефона")
    .setContentText("Камера передаёт изображение · без звука").setOngoing(true).setContentIntent(open).addAction(Notification.Action.Builder(null,"Стоп",stop).build()).build())
   wake=getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"Lenscast:camera").apply { acquire() }
   State.cameraRunning.value=true
   handler.post {
    try {
     cameras=CameraCatalog.discover(this).filter { availableModes(it).isNotEmpty() }
     check(cameras.isNotEmpty()) { "Камеры недоступны. Разрешите камеру в настройках Lenscast." }
     choose(cameras.firstOrNull { it.id==prefs.getString("selected",null) } ?: cameras.firstOrNull { it.facing==CameraCharacteristics.LENS_FACING_BACK } ?: cameras.first())
     current=this
     VideoHub.requestKey={ handler.post { runCatching { encoder?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME,0) }) } } }
     restart(); handler.post(drain)
    } catch(e: Exception) { failed(e) }
   }
  } catch(e: Exception) { failed(e) }
  return START_NOT_STICKY
 }
 private fun availableModes(camera: CameraDescriptor): List<Mode> {
  val encoders=MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { it.isEncoder && it.supportedTypes.any { t->t.equals(MediaFormat.MIMETYPE_VIDEO_AVC,true) } }
  return camera.streams.flatMap { size ->
   camera.aeFpsRanges.filter { camera.mayTryFps(size,it.upper) }.map { Mode(size.width,size.height,it.lower,it.upper) }
  }.filter { m -> m.width<=4096 && m.height<=4096 && m.width%2==0 && m.height%2==0 && encoders.any { e ->
   try { e.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).videoCapabilities.areSizeAndRateSupported(m.width,m.height,m.high.toDouble()) } catch(_: Exception) { false }
  } }.distinct().sortedWith(compareByDescending<Mode> { it.width.toLong()*it.height }.thenByDescending { it.low==it.high }.thenByDescending { it.high }).take(200)
 }
 private fun choose(camera: CameraDescriptor) {
  val choices=availableModes(camera)
  require(choices.isNotEmpty()) { "Камера не имеет совместимого режима H.264. Выберите другую камеру." }
  val saved=runCatching { JSONObject(prefs.getString("camera:${camera.id}","{}")!!) }.getOrDefault(JSONObject())
  selected=camera; modes=choices
  mode=modes.firstOrNull { it.id==saved.optString("mode") } ?: modes.filter { maxOf(it.width,it.height)<=1920 && it.high<=30 }.firstOrNull() ?: modes.last()
  settings=JSONObject(); rotation=camera.orientation ?: 0; mirror=camera.facing==CameraCharacteristics.LENS_FACING_FRONT
  try {
   val old=saved.optJSONObject("controls") ?: JSONObject()
   validate(old); settings=old
   rotation=saved.optInt("rotation",rotation).takeIf { it in listOf(0,90,180,270) } ?: rotation
   mirror=saved.optBoolean("mirror",mirror)
  } catch(e: Exception) { State.log(this,"Сохранённые параметры камеры больше не поддерживаются. Используем автоматические.",e) }
 }
 private fun change(message: JSONObject) {
  if(message.optBoolean("reset")) { prefs.edit().remove("camera:${selected.id}").apply(); choose(selected); restart(); return }
  val id=message.optString("cameraId",selected.id)
  if(id!=selected.id) { choose(cameras.firstOrNull { it.id==id } ?: error("Камера недоступна. Обновите список камер.")); restart(); return }
  val newMode=modes.firstOrNull { it.id==message.optString("mode",mode.id) } ?: error("Режим недоступен. Выберите режим из списка.")
  val next=JSONObject(settings.toString())
  message.optJSONObject("controls")?.let { patch -> patch.keys().forEach { key -> next.put(key,patch.get(key)) } }
  // Enter manual exposure without jumping to the darkest sensor defaults.
  if(next.optBoolean("manualExposure",false)) {
   selected.controls.sensitivity?.let { range -> if(!next.has("iso")) next.put("iso",range.clamp(if(isoActual>0) isoActual else range.lower)) }
   selected.controls.exposureNanos?.let { range -> if(!next.has("exposureMs")) next.put("exposureMs",minOf(range.clamp(if(exposureActual>0) exposureActual else range.lower),1_000_000_000L/newMode.high)/1e6) }
  }
  validate(next,newMode)
  val newRotation=message.optInt("rotation",rotation); require(newRotation in listOf(0,90,180,270)) { "Выберите поворот 0, 90, 180 или 270°." }
  val before=settings; settings=next
  try {
   if(newMode!=mode) { mode=newMode; rotation=newRotation; mirror=message.optBoolean("mirror",mirror); restart(); return }
   if(session!=null) submit()
   rotation=newRotation; mirror=message.optBoolean("mirror",mirror)
   State.cameraStatus.value=if(ready) "Камера передаёт изображение" else "Подготавливаем камеру…"
   if(ready) save(); publish()
  } catch(e: Exception) { settings=before; runCatching { submit() }; throw e }
 }
 private fun validate(s: JSONObject,m: Mode=mode) {
  val c=selected.controls
  val allowed=setOf("zoom","focusMode","focus","ev","aeLock","awbMode","awbLock","torch","videoStabilization","opticalStabilization","manualExposure","iso","exposureMs")
  s.keys().forEach { require(it in allowed) { "Параметр недоступен. Обновите Lenscast на обоих устройствах." } }
  fun number(key: String,low: Double,high: Double,available: Boolean) {
   if(!s.has(key)) return
   val v=s.getDouble(key); require(available && v.isFinite() && v>=low && v<=high && (key !in setOf("iso","ev") || v==kotlin.math.floor(v))) { "Значение «$key» не поддерживается. Выберите значение из доступного диапазона." }
  }
  fun enumeration(key: String,values: List<Int>,request: CaptureRequest.Key<Int>) { if(s.has(key)) require(selected.supports(request) && s.getInt(key) in values) { "Режим недоступен. Выберите поддерживаемый режим." } }
  fun flag(key: String,available: Boolean) { if(s.has(key)) { s.getBoolean(key); require(available) { "Этот переключатель камерой не поддерживается. Сбросьте настройки камеры." } } }
  number("zoom",c.zoom?.lower?.toDouble() ?: 1.0,c.zoom?.upper?.toDouble() ?: 1.0,c.zoom!=null && (selected.supports(CaptureRequest.CONTROL_ZOOM_RATIO) || (c.activeArray!=null && selected.supports(CaptureRequest.SCALER_CROP_REGION))))
  enumeration("focusMode",c.afModes,CaptureRequest.CONTROL_AF_MODE)
  number("focus",0.0,c.minimumFocusDiopters?.toDouble() ?: 0.0,selected.supports(CaptureRequest.LENS_FOCUS_DISTANCE) && c.afModes.contains(CaptureRequest.CONTROL_AF_MODE_OFF))
  number("ev",c.compensation?.lower?.toDouble() ?: 0.0,c.compensation?.upper?.toDouble() ?: 0.0,selected.supports(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION) && c.compensation!=null)
  enumeration("awbMode",c.awbModes,CaptureRequest.CONTROL_AWB_MODE)
  enumeration("videoStabilization",c.videoStabilization,CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE)
  enumeration("opticalStabilization",c.opticalStabilization,CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE)
  flag("aeLock",c.aeLock && selected.supports(CaptureRequest.CONTROL_AE_LOCK)); flag("awbLock",c.awbLock && selected.supports(CaptureRequest.CONTROL_AWB_LOCK))
  flag("torch",c.flash && selected.supports(CaptureRequest.FLASH_MODE))
  val manual=c.aeModes.contains(CaptureRequest.CONTROL_AE_MODE_OFF) && selected.supports(CaptureRequest.CONTROL_AE_MODE) && selected.supports(CaptureRequest.SENSOR_EXPOSURE_TIME) && selected.supports(CaptureRequest.SENSOR_SENSITIVITY) && selected.supports(CaptureRequest.SENSOR_FRAME_DURATION) && c.sensitivity!=null && c.exposureNanos!=null
  flag("manualExposure",manual)
  number("iso",c.sensitivity?.lower?.toDouble() ?: 0.0,c.sensitivity?.upper?.toDouble() ?: 0.0,manual)
  number("exposureMs",(c.exposureNanos?.lower ?: 0)/1e6,minOf((c.exposureNanos?.upper ?: 0),1_000_000_000L/m.high)/1e6,manual)
  require(!(s.optInt("videoStabilization",0)!=0 && s.optInt("opticalStabilization",0)!=0)) { "Включите только один вид стабилизации." }
 }
 private fun restart() {
  if(closed) return
  generation++; measuredAt=0L; capturedFrames=0; measuredFps=0.0; ready=false; captured=false; waitingOpen=true; lastFrame=SystemClock.elapsedRealtime()
  session?.runCatching { close() }; session=null
  device?.close(); device=null
  releaseEncoder(); VideoHub.clearFormat()
  State.cameraStatus.value="Подготавливаем камеру…"; publish(); maybeOpen()
 }
 @Suppress("MissingPermission")
 private fun maybeOpen() {
  if(closed || !waitingOpen || inFlight!=0) return
  waitingOpen=false; val epoch=generation; inFlight++
  try { getSystemService(CameraManager::class.java).openCamera(selected.id,executor,object: CameraDevice.StateCallback() {
   override fun onOpened(camera: CameraDevice) {
    if(closed || epoch!=generation) { camera.close(); return }
    device=camera
    try { configure(camera,epoch) } catch(e: Exception) { failed(e) }
   }
   override fun onDisconnected(camera: CameraDevice) { camera.close(); if(!closed && epoch==generation) failed(IllegalStateException("Камера отключена системой")) }
   override fun onError(camera: CameraDevice,error: Int) { camera.close(); if(!closed && epoch==generation) failed(IllegalStateException("Android отклонил камеру: $error")) }
   override fun onClosed(camera: CameraDevice) { inFlight=(inFlight-1).coerceAtLeast(0); if(closed && inFlight==0) { CaptureOwner.release("camera"); worker.quitSafely() } else maybeOpen() }
  }) } catch(e: Exception) { inFlight--; failed(e) }
 }
 private fun configure(camera: CameraDevice,epoch: Int) {
  val all=MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { it.isEncoder }
  val candidates=(if(preferredCodec==2) all.filter { it.isHardwareAccelerated && it.supportedTypes.any { t->t.equals(MediaFormat.MIMETYPE_VIDEO_HEVC,true) } }.map { Pair(it,MediaFormat.MIMETYPE_VIDEO_HEVC) } else emptyList())+
   all.filter { it.supportedTypes.any { t->t.equals(MediaFormat.MIMETYPE_VIDEO_AVC,true) } }.sortedBy { !it.isHardwareAccelerated }.map { Pair(it,MediaFormat.MIMETYPE_VIDEO_AVC) }
  var last: Exception?=null
  for((info,mime) in candidates) {
   var c: MediaCodec?=null; var input: Surface?=null
   try {
    val caps=info.getCapabilitiesForType(mime).videoCapabilities
    if(!caps.areSizeAndRateSupported(mode.width,mode.height,mode.high.toDouble())) continue
    c=MediaCodec.createByCodecName(info.name)
    val format=MediaFormat.createVideoFormat(mime,mode.width,mode.height).apply {
     setInteger(MediaFormat.KEY_COLOR_FORMAT,MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
     setInteger(MediaFormat.KEY_BIT_RATE,caps.bitrateRange.clamp(8_000_000)); setInteger(MediaFormat.KEY_FRAME_RATE,mode.high)
     setInteger(MediaFormat.KEY_COLOR_STANDARD,MediaFormat.COLOR_STANDARD_BT709); setInteger(MediaFormat.KEY_COLOR_RANGE,MediaFormat.COLOR_RANGE_LIMITED)
     setInteger(MediaFormat.KEY_I_FRAME_INTERVAL,1); setInteger(MediaFormat.KEY_MAX_B_FRAMES,0)
    }
    c.configure(format,null,null,MediaCodec.CONFIGURE_FLAG_ENCODE); input=c.createInputSurface(); c.start()
    encoder=c; surface=input; codecId=if(mime==MediaFormat.MIMETYPE_VIDEO_HEVC) 2 else 1
    State.log(this,"Камера: ${info.name}, ${mode.width}×${mode.height}, аппаратный=${info.isHardwareAccelerated}")
    break
   } catch(e: Exception) { last=e; c?.runCatching { release() }; input?.release() }
  }
  check(encoder!=null) { "Нет совместимого кодировщика: ${last?.javaClass?.simpleName}" }
  val config=SessionConfiguration(SessionConfiguration.SESSION_REGULAR,listOf(OutputConfiguration(surface!!)),executor,object: CameraCaptureSession.StateCallback() {
   override fun onConfigured(value: CameraCaptureSession) {
    if(closed || epoch!=generation) { value.close(); return }
    session=value
    try { submit() } catch(e: Exception) { failed(e) }
   }
   override fun onConfigureFailed(value: CameraCaptureSession) { value.close(); if(!closed && epoch==generation) failed(IllegalStateException("Камера отклонила сочетание разрешения и FPS")) }
  })
  // Some LEGACY HALs throw UnsupportedOperationException for the query. The actual
  // session and first capture/encoded frame remain the mandatory runtime proof.
  val supported=try { camera.isSessionConfigurationSupported(config) } catch(_: UnsupportedOperationException) { true }
  check(supported) { "Камера не поддерживает эту сессию" }
  camera.createCaptureSession(config)
 }
 private fun submit() {
  val camera=device ?: return; val capture=session ?: return; val c=selected.controls; val s=settings
  val builder=camera.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply { addTarget(surface!!) }
  fun <T> set(key: CaptureRequest.Key<T>,value: T) { if(selected.supports(key)) builder.set(key,value) }
  set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,Range(mode.low,mode.high))
  val manual=s.optBoolean("manualExposure",false)
  set(CaptureRequest.CONTROL_AE_MODE,if(manual) CaptureRequest.CONTROL_AE_MODE_OFF else CaptureRequest.CONTROL_AE_MODE_ON)
  if(manual) {
   set(CaptureRequest.SENSOR_SENSITIVITY,s.optInt("iso",c.sensitivity!!.lower))
   set(CaptureRequest.SENSOR_EXPOSURE_TIME,(s.optDouble("exposureMs",c.exposureNanos!!.lower/1e6)*1e6).toLong())
   set(CaptureRequest.SENSOR_FRAME_DURATION,1_000_000_000L/mode.high)
  }
  val af=s.optInt("focusMode",if(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO in c.afModes) CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO else c.afModes.firstOrNull() ?: 0)
  set(CaptureRequest.CONTROL_AF_MODE,af)
  if(af==CaptureRequest.CONTROL_AF_MODE_OFF && s.has("focus")) set(CaptureRequest.LENS_FOCUS_DISTANCE,s.getDouble("focus").toFloat())
  if(s.has("zoom")) {
   val zoom=s.getDouble("zoom").toFloat()
   if(selected.supports(CaptureRequest.CONTROL_ZOOM_RATIO)) set(CaptureRequest.CONTROL_ZOOM_RATIO,zoom)
   else c.activeArray?.let { a -> val w=(a.width()/zoom).toInt(); val h=(a.height()/zoom).toInt(); val left=a.left+(a.width()-w)/2; val top=a.top+(a.height()-h)/2; set(CaptureRequest.SCALER_CROP_REGION,Rect(left,top,left+w,top+h)) }
  }
  if(s.has("ev") && !manual) set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION,s.getInt("ev"))
  if(c.aeLock && !manual) set(CaptureRequest.CONTROL_AE_LOCK,s.optBoolean("aeLock",false))
  if(s.has("awbMode")) set(CaptureRequest.CONTROL_AWB_MODE,s.getInt("awbMode"))
  if(c.awbLock) set(CaptureRequest.CONTROL_AWB_LOCK,s.optBoolean("awbLock",false))
  if(c.flash) set(CaptureRequest.FLASH_MODE,if(s.optBoolean("torch",false)) CaptureRequest.FLASH_MODE_TORCH else CaptureRequest.FLASH_MODE_OFF)
  if(s.optInt("videoStabilization",0)!=0 && CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF in c.opticalStabilization) set(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE,CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF)
  if(s.optInt("opticalStabilization",0)!=0 && CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF in c.videoStabilization) set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
  if(s.has("videoStabilization")) set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,s.getInt("videoStabilization"))
  if(s.has("opticalStabilization")) set(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE,s.getInt("opticalStabilization"))
  val epoch=generation
  capture.setRepeatingRequest(builder.build(),object: CameraCaptureSession.CaptureCallback() {
   override fun onCaptureCompleted(session: CameraCaptureSession,request: CaptureRequest,result: TotalCaptureResult) {
    if(closed || epoch!=generation) return
    captured=true; capturedFrames++
    exposureActual=result.get(CaptureResult.SENSOR_EXPOSURE_TIME) ?: 0; isoActual=result.get(CaptureResult.SENSOR_SENSITIVITY) ?: 0
    val now=SystemClock.elapsedRealtime()
    if(now-measuredAt>=1000) { measuredFps=if(measuredAt==0L) 0.0 else capturedFrames*1000.0/(now-measuredAt); measuredAt=now; capturedFrames=0; publish() }
   }
  },handler)
  if(af==CaptureRequest.CONTROL_AF_MODE_AUTO && selected.supports(CaptureRequest.CONTROL_AF_TRIGGER)) {
   builder.set(CaptureRequest.CONTROL_AF_TRIGGER,CaptureRequest.CONTROL_AF_TRIGGER_START); capture.capture(builder.build(),null,handler)
  }
 }
 private fun save() { prefs.edit().putString("selected",selected.id).putString("camera:${selected.id}",JSONObject().put("mode",mode.id).put("controls",settings).put("rotation",rotation).put("mirror",mirror).toString()).apply() }
 private fun publish() {
  if(!::selected.isInitialized) return
  val c=selected.controls
  val controls=JSONArray()
  fun add(key: String,label: String,request: CaptureRequest.Key<*>,low: Number?=null,high: Number?=null,step: Number=1,values: List<Int>?=null) {
   if(!selected.supports(request)) return
   if(values!=null && values.isEmpty()) return
   val item=JSONObject().put("key",key).put("label",label)
   if(values!=null) item.put("values",JSONArray(values)) else if(low!=null && high!=null) item.put("min",low).put("max",high).put("step",step) else item.put("boolean",true)
   if(settings.has(key)) item.put("value",settings.get(key)) else if(low==null && values==null) item.put("value",false)
   controls.put(item)
  }
  c.zoom?.takeIf { it.upper>it.lower }?.let { if(selected.supports(CaptureRequest.CONTROL_ZOOM_RATIO) || c.activeArray!=null) add("zoom","Масштаб",if(selected.supports(CaptureRequest.CONTROL_ZOOM_RATIO)) CaptureRequest.CONTROL_ZOOM_RATIO else CaptureRequest.SCALER_CROP_REGION,it.lower,it.upper,0.1) }
  add("focusMode","Фокус",CaptureRequest.CONTROL_AF_MODE,values=c.afModes)
  if(c.afModes.contains(CaptureRequest.CONTROL_AF_MODE_OFF)) c.minimumFocusDiopters?.takeIf { it>0 }?.let { add("focus","Ручной фокус · диоптрии",CaptureRequest.LENS_FOCUS_DISTANCE,0,it,0.01) }
  c.compensation?.takeIf { it.upper>it.lower }?.let { add("ev","Экспокоррекция · шаг ${c.compensationStep}",CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION,it.lower,it.upper) }
  if(c.aeLock) add("aeLock","Блокировка автоэкспозиции",CaptureRequest.CONTROL_AE_LOCK)
  add("awbMode","Баланс белого",CaptureRequest.CONTROL_AWB_MODE,values=c.awbModes)
  if(c.awbLock) add("awbLock","Блокировка баланса белого",CaptureRequest.CONTROL_AWB_LOCK)
  if(c.flash) add("torch","Фонарик",CaptureRequest.FLASH_MODE)
  if(c.videoStabilization.any { it!=0 }) add("videoStabilization","Электронная стабилизация",CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,values=c.videoStabilization)
  if(c.opticalStabilization.any { it!=0 }) add("opticalStabilization","Оптическая стабилизация",CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE,values=c.opticalStabilization)
  if(c.aeModes.contains(CaptureRequest.CONTROL_AE_MODE_OFF) && c.sensitivity!=null && c.exposureNanos!=null && selected.supports(CaptureRequest.SENSOR_SENSITIVITY) && selected.supports(CaptureRequest.SENSOR_EXPOSURE_TIME) && selected.supports(CaptureRequest.SENSOR_FRAME_DURATION)) {
   add("manualExposure","Ручная экспозиция",CaptureRequest.CONTROL_AE_MODE)
   add("iso","Светочувствительность · ISO",CaptureRequest.SENSOR_SENSITIVITY,c.sensitivity.lower,c.sensitivity.upper)
   add("exposureMs","Выдержка · мс",CaptureRequest.SENSOR_EXPOSURE_TIME,c.exposureNanos.lower/1e6,minOf(c.exposureNanos.upper,1_000_000_000L/mode.high)/1e6,0.001)
  }
  val json=JSONObject().put("running",State.cameraRunning.value).put("ready",ready).put("status",State.cameraStatus.value)
   .put("cameraId",selected.id).put("mode",mode.id).put("rotation",rotation).put("mirror",mirror).put("actualFps",measuredFps)
   .put("actualIso",isoActual).put("actualExposureMs",exposureActual/1e6).put("codec",codecId)
   .put("cameras",JSONArray(cameras.map { JSONObject().put("id",it.id).put("label",(when(it.facing) { CameraCharacteristics.LENS_FACING_BACK->"Задняя"; CameraCharacteristics.LENS_FACING_FRONT->"Фронтальная"; else->"Внешняя" })+" · ${it.focalLengths.joinToString()} мм · модуль ${it.id}") }))
   .put("modes",JSONArray(modes.map { it.json() })).put("controls",controls)
  State.cameraJson.value=json.toString()
 }
 private val drain=object: Runnable {
  override fun run() {
   if(closed) return
   try {
    encoder?.let { c ->
     val info=MediaCodec.BufferInfo()
     repeat(8) {
      val index=c.dequeueOutputBuffer(info,0)
      if(index==MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
       val data=java.io.ByteArrayOutputStream(); java.io.DataOutputStream(data).apply { writeInt(mode.width); writeInt(mode.height) }
       for(key in listOf("csd-0","csd-1")) c.outputFormat.getByteBuffer(key)?.duplicate()?.let { b -> val bytes=ByteArray(b.remaining()); b.get(bytes); data.write(bytes) }
       check(data.size()>8); VideoHub.offer(VideoHub.Packet(0,0,data.toByteArray(),codecId))
      } else if(index>=0) {
       try {
        if(info.size>0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG==0) {
         val b=c.getOutputBuffer(index) ?: error("Нет кадра камеры"); b.position(info.offset); b.limit(info.offset+info.size)
         val bytes=ByteArray(info.size); b.get(bytes)
         // Camera sensor/codec timestamps vary by HAL; network presentation uses
         // monotonic arrival time, not an invented cross-clock sensor conversion.
         VideoHub.offer(VideoHub.Packet(if(info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME!=0) 2 else 1,SystemClock.elapsedRealtimeNanos()/1000,bytes,codecId))
         lastFrame=SystemClock.elapsedRealtime()
         if(!ready && captured && info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME!=0) { ready=true; State.cameraStatus.value="Камера передаёт изображение"; save(); publish() }
        }
       } finally { c.releaseOutputBuffer(index,false) }
      }
     }
    }
    if(SystemClock.elapsedRealtime()-lastFrame>10_000) { failed(IllegalStateException("Нет кадров камеры")); return }
    handler.postDelayed(this,2)
   } catch(e: Exception) { failed(e) }
  }
 }
 private fun notice(e: Exception) { State.cameraStatus.value="Настройка не применена. Выберите другое значение или нажмите «Сбросить»."; State.log(this,"Параметр камеры отклонён",e); publish() }
 private fun failed(e: Exception) { State.cameraStatus.value="Камера остановлена. Откройте Lenscast и запустите камеру; если блокировка мешает, используйте «Чёрный экран»."; State.log(this,"Передача камеры завершена",e); publish(); stopSelf() }
 private fun releaseEncoder() { encoder?.runCatching { stop() }; encoder?.runCatching { release() }; encoder=null; surface?.release(); surface=null }
 override fun onDestroy() {
  if(current===this) current=null
  State.cameraRunning.value=false
  handler.post {
   closed=true; generation++; waitingOpen=false
   if(owns) {
    ready=false; session?.runCatching { close() }; session=null; device?.close(); device=null; releaseEncoder()
    VideoHub.requestKey=null; VideoHub.stopCapture(); wake?.let { if(it.isHeld) it.release() }; wake=null
    if(State.cameraStatus.value=="Камера передаёт изображение") State.cameraStatus.value="Камера остановлена. Нажмите «Запустить камеру»."
    publish()
   }
   if(inFlight==0) { if(owns) CaptureOwner.release("camera"); worker.quitSafely() }
  }
  super.onDestroy()
 }
}
