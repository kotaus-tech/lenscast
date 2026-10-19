package ru.lenscast

import android.app.*
import android.content.*
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.*
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.*
import android.util.DisplayMetrics
import android.view.Display
import android.view.Surface
import android.view.WindowManager
import java.nio.ByteBuffer
import kotlin.math.roundToInt

/** MediaProjection has its own foreground service; never opens camera/microphone. */
class ScreenService : Service() {
 companion object {
  @Volatile private var current: ScreenService?=null
  @Volatile private var preferredCodec=1
  fun preferCodec(value: Int) {
   if(value !in 1..2 || value==preferredCodec) return
   preferredCodec=value
   current?.let { service -> service.handler.post { if(!service.closed && service.sourceWidth>0) service.resize(service.sourceWidth,service.sourceHeight,true) } }
  }
 }
 private val worker=HandlerThread("Lenscast-экран")
 private lateinit var handler: Handler
 private var projection: MediaProjection?=null
 private var virtual: VirtualDisplay?=null
 private var encoder: MediaCodec?=null
 private var surface: Surface?=null
 private var codecId=1
 private var width=0; private var height=0
 private var sourceWidth=0; private var sourceHeight=0
 private var closed=false
 private var ownsCapture=false
 private val displays by lazy { getSystemService(DisplayManager::class.java) }
 private val projectionCallback=object: MediaProjection.Callback() {
  override fun onStop() { stopSelf() }
  override fun onCapturedContentResize(width: Int,height: Int) { resize(width,height) }
 }
 private val displayCallback=object: DisplayManager.DisplayListener {
  override fun onDisplayAdded(id: Int) {}
  override fun onDisplayRemoved(id: Int) {}
  override fun onDisplayChanged(id: Int) { if(id==Display.DEFAULT_DISPLAY) { val m=metrics(); resize(m.widthPixels,m.heightPixels) } }
 }
 override fun onBind(intent: Intent?)=null
 override fun onCreate() { super.onCreate(); worker.start(); handler=Handler(worker.looper); current=this }
 override fun onStartCommand(intent: Intent?,flags: Int,startId: Int): Int {
  if(intent?.action=="stop") { stopService(Intent(this,LinkService::class.java)); stopSelf(); return START_NOT_STICKY }
  if(projection!=null || State.screenRunning.value) return START_NOT_STICKY
  if(State.mode.value!="Экран" || !CaptureOwner.claim("screen")) { State.screenStatus.value="Дождитесь остановки камеры и повторите запуск экрана."; stopSelf(); return START_NOT_STICKY }; ownsCapture=true
  val manager=getSystemService(NotificationManager::class.java)
  manager.createNotificationChannel(NotificationChannel("screen","Показ экрана",NotificationManager.IMPORTANCE_LOW))
  val stop=PendingIntent.getService(this,2,Intent(this,ScreenService::class.java).setAction("stop"),PendingIntent.FLAG_IMMUTABLE)
  val open=PendingIntent.getActivity(this,0,Intent(this,MainActivity::class.java),PendingIntent.FLAG_IMMUTABLE)
  startForeground(2,Notification.Builder(this,"screen").setSmallIcon(android.R.drawable.stat_sys_upload)
   .setContentTitle("Lenscast · показ экрана").setContentText("Экран передаётся ПК · без звука")
   .setOngoing(true).setContentIntent(open).addAction(Notification.Action.Builder(null,"Стоп",stop).build()).build())
  @Suppress("DEPRECATION") val data=intent?.getParcelableExtra<Intent>("consent")
  if(data==null) { stopSelf(); return START_NOT_STICKY }
  State.screenRunning.value=true
  handler.post {
   try {
    projection=getSystemService(MediaProjectionManager::class.java).getMediaProjection(Activity.RESULT_OK,data)
    projection!!.registerCallback(projectionCallback,handler)
    displays.registerDisplayListener(displayCallback,handler)
    val m=metrics(); resize(m.widthPixels,m.heightPixels)
    VideoHub.requestKey={ handler.post { runCatching { encoder?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME,0) }) } } }
    handler.post(drain)
   } catch(e: Exception) { failed(e) }
  }
  return START_NOT_STICKY
 }
 private fun metrics(): DisplayMetrics {
  val bounds=getSystemService(WindowManager::class.java).maximumWindowMetrics.bounds
  return DisplayMetrics().also { it.widthPixels=bounds.width(); it.heightPixels=bounds.height(); it.densityDpi=resources.displayMetrics.densityDpi }
 }
 private fun resize(w: Int,h: Int,force: Boolean=false) {
  if(closed || w<=0 || h<=0 || (!force && w==sourceWidth && h==sourceHeight)) return
  try {
   GestureService.release()
   virtual?.surface=null
   releaseEncoder()
   VideoHub.clearFormat()
   val all=MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { it.isEncoder }
   val avc=all.filter { it.supportedTypes.any { t->t.equals(MediaFormat.MIMETYPE_VIDEO_AVC,true) } }.sortedBy { if(it.isHardwareAccelerated) 0 else 1 }
   val hevc=if(preferredCodec==2) all.filter { it.isHardwareAccelerated && it.supportedTypes.any { t->t.equals(MediaFormat.MIMETYPE_VIDEO_HEVC,true) } } else emptyList()
   val candidates=hevc.map { Pair(it,MediaFormat.MIMETYPE_VIDEO_HEVC) }+avc.map { Pair(it,MediaFormat.MIMETYPE_VIDEO_AVC) }
   var selected: MediaCodec?=null
   var selectedSurface: Surface?=null
   var last: Exception?=null
   // Prefer actual hardware codecs. Software is an explicit fallback, including CI AVD.
   outer@ for((info,mime) in candidates) {
    val caps=info.getCapabilitiesForType(mime).videoCapabilities
    for(edge in listOf(1920,1280,960,640)) {
     val scale=minOf(1.0,edge.toDouble()/maxOf(w,h))
     val cw=maxOf(caps.widthAlignment,((w*scale).roundToInt()/caps.widthAlignment)*caps.widthAlignment)
     val ch=maxOf(caps.heightAlignment,((h*scale).roundToInt()/caps.heightAlignment)*caps.heightAlignment)
     if(!caps.isSizeSupported(cw,ch) || !caps.areSizeAndRateSupported(cw,ch,30.0)) continue
     var c: MediaCodec?=null
     try {
      c=MediaCodec.createByCodecName(info.name)
      val format=MediaFormat.createVideoFormat(mime,cw,ch).apply {
       setInteger(MediaFormat.KEY_COLOR_FORMAT,MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
       setInteger(MediaFormat.KEY_BIT_RATE,caps.bitrateRange.clamp(6_000_000))
       setInteger(MediaFormat.KEY_FRAME_RATE,30); setInteger(MediaFormat.KEY_I_FRAME_INTERVAL,1)
       setInteger(MediaFormat.KEY_MAX_B_FRAMES,0)
       setInteger(MediaFormat.KEY_COLOR_STANDARD,MediaFormat.COLOR_STANDARD_BT709)
       setInteger(MediaFormat.KEY_COLOR_RANGE,MediaFormat.COLOR_RANGE_LIMITED)
       setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER,100_000L)
       setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER,30f)
      }
      c.configure(format,null,null,MediaCodec.CONFIGURE_FLAG_ENCODE)
      val s=c.createInputSurface()
      try { c.start() } catch(e: Exception) { s.release(); throw e }
      selected=c; selectedSurface=s; width=cw; height=ch; codecId=if(mime==MediaFormat.MIMETYPE_VIDEO_HEVC) 2 else 1
      State.log(this,"Экран: ${info.name}, ${cw}×$ch, аппаратный=${info.isHardwareAccelerated}")
      break@outer
     } catch(e: Exception) { last=e; c?.runCatching { release() } }
    }
   }
   encoder=selected ?: throw last ?: IllegalStateException("Нет поддерживаемого кодировщика H.264")
   surface=selectedSurface
   sourceWidth=w; sourceHeight=h
   if(virtual==null) {
    virtual=projection!!.createVirtualDisplay("Lenscast",width,height,metrics().densityDpi,
     DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,surface,null,handler)
   } else { virtual!!.resize(width,height,metrics().densityDpi); virtual!!.surface=surface }
   State.screenStatus.value="Экран передаётся · ${width}×$height · ${if(codecId==1) "H.264" else "H.265"}"
  } catch(e: Exception) { failed(e) }
 }
 private fun config(format: MediaFormat) {
  val data=java.io.ByteArrayOutputStream()
  java.io.DataOutputStream(data).apply { writeInt(width); writeInt(height) }
  for(key in listOf("csd-0","csd-1")) format.getByteBuffer(key)?.duplicate()?.let { b -> val raw=ByteArray(b.remaining()); b.get(raw); data.write(raw) }
  check(data.size()>8) { "Кодировщик не выдал заголовок H.264" }
  VideoHub.offer(VideoHub.Packet(0,0,data.toByteArray(),codecId))
 }
 private val drain=object: Runnable {
  override fun run() {
   if(closed) return
   try {
    val c=encoder ?: return
    val info=MediaCodec.BufferInfo()
    repeat(8) {
     val index=c.dequeueOutputBuffer(info,0)
     if(index==MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) config(c.outputFormat)
     else if(index>=0) {
      try {
       if(info.size>0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG==0) {
        val b=c.getOutputBuffer(index) ?: error("Нет данных кодировщика")
        b.position(info.offset); b.limit(info.offset+info.size)
        val bytes=ByteArray(info.size); b.get(bytes)
        VideoHub.offer(VideoHub.Packet(if(info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME!=0) 2 else 1,info.presentationTimeUs+(SystemClock.elapsedRealtimeNanos()-System.nanoTime())/1000,bytes,codecId))
       }
      } finally { c.releaseOutputBuffer(index,false) }
     } else return@repeat
    }
    handler.postDelayed(this,2)
   } catch(e: Exception) { failed(e) }
  }
 }
 private fun releaseEncoder() { encoder?.runCatching { stop() }; encoder?.runCatching { release() }; encoder=null; surface?.release(); surface=null }
 private fun failed(e: Exception) {
  State.screenStatus.value="Показ экрана остановлен. Нажмите «Разрешить показ экрана» ещё раз."
  State.log(this,"Не удалось передать экран",e); stopSelf()
 }
 override fun onDestroy() {
  current=null
  State.screenRunning.value=false
  if(State.screenStatus.value.startsWith("Экран передаётся")) State.screenStatus.value="Показ экрана остановлен. Нажмите «Разрешить показ экрана»."
  handler.post {
   closed=true; if(!ownsCapture) { worker.quitSafely(); return@post }; VideoHub.requestKey=null; VideoHub.stopCapture(); GestureService.release()
   displays.unregisterDisplayListener(displayCallback)
   virtual?.release(); virtual=null; releaseEncoder()
   projection?.unregisterCallback(projectionCallback); projection?.stop(); projection=null
   CaptureOwner.release("screen"); worker.quitSafely()
  }
  super.onDestroy()
 }
}
