package ru.lenscast

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Intent
import android.graphics.Path
import android.hardware.display.DisplayManager
import android.os.Handler
import android.os.Looper
import android.util.DisplayMetrics
import android.view.Display
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import org.json.JSONObject

/** Only gestures/global navigation; no view hierarchy, text, passwords or key events. */
class GestureService: AccessibilityService() {
 companion object {
  @Volatile private var current: GestureService?=null
  private val main=Handler(Looper.getMainLooper())
  fun release() { main.post { current?.finish() } }
  fun command(message: JSONObject): Boolean {
   val service=current ?: return false
   main.post { try { service.accept(message) } catch(e: Exception) { service.down=false; service.stroke=null; service.busy=false; service.generation++; State.screenStatus.value="Жест не выполнен. Повторите его на телефоне."; State.log(service,"Жест прерван",e) } }; return true
  }
 }
 private var stroke: GestureDescription.StrokeDescription?=null
 private var down=false
 private var busy=false
 private var x=0f; private var y=0f
 private var nextX=0f; private var nextY=0f
 private var generation=0
 private var lastInput=0L
 override fun onServiceConnected() { current=this; State.gesturesReady.value=true }
 override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
 override fun onInterrupt() { finish() }
 override fun onUnbind(intent: Intent?): Boolean { finish(); current=null; State.gesturesReady.value=false; return super.onUnbind(intent) }
 private fun accept(m: JSONObject) {
  if(m.optString("type")=="navigation") {
   finish()
   val action=when(m.optString("action")) { "back"->GLOBAL_ACTION_BACK; "home"->GLOBAL_ACTION_HOME; "recents"->GLOBAL_ACTION_RECENTS; else->return }
   if(!performGlobalAction(action)) { State.screenStatus.value="Навигация недоступна. Выполните действие на телефоне."; State.log(this,"Android отклонил навигацию") }; return
  }
  val action=m.optString("action")
  lastInput=android.os.SystemClock.elapsedRealtime()
  if(action=="cancel") { finish(); return }
  val nx=m.optDouble("x",Double.NaN); val ny=m.optDouble("y",Double.NaN)
  if(!nx.isFinite() || !ny.isFinite() || nx !in 0.0..1.0 || ny !in 0.0..1.0) { finish(); return }
  val bounds=getSystemService(WindowManager::class.java).maximumWindowMetrics.bounds
  nextX=(nx*(bounds.width()-1)).toFloat(); nextY=(ny*(bounds.height()-1)).toFloat()
  when(action) {
   "down"->{ if(stroke!=null || busy) return; down=true; x=nextX; y=nextY; pump() }
   "move"->if(down && !busy) pump()
   "up"->{ down=false; if(!busy) pump() }
  }
 }
 private fun finish() { down=false; if(!busy) pump() }
 private fun pump() {
  if(down && android.os.SystemClock.elapsedRealtime()-lastInput>1500) down=false
  if(busy || (!down && stroke==null)) return
  val endX=nextX; val endY=nextY
  val path=Path().apply { moveTo(x,y); lineTo(endX,endY) }
  val next=stroke?.continueStroke(path,0,16,down) ?: GestureDescription.StrokeDescription(path,0,16,down)
  val epoch=generation
  busy=true
  val accepted=dispatchGesture(GestureDescription.Builder().addStroke(next).build(),object: GestureResultCallback() {
   override fun onCompleted(description: GestureDescription) {
    if(epoch!=generation) return
    busy=false; stroke=if(next.willContinue()) next else null; x=endX; y=endY
    if(down || stroke!=null) main.postDelayed({ pump() },8)
   }
   override fun onCancelled(description: GestureDescription) { generation++; busy=false; down=false; stroke=null }
  },main)
  if(!accepted) { generation++; busy=false; down=false; stroke=null }
 }
}
